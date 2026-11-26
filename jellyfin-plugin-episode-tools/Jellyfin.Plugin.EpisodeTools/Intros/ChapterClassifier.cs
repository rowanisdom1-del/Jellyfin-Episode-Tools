using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.EpisodeTools.Intros;

/// <summary>
/// A classified intro or outro window read straight from a file's container chapters.
/// Chapter boundaries are frame-exact, so results from here are used as-is: no chromaprint
/// pass and no silence/visual snapping (those can only make an exact boundary worse).
/// </summary>
public sealed class ChapterWindow
{
    public double Start { get; init; }

    public double End { get; init; }

    /// <summary>Chapter title the window was classified from, e.g. "OP".</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>One container chapter, already converted to seconds.</summary>
internal sealed class ChapterPoint
{
    public double Start { get; set; }

    public double End { get; set; }

    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// Classifies container chapters into intro (OP/opening) and outro (ED/ending/credits)
/// windows, implementing the priority chain: Manual Override → Valid Embedded Chapter →
/// Chromaprint + Snap. Zero dependency: works on the chapters ffprobe already extracted.
/// </summary>
internal static class ChapterClassifier
{
    // Titles that mark the OP/opening itself. The [^a-z] sentinels (case-insensitive) keep
    // fragments like "props" or "copilot" from matching "op".
    private static readonly Regex IntroRegex = new(
        @"(?i)(^|[^a-z])(op ?\d|ncop|opening|intro|theme song|title sequence|abertura|creditless opening|credit-less opening)($|[^a-z])",
        RegexOptions.Compiled);

    // Titles that mark the ED/ending/credits sequence itself. The lookbehind keeps
    // "post credits" (a bounder, not an outro) from matching "credits".
    private static readonly Regex OutroRegex = new(
        @"(?i)(^|[^a-z])(ed ?\d|nced|ending|outro|(?<!post[ -])credits|closing|encerramento|creditless ending|credit-less ending)($|[^a-z])",
        RegexOptions.Compiled);

    // Titles that can follow an OP chapter: they end the intro window.
    private static readonly Regex IntroBounderRegex = new(
        @"(?i)(^|[^a-z])(main ep|main episode|part a|part-a|episode|ep ?\d|act|story|chapter|title card|recap)($|[^a-z])",
        RegexOptions.Compiled);

    // Titles that can follow an ED chapter: they end the outro window.
    private static readonly Regex OutroBounderRegex = new(
        @"(?i)(^|[^a-z])(preview|next episode|next time|post credits|post-credits|c-part|c part|pv|dansei|title card)($|[^a-z])",
        RegexOptions.Compiled);

    // Bare names ("OP", "ED", "OP2", "Ending") that the sentineled patterns above miss.
    private static readonly Regex BareIntroRegex = new(
        @"(?i)^op ?\d*$|^opening$|^intro$|^ncop$|^abertura$",
        RegexOptions.Compiled);
    private static readonly Regex BareOutroRegex = new(
        @"(?i)^ed ?\d*$|^ending$|^outro$|^nced$|^credits$|^end credits$|^closing$|^encerramento$",
        RegexOptions.Compiled);

    // Hard floor: labeled OP/ED chapters shorter than this are studio bumpers or mislabeled
    // stingers, no matter what the configured minimum duration is.
    private const double HardFloorSeconds = 10;

    /// <summary>
    /// Finds the first chapter whose title classifies as an OP/opening and whose resulting
    /// window passes the duration guards. Returns null when the file has no usable intro
    /// chapter (the caller falls back to fingerprinting).
    /// </summary>
    public static ChapterWindow? FindIntro(
        IReadOnlyList<ChapterPoint> chapters, int minDuration, int maxDuration, double fileDuration)
    {
        return FindWindow(chapters, IsIntro, IsIntroBounder, minDuration, maxDuration, fileDuration);
    }

