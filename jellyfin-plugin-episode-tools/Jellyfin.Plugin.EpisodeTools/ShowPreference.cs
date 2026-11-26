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

    /// <summary>
    /// Gets or sets a value indicating whether the fast intro scanner should include this
    /// show (default true). Turned off on the Shows tab for shows whose pairwise matching
    /// produces garbage (single specials, short-form content).
    /// </summary>
    public bool IntroScanEnabled { get; set; } = true;
}

/// <summary>
/// Manual intro timing override. Applied by the intro scanner to every matching episode;
/// matched episodes skip fingerprinting and pairing entirely.
/// </summary>
public class IntroOverride
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IntroOverride"/> class.
    /// </summary>
    public IntroOverride()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroOverride"/> class.
    /// </summary>
    /// <param name="show">Show folder name (required).</param>
    /// <param name="season">Season key like S01; empty matches every season.</param>
    /// <param name="episode">File path fragment like S01E06; empty matches every episode.</param>
    /// <param name="start">Intro start in seconds.</param>
    /// <param name="end">Intro end in seconds.</param>
    public IntroOverride(string show, string season, string episode, double start, double end)
    {
        Show = show;
        Season = season;
        Episode = episode;
        Start = start;
        End = end;
    }

    /// <summary>Gets or sets the show folder name (required).</summary>
    public string Show { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional season filter (S01 style; empty = every season).</summary>
    public string Season { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional episode filter (file path fragment; empty = every episode).</summary>
    public string Episode { get; set; } = string.Empty;

    /// <summary>Gets or sets the intro start in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Gets or sets the intro end in seconds.</summary>
    public double End { get; set; }
}
