using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalAspectRatio.Providers;

/// <summary>
/// Original aspect ratio metadata provider.
/// </summary>
public class OriginalAspectRatioMetadataProvider :
    ICustomMetadataProvider<Movie>,
    ICustomMetadataProvider<Episode>
{
    private readonly IBlurayExaminer _blurayExaminer;
    private readonly ItemUpdateType _cachedTask = ItemUpdateType.None;
    private readonly ILogger<OriginalAspectRatioMetadataProvider> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="OriginalAspectRatioMetadataProvider "/> class.
    /// </summary>
    /// <param name="mediaEncoder">A MediaEncoder.</param>
    /// <param name="mediaSourceManager">A MediaSourceManager.</param>
    /// <param name="blurayExaminer">A BlurayExaminer.</param>
    /// <param name="logger">A Logger.</param>
    public OriginalAspectRatioMetadataProvider(
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        IBlurayExaminer blurayExaminer,
        ILogger<OriginalAspectRatioMetadataProvider> logger)
    {
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _blurayExaminer = blurayExaminer;
        _logger = logger;
    }

    private static int ApectRatioDetectChecksPerVideo => OriginalAspectRatioPlugin.Instance?.Configuration.ApectRatioDetectChecksPerVideo ?? 10;

    private static Collection<string> AspectRatios => OriginalAspectRatioPlugin.Instance?.Configuration.AspectRatios ?? new Collection<string>();

    private static bool ShouldAlwaysWriteOriginalAspectRatio => OriginalAspectRatioPlugin.Instance?.Configuration.ShouldAlwaysWriteOriginalAspectRatio ?? false;

    private static bool ShouldOverrideExistingAspectRatio => OriginalAspectRatioPlugin.Instance?.Configuration.ShouldOverrideExistingAspectRatio ?? false;

    /// <inheritdoc/>
    public string Name => GetType().Name;

    /// <inheritdoc/>
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken) => FetchAsync((Video)item, options, cancellationToken);

    /// <inheritdoc/>
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken) => FetchAsync((Video)item, options, cancellationToken);

    private static decimal? AspectRatioToDecimal(string aspectRatio)
    {
        aspectRatio = aspectRatio.Replace(" ", string.Empty, StringComparison.InvariantCultureIgnoreCase);

        if (aspectRatio.Contains(':', StringComparison.InvariantCultureIgnoreCase))
        {
            var tokens = aspectRatio.Split(':');
            decimal? width = decimal.TryParse(tokens?[0], out var tempHeight) ? tempHeight : null;
            decimal? height = decimal.TryParse(tokens?[1], out var tempWidth) ? tempWidth : null;

            if (!width.HasValue || !height.HasValue)
            {
                return null;
            }

            return width.Value / height.Value;
        }

        return decimal.TryParse(aspectRatio, out var tempAspectRatio) ? tempAspectRatio : (decimal?)null;
    }

    private async Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!(item is Video videoItem) || !(item is IHasAspectRatio aspectRatioItem))
        {
            _logger.LogDebug("Item {0} is not a Video or does not support original aspect ratio, skipping.", item.Name);
            return _cachedTask;
        }

        if (!string.IsNullOrWhiteSpace(aspectRatioItem.AspectRatio) && !ShouldOverrideExistingAspectRatio)
        {
            _logger.LogInformation("Item {0} already has an original aspect ratio defined, skipping.", item.Name);
            return _cachedTask;
        }

        var aspectRatios = AspectRatios
            .ToDictionary(
                (ar) => ar,
                AspectRatioToDecimal)
            .Where(kvp => kvp.Value.HasValue)
            .ToDictionary(
                (kvp) => kvp.Key,
                (kvp) => kvp.Value!.Value);

        var mediaInfo = default(MediaInfo);

        switch (videoItem.VideoType)
        {
            case VideoType.Dvd:
                // Get list of playable .vob files
                var vobs = _mediaEncoder.GetPrimaryPlaylistVobFiles(item.Path, null).ToList();

                // Return if no playable .vob files are found
                if (vobs.Count == 0)
                {
                    _logger.LogError("No playable .vob files found in DVD structure, skipping FFprobe.");
                    return _cachedTask;
                }

                // Fetch metadata of first .vob file
                mediaInfo = await GetMediaInfo(
                    new Video
                    {
                        Path = vobs[0]
                    },
                    cancellationToken).ConfigureAwait(false);
                break;

            case VideoType.BluRay:
                // Get BD disc information
                var blurayDiscInfo = GetBDInfo(item.Path);

                // Get playable .m2ts files
                var m2ts = _mediaEncoder.GetPrimaryPlaylistM2tsFiles(item.Path);

                // Return if no playable .m2ts files are found
                if (blurayDiscInfo is null || blurayDiscInfo.Files.Length == 0 || m2ts.Count == 0)
                {
                    _logger.LogError("No playable .m2ts files found in Blu-ray structure, skipping FFprobe.");
                    return _cachedTask;
                }

                // Fetch metadata of first .m2ts file
                mediaInfo = await GetMediaInfo(
                    new Video
                    {
                        Path = m2ts[0]
                    },
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                mediaInfo = await GetMediaInfo(videoItem, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (mediaInfo == default(MediaInfo))
        {
            _logger.LogError("Unable to retrieve MediaInfo for item {0}, skipping.", item.Name);
            return _cachedTask;
        }

        if (!videoItem.RunTimeTicks.HasValue || videoItem.RunTimeTicks <= 0)
        {
            _logger.LogInformation("Unable to determine runtime for {0} and therefore cannot detect aspect ratio.", item.Name);
            return _cachedTask;
        }

        var originalAspectRatio = await DetectOriginalAspectRatio(mediaInfo.Path, videoItem.RunTimeTicks!.Value, cancellationToken).ConfigureAwait(false);

        if (!originalAspectRatio.HasValue)
        {
            _logger.LogWarning("Failed to determine original aspect ration for {0}.", item.Name);
            return _cachedTask;
        }

        _logger.LogInformation("Discovered original aspect ratio value of {0} for {1}", originalAspectRatio, item.Name);

        var videoStreamAspectRatio = videoItem.GetDefaultVideoStream().AspectRatio;
        var videoStreamAspectRatioValue = AspectRatioToDecimal(videoItem.GetDefaultVideoStream().AspectRatio);
        _logger.LogDebug("Converted video stream aspect ratio of {0} to {1} for {2}", videoStreamAspectRatio, videoStreamAspectRatioValue, item.Name);

        if (!videoStreamAspectRatioValue.HasValue)
        {
            _logger.LogWarning("Failed to convert video stream aspect ratio for {0}.", item.Name);
            return _cachedTask;
        }

        var acceptedOriginalAspectRatio = aspectRatios.OrderBy(ar => Math.Abs(originalAspectRatio.Value - ar.Value)).First().Key;
        _logger.LogInformation("Matched discovered original aspect ratio of {0} to accepted original aspect ratios value of {1} for {2}.", originalAspectRatio.Value, acceptedOriginalAspectRatio, item.Name);

        var acceptedOriginalAspectRatioValue = AspectRatioToDecimal(acceptedOriginalAspectRatio);
        _logger.LogDebug("Converted accepted original aspect ratio of {0} to {1} for {2}", acceptedOriginalAspectRatio, acceptedOriginalAspectRatioValue, item.Name);

        if (!acceptedOriginalAspectRatioValue.HasValue)
        {
            _logger.LogWarning("Failed to convert accepted origninal aspect ratio for {0}.", item.Name);
            return _cachedTask;
        }

        var aspectRatioDiscrepency = Math.Abs(acceptedOriginalAspectRatioValue.Value - videoStreamAspectRatioValue.Value);
        _logger.LogDebug("Discrpency between accepted original aspect ratio {0} and video stream aspect ratio of {1} for {2} is {3}.", acceptedOriginalAspectRatioValue, videoStreamAspectRatioValue, item.Name, aspectRatioDiscrepency);

        if (aspectRatioDiscrepency < .01M && !ShouldAlwaysWriteOriginalAspectRatio)
        {
            _logger.LogInformation("Accepted original aspect ratio of {0} for {1} matches video stream aspect ratio of {2}, skipping.", acceptedOriginalAspectRatioValue, item.Name, videoStreamAspectRatioValue);
            return _cachedTask;
        }

        aspectRatioItem.AspectRatio = acceptedOriginalAspectRatio;

        _logger.LogInformation("Saved original aspect ratio of {0} for {1}.", acceptedOriginalAspectRatioValue, item.Name);

        return ItemUpdateType.MetadataImport;
    }

    private async Task<decimal?> DetectOriginalAspectRatio(string inputPath, long durationInTicks, CancellationToken cancellationToken)
    {
        var duationInMs = durationInTicks / TimeSpan.TicksPerMillisecond;
        var numSamples = Math.Min(ApectRatioDetectChecksPerVideo, durationInTicks / TimeSpan.TicksPerSecond);  // Sample the lesser of ApectRatioDetectChecksPerVideo times or duration seconds times
        var interval = duationInMs / numSamples;

        var processArgsBuilder = new StringBuilder();
        processArgsBuilder.Append(" -nostats -hide_banner");

        for (long skipMs = 0; skipMs < duationInMs; skipMs += interval)
        {
            processArgsBuilder.Append(
                string.Format(
                    CultureInfo.InvariantCulture,
                    " -ss {0} -t 1 -i file:\"{1}\"",
                    TimeSpan.FromMilliseconds(skipMs).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                    inputPath));
        }

        processArgsBuilder.Append(" -filter_complex \"");

        for (var i = 0; i < numSamples; i++)
        {
            processArgsBuilder.Append(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0}:v]",
                    i.ToString(CultureInfo.InvariantCulture)));
        }

        processArgsBuilder.Append(
            string.Format(
                CultureInfo.InvariantCulture,
                "concat=n={0}:v=1:a=0[v];[v]cropdetect\" -f null -",
                numSamples.ToString(CultureInfo.InvariantCulture)));

        int exitCode;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                Arguments = processArgsBuilder.ToString(),
                FileName = _mediaEncoder.EncoderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardOutput = false,
                RedirectStandardError = true,  // FFMpeg produces output on stdError
            },
            EnableRaisingEvents = true
        };

        _logger.LogInformation("Performing ffmpeg cropdetect on {0} to determine original aspect ratio.", inputPath);
        _logger.LogDebug("{File} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

        var stdErrTerminated = new ManualResetEvent(false);
        var cropDetectErrorOutput = string.Empty;
        var handleErrorOutputDataReceived = new DataReceivedEventHandler(
            (_, args) =>
            {
                if (args?.Data == null)
                {
                    stdErrTerminated.Set();
                    return;
                }

                if (!args.Data.Contains("Parsed_cropdetect", StringComparison.InvariantCultureIgnoreCase))
                {
                    return;
                }

                cropDetectErrorOutput = args.Data;
            });

        process.ErrorDataReceived += handleErrorOutputDataReceived;

        var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            process.Start();
            process.BeginErrorReadLine();

            // wait for the process to finish
            await process.WaitForExitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;

            // wait for output to terminate
            stdErrTerminated.WaitOne();
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            exitCode = -1;
        }
        finally
        {
            process.ErrorDataReceived -= handleErrorOutputDataReceived;
            timeoutCancellationTokenSource.Dispose();

            stdErrTerminated.Close();
            process.Close();
        }

        if (exitCode != 0)
        {
            _logger.LogWarning("Failed to detect original aspect ratio for {0}.  Process exited with code {1}.", inputPath, exitCode);
            return null;
        }

        var outputTokens = cropDetectErrorOutput.Split(' ');
        var height = outputTokens.SingleOrDefault(sot => sot.StartsWith("h:", StringComparison.InvariantCultureIgnoreCase))?.Split(':')?[1];
        var width = outputTokens.SingleOrDefault(sot => sot.StartsWith("w:", StringComparison.InvariantCultureIgnoreCase))?.Split(':')?[1];

        int? heightVal = int.TryParse(height, out var tempHeightVal) ? tempHeightVal : null;
        int? widthVal = int.TryParse(width, out var tempWidthVal) ? tempWidthVal : null;

        if (heightVal == null || widthVal == null || heightVal <= 0 || widthVal <= 0)
        {
            _logger.LogWarning("Failed to parse height/width from ffmpeg output for {0}.", inputPath);
            return null;
        }

        _logger.LogInformation("Cropdetect found width of {0} and height of {1} for {2}.", widthVal, heightVal, inputPath);
        return (decimal)widthVal / (decimal)heightVal;
    }

    private BlurayDiscInfo? GetBDInfo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            return _blurayExaminer.GetDiscInfo(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting BDInfo");
            return null;
        }
    }

    private Task<MediaInfo> GetMediaInfo(
        Video item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = item.Path;
        var protocol = item.PathProtocol ?? MediaProtocol.File;

        if (item.IsShortcut)
        {
            path = item.ShortcutPath;
            protocol = _mediaSourceManager.GetPathProtocol(path);
        }

        return _mediaEncoder.GetMediaInfo(
            new MediaInfoRequest
            {
                ExtractChapters = false,
                MediaType = DlnaProfileType.Video,
                MediaSource = new MediaSourceInfo
                {
                    Path = path,
                    Protocol = protocol,
                    VideoType = item.VideoType,
                    IsoType = item.IsoType
                }
            },
            cancellationToken);
    }
}