    /// <summary>
    /// Finds the first chapter whose title classifies as an ED/ending/credits. The window
    /// runs to the next bounder chapter (preview/next-episode/…) or to the end of the file.
    /// </summary>
    public static ChapterWindow? FindOutro(
        IReadOnlyList<ChapterPoint> chapters, int minDuration, int maxDuration, double fileDuration)
    {
        return FindWindow(chapters, IsOutro, IsOutroBounder, minDuration, maxDuration, fileDuration);
    }

    private static bool IsIntro(string title) => IntroRegex.IsMatch(title) || BareIntroRegex.IsMatch(title.Trim());

    private static bool IsOutro(string title) => OutroRegex.IsMatch(title) || BareOutroRegex.IsMatch(title.Trim());

    private static bool IsIntroBounder(string title) => IntroBounderRegex.IsMatch(title);

    private static bool IsOutroBounder(string title) => OutroBounderRegex.IsMatch(title);

    private static ChapterWindow? FindWindow(
        IReadOnlyList<ChapterPoint> chapters,
        Func<string, bool> isMatch,
        Func<string, bool> isBounder,
        int minDuration,
        int maxDuration,
        double fileDuration)
    {
        if (chapters.Count < 2 || fileDuration <= 0)
        {
            return null;
        }

        for (var i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            if (!isMatch(chapter.Title))
            {
                continue;
            }

            // Window end: the first following chapter with a bounder title, else this
            // chapter's own end (many tags give the OP/ED chapter its true duration).
            var end = chapter.End;
            for (var k = i + 1; k < chapters.Count; k++)
            {
                if (isBounder(chapters[k].Title))
                {
                    end = chapters[k].Start;
                    break;
                }
            }

            var duration = end - chapter.Start;
            var min = Math.Max(minDuration, HardFloorSeconds);

            // Guards: real OP/ED windows live inside the configured duration bounds, are
            // never studio bumpers (hard floor), never start at 0 (a chapter at absolute 0
            // is almost always a mislabeled bumper or a whole first act) and never swallow
            // most of the episode (>60% runtime means the tag is wrong, not an intro).
            if (duration < min || duration > maxDuration)
            {
                continue;
            }

            if (chapter.Start <= 0.5)
            {
                continue;
            }

            if (duration > 0.6 * fileDuration)
            {
                continue;
            }

            return new ChapterWindow
            {
                Start = Math.Round(chapter.Start, 2),
                End = Math.Round(Math.Min(end, fileDuration), 2),
                Label = string.IsNullOrWhiteSpace(chapter.Title) ? "chapter" : chapter.Title.Trim()
            };
        }

        return null;
    }

    /// <summary>
    /// Classifies chapters that Jellyfin already has in its database (used by the media
    /// segment provider as a fallback when the scanner has no result for an episode yet).
    /// Chapter end times are derived from the next chapter's start because the stored
    /// chapter model only carries start positions.
    /// </summary>
    public static (ChapterWindow? Intro, ChapterWindow? Outro) ClassifyStoredChapters(
        IReadOnlyList<(string? Name, double Start)> stored,
        double fileDuration,
        int minDuration,
        int maxDuration,
        bool wantOutro)
    {
        if (stored.Count < 2 || fileDuration <= 0)
        {
            return (null, null);
        }

        var chapters = new List<ChapterPoint>(stored.Count);
        for (var i = 0; i < stored.Count; i++)
        {
            var end = i + 1 < stored.Count ? stored[i + 1].Start : fileDuration;
            chapters.Add(new ChapterPoint { Start = stored[i].Start, End = end, Title = stored[i].Name ?? string.Empty });
        }

        var intro = FindIntro(chapters, minDuration, maxDuration, fileDuration);
        var outro = wantOutro ? FindOutro(chapters, minDuration, maxDuration, fileDuration) : null;
        return (intro, outro);
    }

    /// <summary>Converts Jellyfin's stored chapter tick positions to seconds.</summary>
    public static double TicksToSeconds(long ticks) => ticks / (double)TimeSpan.TicksPerSecond;
}
