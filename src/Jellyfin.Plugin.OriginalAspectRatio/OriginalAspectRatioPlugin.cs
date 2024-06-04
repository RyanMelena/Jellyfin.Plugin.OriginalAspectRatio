using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.OriginalAspectRatio.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OriginalAspectRatio;

/// <summary>
/// The main plugin.
/// </summary>
public class OriginalAspectRatioPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OriginalAspectRatioPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public OriginalAspectRatioPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Original Aspect Ratio Detector";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("1b77aa92-62b6-43d7-ae75-6ab2251c05a2");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static OriginalAspectRatioPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                DisplayName = "Original Aspect Ratio Discovery Plugin",
                Name = this.Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };
    }
}
