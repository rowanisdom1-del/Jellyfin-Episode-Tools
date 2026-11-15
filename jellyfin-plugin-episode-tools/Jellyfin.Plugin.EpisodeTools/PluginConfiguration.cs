using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Gets or sets the folder that contains one sub folder per series. Kept for compatibility
    /// with configurations from earlier versions; it is only used when the RenameFolders list
    /// below is empty.
    /// </summary>
    public string RenameDirectory { get; set; }

    /// <summary>
    /// Gets the folders that contain one sub folder per series. Edited as up to five inputs on
    /// the settings page. When every entry is empty, RenameDirectory is used instead.
    /// </summary>
    [XmlElement("RenameFolder")]
    public List<string> RenameFolders { get; set; } = new List<string>();

    /// <summary>
    /// Returns the effective list of series folders, trimming empty entries and falling back to
    /// the old single-folder setting for configurations saved by earlier versions.
    /// </summary>
    [XmlIgnore]
    public IReadOnlyList<string> EffectiveRenameFolders
    {
        get
        {
            var result = new List<string>();
            if (RenameFolders is not null)
            {
                foreach (var folder in RenameFolders)
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
                var legacy = (RenameDirectory ?? string.Empty).Trim();
                if (legacy.Length > 0)
                {
                    result.Add(legacy);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Per-show track preferences, edited on the Episode Tools Shows page. SubtitleLanguage is
    /// "" (keep all), "none" (drop subtitles) or a language code. AudioLanguage is "" (keep all)
    /// or a language code; matching audio is kept and flagged as default, with a safety fallback
    /// that never removes every audio track.
    /// </summary>
    [XmlElement("Preference")]
    public List<ShowPreference> ShowPreferences { get; set; } = new List<ShowPreference>();

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
    private string _faststartOptimize = "true";
    private string _dropSignsSongs = "true";

    /// <summary>
    /// Raw serialized value for DropSignsSongs. Kept as string for tolerant deserialization.
    /// The XML element name is preserved.
    /// </summary>
    [XmlElement("DropSignsSongs")]
    public string DropSignsSongsRaw
    {
        get => _dropSignsSongs;
        set => _dropSignsSongs = value ?? "true";
    }

    /// <summary>
    /// Gets or sets a value indicating whether subtitle tracks whose title suggests they contain
    /// signs, songs, lyrics or karaoke (typical anime tracks) are dropped during conversion when a
    /// dialogue track exists. On by default: players otherwise often show the lyrics track.
    /// This boolean accessor is XmlIgnored and parses many common textual representations.
    /// </summary>
    [XmlIgnore]
    public bool DropSignsSongs
    {
        get
        {
            var s = (_dropSignsSongs ?? "true").Trim().ToLowerInvariant();
            return !(s == "false" || s == "0" || s == "no" || s == "n" || s == "off");
        }
        set => _dropSignsSongs = value ? "true" : "false";
    }

    // Backing field for the minimum free space requirement.
    private string _minFreeSpaceGb = "10";

    /// <summary>
    /// Raw serialized value for MinFreeSpaceGb. Kept as string for tolerant deserialization.
    /// </summary>
    [XmlElement("MinFreeSpaceGb")]
    public string MinFreeSpaceGbRaw
    {
        get => _minFreeSpaceGb;
        set => _minFreeSpaceGb = value ?? "10";
    }

    /// <summary>
    /// Gets or sets the minimum free space (in GB) required on the destination before a file is
    /// converted. Files are skipped with a clear report entry when there is less.
    /// This accessor is XmlIgnored and falls back to the default when the value is not a number.
    /// </summary>
    [XmlIgnore]
    public int MinFreeSpaceGb
    {
        get => int.TryParse(_minFreeSpaceGb, out var n) && n >= 0 ? n : 10;
        set => _minFreeSpaceGb = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Raw serialized value for FaststartOptimize. Kept as string for tolerant deserialization.
    /// The XML element name is preserved.
    /// </summary>
    [XmlElement("FaststartOptimize")]
    public string FaststartOptimizeRaw
    {
        get => _faststartOptimize;
        set => _faststartOptimize = value ?? "true";
    }

    /// <summary>
    /// Gets or sets a value indicating whether the converted MP4 is optimized for progressive
    /// web playback (faststart). This writes the output file twice. Turning it off makes the
    /// conversion faster and halves the writes, which helps on slow drives. Jellyfin streams
    /// with range requests, so it works fine without faststart.
    /// This boolean accessor is XmlIgnored and parses many common textual representations.
    /// </summary>
    [XmlIgnore]
    public bool FaststartOptimize
    {
        get
        {
            var s = (_faststartOptimize ?? "true").Trim().ToLowerInvariant();
            return !(s == "false" || s == "0" || s == "no" || s == "n" || s == "off");
        }
        set => _faststartOptimize = value ? "true" : "false";
    }

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
