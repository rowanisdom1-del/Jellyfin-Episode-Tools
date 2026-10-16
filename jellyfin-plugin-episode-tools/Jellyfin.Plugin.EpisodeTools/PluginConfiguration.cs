using System.Xml.Serialization;
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
        _deleteOriginalsAfterConvert = "false";
        _convertUnusualAudioToAac = "true";
    }

    /// <summary>
    /// Gets or sets the folder that contains one sub folder per series.
    /// </summary>
    public string RenameDirectory { get; set; }

    /// <summary>
    /// Gets or sets the folder that is searched (including sub folders) for .mkv files.
    /// </summary>
    public string ConvertDirectory { get; set; }

    // Backing fields that hold the raw serialized representation. These are strings to make
    // the XmlSerializer tolerant of values like "on"/"off"/"yes" which would otherwise
    // cause deserialization to fail for strict bool properties.
    private string _deleteOriginalsAfterConvert;
    private string _convertUnusualAudioToAac;

    /// <summary>
    /// Raw serialized value for DeleteOriginalsAfterConvert. Kept as string for tolerant
    /// deserialization. The XML element name is preserved.
    /// </summary>
    [XmlElement("DeleteOriginalsAfterConvert")]
    public string DeleteOriginalsAfterConvertRaw
    {
        get => _deleteOriginalsAfterConvert;
        set => _deleteOriginalsAfterConvert = value ?? "false";
    }

    /// <summary>
    /// Gets or sets a value indicating whether each .mkv is deleted once its .mp4 has been checked.
    /// This boolean accessor is XmlIgnored and parses many common textual representations.
    /// </summary>
    [XmlIgnore]
    public bool DeleteOriginalsAfterConvert
    {
        get
        {
            var s = (_deleteOriginalsAfterConvert ?? "false").Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes" || s == "y" || s == "on";
        }
        set => _deleteOriginalsAfterConvert = value ? "true" : "false";
    }

    /// <summary>
    /// Raw serialized value for ConvertUnusualAudioToAac. Kept as string for tolerant
    /// deserialization. The XML element name is preserved.
    /// </summary>
    [XmlElement("ConvertUnusualAudioToAac")]
    public string ConvertUnusualAudioToAacRaw
    {
        get => _convertUnusualAudioToAac;
        set => _convertUnusualAudioToAac = value ?? "false";
    }

    /// <summary>
    /// Gets or sets a value indicating whether audio that MP4 handles badly (DTS, TrueHD, FLAC...) is
    /// re-encoded to AAC instead of the file being skipped. The video is always copied untouched.
    /// This boolean accessor is XmlIgnored and parses many common textual representations.
    /// </summary>
    [XmlIgnore]
    public bool ConvertUnusualAudioToAac
    {
        get
        {
            var s = (_convertUnusualAudioToAac ?? "false").Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes" || s == "y" || s == "on";
        }
        set => _convertUnusualAudioToAac = value ? "true" : "false";
    }
}
