namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Per-show subtitle and audio defaults. Edited on the Episode Tools Shows tab; applied by the
/// convert script the next time that show is converted.
/// </summary>
public class ShowPreference
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShowPreference"/> class.
    /// </summary>
    public ShowPreference()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowPreference"/> class.
    /// </summary>
    /// <param name="show">Show name (the folder name inside the series folder).</param>
    /// <param name="subtitleLanguage">"" keep all, "none" drop subtitles, or a language code.</param>
    /// <param name="audioLanguage">"" keep all, or a language code.</param>
    public ShowPreference(string show, string subtitleLanguage, string audioLanguage, string audioFallback = "")
    {
        Show = show;
        SubtitleLanguage = subtitleLanguage;
        AudioLanguage = audioLanguage;
        AudioFallback = audioFallback;
    }

    /// <summary>Gets or sets the show name.</summary>
    public string Show { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred subtitle language: "", "none" or a language code.</summary>
    public string SubtitleLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred audio language: "" or a language code.</summary>
    public string AudioLanguage { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fallback audio language, used when no track matches AudioLanguage.
    /// "" or a language code. Never removes all audio either way.
    /// </summary>
    public string AudioFallback { get; set; } = string.Empty;
}
