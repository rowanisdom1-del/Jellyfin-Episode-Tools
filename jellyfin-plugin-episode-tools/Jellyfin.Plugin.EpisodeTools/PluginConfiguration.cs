using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

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
    /// Per-show track preferences, edited on the Episode Tools Shows tab. SubtitleLanguage is
    /// "" (keep all), "none" (drop subtitles) or a language code. AudioLanguage is "" (keep all)
    /// or a language code; matching audio is kept and flagged as default, with a safety fallback
    /// that never removes every audio track.
    /// </summary>
    [XmlElement("Preference")]
    public List<ShowPreference> ShowPreferences { get; set; } = new List<ShowPreference>();

    /// <summary>
    /// Manual intro overrides, edited on the settings page. When an override matches a show
    /// (and optionally a season and/or an episode name fragment), the intro scanner skips
    /// detection for those episodes and stamps the configured start/end seconds onto every
    /// one of them instead. Use it for shows whose variable cold opens confuse the pairwise
    /// matching.
    /// </summary>
    [XmlElement("IntroOverride")]
    public List<IntroOverride> IntroOverrides { get; set; } = new List<IntroOverride>();

    /// <summary>
    /// Override conflict pairs already reported, so a duplicate rule logs a single warning
    /// per rule pair instead of one per matching episode.
    /// </summary>
    private static readonly HashSet<string> LoggedOverrideConflicts = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the first override that matches this episode, or null. Season and Episode are
    /// optional filters; an empty value matches everything. Episode matches when the file
    /// path contains the given fragment (case-insensitive), so "S01E06" or a full file name
    /// both work. When a logger is supplied, later rules that also match this episode are
    /// reported once each as conflicts; the first match still wins.
    /// </summary>
    public IntroOverride? FindIntroOverride(string show, string season, string path, ILogger? logger = null)
    {
        if (IntroOverrides is null)
        {
            return null;
        }

        IntroOverride? matched = null;
        var matchedIndex = -1;
        for (var i = 0; i < IntroOverrides.Count; i++)
        {
            var ov = IntroOverrides[i];
            if (ov is null || ov.Start < 0 || ov.End <= ov.Start)
            {
                continue;
            }

            var ovShow = (ov.Show ?? string.Empty).Trim();
            if (ovShow.Length == 0 || !string.Equals(ovShow, show ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ovSeason = (ov.Season ?? string.Empty).Trim();
            if (ovSeason.Length > 0 && !string.Equals(ovSeason, season ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ovEpisode = (ov.Episode ?? string.Empty).Trim();
            if (ovEpisode.Length > 0 && (path ?? string.Empty).IndexOf(ovEpisode, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (matched is null)
            {
                matched = ov;
                matchedIndex = i;
                continue;
            }

            if (logger is not null && LoggedOverrideConflicts.Add(matchedIndex + ">" + i))
            {
                logger.LogWarning(
                    "[Episode Tools] Intro override skipped: rule #{Index} (show {Show}) overlaps rule #{Matched} for episode {Path}; the first match wins.",
                    i + 1,
                    ovShow,
                    matchedIndex + 1,
                    path);
            }
        }

        return matched;
    }

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
    private string _dropGarbageSubs = "true";

    /// <summary>
    /// Raw serialized value for DropGarbageSubs. Kept as string for tolerant deserialization.
    /// The XML element name is preserved.
    /// </summary>
    [XmlElement("DropGarbageSubs")]
    public string DropGarbageSubsRaw
    {
        get => _dropGarbageSubs;
        set => _dropGarbageSubs = value ?? "true";
    }

    /// <summary>
    /// Gets or sets a value indicating whether subtitle tracks whose content is mostly garbage
    /// (pure numbers, ad/spam lines, OCR junk) are dropped during conversion when a better track
    /// exists. Requires python3 on the server; skipped silently when it is missing.
    /// This boolean accessor is XmlIgnored and parses many common textual representations.
    /// </summary>
    [XmlIgnore]
    public bool DropGarbageSubs
    {
        get
        {
            var s = (_dropGarbageSubs ?? "true").Trim().ToLowerInvariant();
            return !(s == "false" || s == "0" || s == "no" || s == "n" || s == "off");
        }
        set => _dropGarbageSubs = value ? "true" : "false";
    }

    /// <summary>
    /// Gets or sets an optional webhook URL that receives a POST request when a convert run
    /// finishes (apply mode only). Empty disables it. Useful with ntfy.sh or similar.
    /// </summary>
    public string NotifyWebhookUrl { get; set; } = string.Empty;

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

    // Backing fields for the fast intro scanner settings. Kept as strings for tolerant
    // deserialization, following the same pattern as MinFreeSpaceGb above.
    private string _introWindowMinutes = "4";
    private string _introParallelism = "2";
    private string _introMinDuration = "15";
    private string _introMaxDuration = "120";

    /// <summary>
    /// Raw serialized value for IntroWindowMinutes.
    /// </summary>
    [XmlElement("IntroWindowMinutes")]
    public string IntroWindowMinutesRaw
    {
        get => _introWindowMinutes;
        set => _introWindowMinutes = value ?? "4";
    }

    /// <summary>
    /// Gets or sets how many minutes of audio at the start of each episode are fingerprinted
    /// when scanning for intros (default 4). Intros always live at the start; limiting the
    /// window is what makes the scan much faster than the official scanner's default
    /// window (the first 25% of the file, up to 10 minutes).
    /// </summary>
    [XmlIgnore]
    public int IntroWindowMinutes
    {
        get => int.TryParse(_introWindowMinutes, out var n) && n >= 1 ? n : 4;
        set => _introWindowMinutes = Math.Clamp(value, 1, 15).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Raw serialized value for IntroParallelism.
    /// </summary>
    [XmlElement("IntroParallelism")]
    public string IntroParallelismRaw
    {
        get => _introParallelism;
        set => _introParallelism = value ?? "2";
    }

    /// <summary>
    /// Gets or sets how many episodes are fingerprinted at the same time (default 2).
    /// Each run spawns one ffmpeg process.
    /// </summary>
    [XmlIgnore]
    public int IntroParallelism
    {
        get => int.TryParse(_introParallelism, out var n) && n >= 1 ? n : 2;
        set => _introParallelism = Math.Clamp(value, 1, 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Raw serialized value for IntroMinDuration.
    /// </summary>
    [XmlElement("IntroMinDuration")]
    public string IntroMinDurationRaw
    {
        get => _introMinDuration;
        set => _introMinDuration = value ?? "15";
    }

    /// <summary>
    /// Gets or sets the shortest audio range (in seconds) that counts as an intro (default 15).
    /// </summary>
    [XmlIgnore]
    public int IntroMinDuration
    {
        get => int.TryParse(_introMinDuration, out var n) && n >= 1 ? n : 15;
        set => _introMinDuration = Math.Clamp(value, 1, 600).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Raw serialized value for IntroMaxDuration.
    /// </summary>
    [XmlElement("IntroMaxDuration")]
    public string IntroMaxDurationRaw
    {
        get => _introMaxDuration;
        set => _introMaxDuration = value ?? "120";
    }

    /// <summary>
    /// Gets or sets the longest audio range (in seconds) that counts as an intro (default 120).
    /// </summary>
    [XmlIgnore]
    public int IntroMaxDuration
    {
        get => int.TryParse(_introMaxDuration, out var n) && n >= 1 ? n : 120;
        set => _introMaxDuration = Math.Clamp(value, 1, 900).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
