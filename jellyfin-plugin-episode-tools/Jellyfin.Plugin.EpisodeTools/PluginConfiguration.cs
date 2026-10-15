using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Plugin settings. They are edited on the plugin's settings page and saved by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        RenameDirectory = "/mnt/storage/media/Series";
        ConvertDirectory = "/mnt/storage/media";
        DeleteOriginalsAfterConvert = false;
    }

    /// <summary>
    /// Gets or sets the folder that contains one sub folder per series.
    /// </summary>
    public string RenameDirectory { get; set; }

    /// <summary>
    /// Gets or sets the folder that is searched (including sub folders) for .mkv files.
    /// </summary>
    public string ConvertDirectory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each .mkv is deleted once its .mp4 has been checked.
    /// </summary>
    public bool DeleteOriginalsAfterConvert { get; set; }
}
