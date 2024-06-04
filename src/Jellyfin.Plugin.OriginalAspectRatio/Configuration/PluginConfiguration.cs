using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OriginalAspectRatio.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a comma-separated string of accepted original aspect ratios.
    /// </summary>
    public string AcceptedAspectRatios { get; set; } = "1.33, 1.78, 1.85, 2.00, 2.20, 2.35, 2.37, 2.39, 2.40";

    /// <summary>
    /// Gets or sets the number of aspect ratio detection checks used per video file.
    /// </summary>
    public int ApectRatioDetectChecksPerVideo { get; set; } = 10;

    /// <summary>
    /// Gets or sets a value indicating whether original aspect ratios should be written even when it matches video aspect ratio.
    /// </summary>
    public bool ShouldAlwaysWriteOriginalAspectRatio { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether existing original aspect ratios should be overriden.
    /// </summary>
    public bool ShouldOverrideExistingAspectRatio { get; set; } = false;

    /// <summary>
    /// Gets a collection of acceptable original aspect ratio values.
    /// </summary>
    public Collection<string> AspectRatios => new Collection<string>(AcceptedAspectRatios.Split(',', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries));
}
