using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Episode Tools: renames series episodes to "Show - S01E02 - Title" and converts MKV to MP4 without
/// re-encoding. The tasks show up under Dashboard > Scheduled Tasks > Episode Tools.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin folders.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        WorkFolder = Path.Combine(applicationPaths.DataPath, "episode-tools");
    }

    /// <summary>
    /// Gets the running plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the folder where the embedded scripts are unpacked before they run.
    /// </summary>
    public string WorkFolder { get; }

    /// <inheritdoc />
    public override string Name => "Episode Tools";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("059e3874-fa20-4af9-af24-8d26823840d9");

    /// <inheritdoc />
    public override string Description =>
        "Scheduled tasks to rename episodes to Show - S01E02 - Title and to convert MKV to MP4 without quality loss.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
