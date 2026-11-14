using System;
using System.Collections.Generic;
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
    /// Kept for compatibility with configurations from version 1.0/1.1; it is only used
    /// when the ConvertFolders list below is empty.
    /// </summary>
    public string ConvertDirectory { get; set; }

    /// <summary>
    /// Gets the folders that are scanned for .mkv files. Edited as up to five inputs on the
    /// settings page. When every entry is empty, ConvertDirectory is used instead.
    /// </summary>
    [XmlElement("ConvertFolder")]
    public List<string> ConvertFolders { get; set; } = new List<string>();

    /// <summary>
    /// Returns the effective list of folders to scan, trimming empty entries and falling back
    /// to the old single-folder setting for configurations saved by earlier versions.
    /// </summary>
    [XmlIgnore]
    public IReadOnlyList<string> EffectiveConvertFolders
    {
        get
        {
            var result = new List<string>();
            if (ConvertFolders is not null)
            {
                foreach (var folder in ConvertFolders)
                {
                    var trimmed = (folder ?? string.Empty).Trim();
                    if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(trimmed);
                    }
                }
            }

            if (result.Count == 0)
            {
                var legacy = (ConvertDirectory ?? string.Empty).Trim();
                if (legacy.Length > 0)
                {
                    result.Add(legacy);
                }
            }

            return result;
        }
    }

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
