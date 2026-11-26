using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools.Intros;

/// <summary>
/// One detected intro: the shared opening sequence found between episodes of the same season.
/// Times are seconds from the start of the file.
/// </summary>
public sealed class IntroResult
{
    /// <summary>Gets or sets the absolute path of the episode file.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the series name (folder name).</summary>
    public string Show { get; set; } = string.Empty;

    /// <summary>Gets or sets the season marker parsed from the file name, e.g. "S01".</summary>
    public string Season { get; set; } = string.Empty;

    /// <summary>Gets or sets the intro start (seconds).</summary>
    public double Start { get; set; }

    /// <summary>Gets or sets the intro end (seconds).</summary>
    public double End { get; set; }

    /// <summary>Gets or sets the detected outro/credits start (seconds), or null when none.</summary>
    public double? OutroStart { get; set; }

    /// <summary>Gets or sets the detected outro/credits end (seconds), or null when none.</summary>
    public double? OutroEnd { get; set; }

    /// <summary>
    /// Where this intro came from: "override", "chapter", "fingerprint" or "skipped"
    /// (no usable segment; Detail carries the reason). Null in results written by older
    /// plugin versions — the UI treats null as "fingerprint".
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Human-readable provenance, e.g. Chapter: "OP", Fingerprint (11/12), Override, or
    /// the skip reason. Null when unknown.
    /// </summary>
    public string? Detail { get; set; }
}

/// <summary>
/// Runs one intro scan at a time across the configured series/scan folders.
/// Way faster than the official Intro Skipper: only the first few minutes of each file are
/// fingerprinted (intros always live at the start), files are decoded in parallel and results
/// are cached per file, so re-scans only fingerprint new or changed episodes.
/// </summary>
internal static class IntrosRunner
{
    private const double SamplesToSeconds = 0.128;

    private static readonly Regex SeasonRegex = new(
        @"(?i)S(\d{1,3})E(\d{1,3})",
        RegexOptions.Compiled);

    private static readonly Regex SilenceRegex = new(
        @"silence_(?<type>start|end):\s*(?<time>[0-9.]+)",
        RegexOptions.Compiled);

    private static readonly object Lock = new();
    private static Task? _running;
    private static CancellationTokenSource? _cancellation;
    private static string _lastMessage = string.Empty;
    private static double _lastProgress;

    /// <summary>
    /// Starts a scan. Returns false when one is already running. When
    /// <paramref name="showFilter"/> is set, only episodes of that show (folder leaf name,
    /// case-insensitive) are scanned — used by the Intros tab's per-show rescan.
    /// </summary>
    public static bool TryStart(ILogger logger, string? showFilter = null)
    {
        lock (Lock)
        {
            if (_running is not null && !_running.IsCompleted)
            {
                return false;
            }

            _lastMessage = "starting";
            _lastProgress = 0;
            _cancellation = new CancellationTokenSource();
            var ct = _cancellation.Token;
            _running = Task.Run(
                async () =>
                {
                    try
                    {
                        await RunAsync(logger, ct, showFilter).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _lastMessage = "stopped";
                        logger.LogInformation("[Episode Tools] Intro scan stopped.");
                    }
                    catch (Exception ex)
                    {
                        _lastMessage = "failed: " + ex.Message;
                        logger.LogError(ex, "[Episode Tools] Fast intro scan failed.");
                    }
                },
                CancellationToken.None);
            return true;
        }
    }

    /// <summary>Stops a running scan politely.</summary>
    public static void Stop()
    {
        lock (Lock)
        {
            _cancellation?.Cancel();
        }
    }

    /// <summary>True while a scan is running.</summary>
    public static bool Running
    {
        get
        {
            lock (Lock)
            {
                return _running is not null && !_running.IsCompleted;
            }
        }
    }

    /// <summary>Latest progress percentage (0-100) of the running scan.</summary>
    public static double LastProgress
    {
        get
        {
            lock (Lock)
            {
                return _lastProgress;
            }
        }
    }

    /// <summary>Snapshot for the status endpoint.</summary>
    public static object Status()
    {
        lock (Lock)
        {
            return new
            {
                running = Running,
                message = _lastMessage,
                progress = _lastProgress
            };
        }
    }

    /// <summary>Path of the results file.</summary>
    private static string ResultsPath =>
        Path.Combine(Plugin.Instance?.WorkFolder ?? ".", "intros.json");

    private static (long Length, DateTime Modified, IReadOnlyList<IntroResult> Results, IntrosState? State)? _resultsCache;

    /// <summary>True when the results file holds an intro for exactly this path.</summary>
    public static bool HasIntro(string path)
    {
        var results = LoadResults();
        foreach (var intro in results)
        {
            if (string.Equals(intro.File, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the results file holds a usable intro or outro (one second or longer) for
    /// exactly this path — used by the media segment provider to decide whether to run.
    /// </summary>
    public static bool HasAnySegment(string path)
    {
        var results = LoadResults();
        foreach (var intro in results)
        {
            if (string.Equals(intro.File, path, StringComparison.OrdinalIgnoreCase) &&
                (intro.End - intro.Start >= 1 ||
                 (intro.OutroStart is { } outroStart && intro.OutroEnd is { } outroEnd && outroEnd - outroStart >= 1)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Loads the latest results (empty list when nothing was scanned yet). The file is only
    /// re-parsed when its timestamp or size changes, since this is called per episode while
    /// the media segment task runs and per poll while the dashboard is open.
    /// </summary>
    public static IReadOnlyList<IntroResult> LoadResults() => LoadState().Results;

    /// <summary>
    /// Loads the latest results and the raw scan settings of the previous run (used by the
    /// scanner to detect settings changes). The file is only re-parsed when its timestamp or
    /// size changes, since this is called per episode while the media segment task runs and
    /// per poll while the dashboard is open. Deleting or corrupting the results file clears
    /// the cache immediately, so a reset takes effect without a server restart.
    /// </summary>
    internal static (IReadOnlyList<IntroResult> Results, IntrosState? State) LoadState()
    {
        try
        {
            var file = ResultsPath;
            if (Plugin.Instance is null || !File.Exists(file))
            {
                _resultsCache = null;
                return (Array.Empty<IntroResult>(), null);
            }

            var info = new FileInfo(file);
            var key = (Length: info.Length, Modified: info.LastWriteTimeUtc);
            if (_resultsCache is { } cached && cached.Length == key.Length && cached.Modified == key.Modified)
            {
                return (cached.Results, cached.State);
            }

            var json = File.ReadAllText(file);
            var state = System.Text.Json.JsonSerializer.Deserialize<IntrosState>(
                json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            IReadOnlyList<IntroResult> results = state?.Intros ?? new List<IntroResult>();
            _resultsCache = (key.Length, key.Modified, results, state);
            return (results, state);
        }
        catch (Exception)
        {
            _resultsCache = null;
            return (Array.Empty<IntroResult>(), null);
        }
    }

    internal sealed class IntrosState
    {
        // Version 2 adds per-entry Source/Detail provenance; deserialization is tolerant,
        // so v1 files (null Source) still load and are shown as fingerprints.
        public int Version { get; set; } = 2;

        public DateTime ScannedAt { get; set; }

        public int WindowSeconds { get; set; }

        public int MinDuration { get; set; }

        public int MaxDuration { get; set; }

        public List<IntroResult> Intros { get; set; } = new();
    }

    private sealed class Job
    {
        public string Path { get; set; } = string.Empty;

        public string Show { get; set; } = string.Empty;

        public string Season { get; set; } = string.Empty;

        public string SeasonKey { get; set; } = string.Empty;

        public uint[]? Points { get; set; }

        // Staged at enumeration time (one directory listing pass), so every cache check in
        // the scan validates against these values instead of re-statting the file over
        // SMB/NFS three times per episode.
        public long Size { get; set; }

        public double ModifiedUtc { get; set; }
    }

    /// <summary>One container chapter, already converted to seconds.</summary>
    internal sealed class ChapterPoint
    {
        public double Start { get; set; }

        public double End { get; set; }

        public string Title { get; set; } = string.Empty;
    }

    /// <summary>The scan itself.</summary>
    private static async Task RunAsync(ILogger logger, CancellationToken ct, string? showFilter = null)
    {
        var cfg = Plugin.Instance?.Configuration;
        var work = Plugin.Instance?.WorkFolder;
        if (cfg is null || work is null)
        {
            _lastMessage = "plugin not initialized";
            return;
        }

        var window = Math.Clamp(cfg.IntroWindowMinutes, 1, 15) * 60;
        var parallelism = Math.Clamp(cfg.IntroParallelism, 1, 8);
        var minDuration = Math.Clamp(cfg.IntroMinDuration, 5, 300);
        var maxDuration = Math.Clamp(cfg.IntroMaxDuration, minDuration, 600);

        var folders = new List<string>(cfg.EffectiveRenameFolders);
        foreach (var f in cfg.EffectiveConvertFolders)
        {
            if (!folders.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(f);
            }
        }

        // Every video file under every configured show folder, grouped per (show folder, season).
        var jobs = EnumerateJobs(folders);

        // Per-show rescan (Intros tab): restrict the scan to one show's episodes. The show
        // name is matched case-insensitively against the enumerated folder leaf names, so
        // no path traversal is possible and all seasons of the show are covered.
        if (!string.IsNullOrWhiteSpace(showFilter))
        {
            var filter = showFilter.Trim();
            var beforeFilter = jobs.Count;
            jobs = jobs.Where(j => string.Equals(j.Show, filter, StringComparison.OrdinalIgnoreCase)).ToList();
            logger.LogInformation(
                "[Episode Tools] Intro scan: per-show filter \"{Show}\" keeps {Count} of {Before} episode(s).",
                filter,
                jobs.Count,
                beforeFilter);
            if (jobs.Count == 0)
            {
                _lastMessage = $"no episodes found for show \"{filter}\"";
                return;
            }
        }

        // Per-show opt-out from the Shows tab: shows with IntroScanEnabled=false (single
        // specials, short-form content) are excluded before anything is fingerprinted.
        var skipShows = new HashSet<string>(
            (cfg.ShowPreferences ?? new List<ShowPreference>())
                .Where(p => p is not null && !p.IntroScanEnabled)
                .Select(p => (p.Show ?? string.Empty).Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (skipShows.Count > 0)
        {
            var beforeCount = jobs.Count;
            jobs = jobs.Where(j => !skipShows.Contains(j.Show)).ToList();
            if (jobs.Count < beforeCount)
            {
                logger.LogInformation(
                    "[Episode Tools] Intro scan: skipping {Count} episode(s) in {Shows} show(s) disabled on the Shows tab.",
                    beforeCount - jobs.Count,
                    skipShows.Count);
            }
        }

        logger.LogInformation(
            "[Episode Tools] Intro scan: {Jobs} episode(s) enumerated. Folders: {Folders}",
            jobs.Count,
            string.Join(", ", folders));
        if (jobs.Count == 0)
        {
            logger.LogInformation("[Episode Tools] Intro scan found no episodes in the configured folders.");
            _lastMessage = "no episodes found in the configured folders";
            return;
        }

        var cacheDir = Path.Combine(work, "intros-fpcache");
        Directory.CreateDirectory(cacheDir);

        // One metadata probe per file (chapters + duration in a single ffprobe call) with
        // its own cache: the outro pass reuses the duration instead of running a second
        // ffprobe per file, and the chapter fast-path reads the chapters straight from here.
        var meta = new Dictionary<string, (double Duration, List<ChapterPoint> Chapters)>(StringComparer.OrdinalIgnoreCase);
        var metaMissing = new List<Job>();
        foreach (var job in jobs)
        {
            if (TryLoadMeta(cacheDir, job.Path, job.Size, job.ModifiedUtc, out var m))
            {
                meta[job.Path] = m;
            }
            else
            {
                metaMissing.Add(job);
            }
        }

        if (metaMissing.Count > 0)
        {
            _lastMessage = $"probing {metaMissing.Count} episode(s)";
            var metaDone = 0;
            await Parallel.ForEachAsync(
                metaMissing,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = ct
                },
                async (job, token) =>
                {
                    var probed = await ProbeMetadataAsync(job.Path, logger, token).ConfigureAwait(false);
                    if (probed is { } ok)
                    {
                        meta[job.Path] = ok;
                        SaveMeta(cacheDir, job, ok.Duration, ok.Chapters);
                    }

                    var count = Interlocked.Increment(ref metaDone);
                    _lastProgress = 50.0 * count / Math.Max(1, metaMissing.Count);
                    _lastMessage = $"probing {count}/{metaMissing.Count} episode(s)";
                }).ConfigureAwait(false);
        }

        // Load fingerprints from the cache for everything we already scanned before.
        foreach (var job in jobs)
        {
            if (TryLoadCached(cacheDir, job.Path, window, job.Size, job.ModifiedUtc, out var points))
            {
                job.Points = points;
            }
        }

        // Compare seasons: every episode against the episodes that come after it.
        // Seasons are processed one at a time and results are saved after each one,
        // so intros stream in as the scan runs instead of only at the very end.
        var seasons = jobs.GroupBy(j => j.SeasonKey, StringComparer.OrdinalIgnoreCase).ToList();
        logger.LogInformation(
            "[Episode Tools] Intro scan: {Jobs} episode(s) in {Seasons} season group(s).",
            jobs.Count,
            seasons.Count);

        // Manual overrides: episodes with a matching override never get fingerprinted, and
        // their intro window is stamped straight into the results. This both saves all the
        // fingerprint/pairing work for shows where detection is unnecessary or unreliable and
        // protects the configured window from being clobbered by a rescan.
        var overrideMap = new Dictionary<string, IntroResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            var ov = cfg.FindIntroOverride(job.Show, job.Season, job.Path, logger);
            if (ov is not null)
            {
                overrideMap[job.Path] = new IntroResult
                {
                    File = job.Path,
                    Show = job.Show,
                    Season = job.Season,
                    Start = Math.Round(ov.Start, 2),
                    End = Math.Round(ov.End, 2),
                    Source = "override",
                    Detail = "Override"
                };
            }
        }

        if (overrideMap.Count > 0)
        {
            logger.LogInformation(
                "[Episode Tools] Intro scan: {Count} episode(s) matched a manual intro override and skip detection.",
                overrideMap.Count);
        }

        // Chapter fast-path (priority: override > chapter > fingerprint): episodes whose
        // container carries a well-tagged OP/ED chapter get their intro (and outro, when
        // enabled) straight from the chapters — frame exact, no chromaprint decode, no
        // statistical matching and no silence snapping (which could only move an exact
        // boundary). Chapter boundaries come from the metadata probe's cache.
        var chapterIntro = new Dictionary<string, IntroResult>(StringComparer.OrdinalIgnoreCase);
        var chapterOutro = new Dictionary<string, (double Start, double End, string Label)>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (overrideMap.ContainsKey(job.Path) || !meta.TryGetValue(job.Path, out var m) || m.Chapters.Count < 2)
            {
                continue;
            }

            var win = ChapterClassifier.FindIntro(m.Chapters, minDuration, maxDuration, m.Duration);
            if (win is not null)
            {
                chapterIntro[job.Path] = new IntroResult
                {
                    File = job.Path,
                    Show = job.Show,
                    Season = job.Season,
                    Start = win.Start,
                    End = win.End,
                    Source = "chapter",
                    Detail = $"Chapter: \"{win.Label}\""
                };
            }

            if (cfg.DetectOutros)
            {
                var ed = ChapterClassifier.FindOutro(m.Chapters, minDuration, maxDuration, m.Duration);
                if (ed is not null)
                {
                    chapterOutro[job.Path] = (ed.Start, ed.End, ed.Label);
                }
            }
        }

        if (chapterIntro.Count > 0 || chapterOutro.Count > 0)
        {
            logger.LogInformation(
                "[Episode Tools] Intro scan: {Intros} intro(s) and {Outros} outro(s) read from embedded chapters; those episodes skip fingerprinting.",
                chapterIntro.Count,
                chapterOutro.Count);
        }

        var totalMissing = jobs.Count(j => j.Points is null && !overrideMap.ContainsKey(j.Path) && !chapterIntro.ContainsKey(j.Path));
        var done = 0;
        var found = new Dictionary<string, IntroResult>(StringComparer.OrdinalIgnoreCase);

        // Detect scanner settings changes: min/max duration influence the pairing result, so a
        // change forces re-pairing even when every fingerprint came from the cache.
        var (prevResults, prevState) = LoadState();
        var settingsChanged = prevState is null ||
                              prevState.WindowSeconds != window ||
                              prevState.MinDuration != minDuration ||
                              prevState.MaxDuration != maxDuration;

        // Seed with the previous run's results so an interrupted scan never wipes intros for
        // seasons it has not reached yet; entries for files that disappeared drop out here.
        var jobPaths = new HashSet<string>(jobs.Select(j => j.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var prev in prevResults)
        {
            if (jobPaths.Contains(prev.File))
            {
                found[prev.File] = prev;
            }
        }

        // Overrides take precedence over anything a previous scan found, then chapter
        // results (frame-exact) over anything a previous fingerprint-based scan found.
        foreach (var kvp in overrideMap)
        {
            found[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in chapterIntro)
        {
            found[kvp.Key] = kvp.Value;
        }

        var jsonOpts = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        void WriteState()
        {
            var results = found.Values
                .OrderBy(r => r.Show, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var state = new IntrosState
            {
                ScannedAt = DateTime.UtcNow,
                WindowSeconds = window,
                MinDuration = minDuration,
                MaxDuration = maxDuration,
                Intros = results
            };

            Directory.CreateDirectory(work);
            var tmp = ResultsPath + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(state, jsonOpts));

            // Rename over the old file so readers never see a half-written document.
            File.Move(tmp, ResultsPath, overwrite: true);
        }

        var seasonIndex = 0;
        foreach (var season in seasons)
        {
            seasonIndex++;
            ct.ThrowIfCancellationRequested();

            // Playback guard: never fingerprint while someone is actually watching. The
            // check runs between season groups, so pauses are always at file boundaries
            // and a stopped session resumes the scan within seconds.
            await PlaybackGuard.WaitWhilePlaybackActiveAsync(logger, ct).ConfigureAwait(false);

            if (totalMissing == 0)
            {
                // Everything came from the cache; the season counter is the only progress.
                _lastProgress = 100.0 * seasonIndex / seasons.Count;
            }

            var queue = season.Where(j => j.Points is { Length: > 0 } && !overrideMap.ContainsKey(j.Path) && !chapterIntro.ContainsKey(j.Path)).ToList();
            var missing = season.Where(j => j.Points is null && !overrideMap.ContainsKey(j.Path) && !chapterIntro.ContainsKey(j.Path)).ToList();

            if (missing.Count > 0)
            {
                _lastMessage = $"season {seasonIndex}/{seasons.Count}: fingerprinting {missing.Count} episode(s)";

                await Parallel.ForEachAsync(
                    missing,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = ct
                    },
                    async (job, token) =>
                    {
                        job.Points = await FingerprintAsync(job.Path, window, logger, token).ConfigureAwait(false);
                        if (job.Points is not null && job.Points.Length > 0)
                        {
                            SaveCached(cacheDir, job, window);
                        }

                        var count = Interlocked.Increment(ref done);
                        _lastProgress = 100.0 * count / Math.Max(1, totalMissing);
                        _lastMessage = $"fingerprinting {count}/{totalMissing} episodes (season {seasonIndex}/{seasons.Count})";
                    }).ConfigureAwait(false);
            }

            queue = season.Where(j => j.Points is { Length: > 0 } && !overrideMap.ContainsKey(j.Path) && !chapterIntro.ContainsKey(j.Path)).ToList();
            if (queue.Count < 2)
            {
                // Overridden and chapter-covered episodes of this season keep their exact
                // intro even when fewer than two episodes are left for pairing — a lone
                // special with a tagged OP now gets a skip button too.
                foreach (var j in season)
                {
                    if (overrideMap.TryGetValue(j.Path, out var ov))
                    {
                        found[j.Path] = ov;
                    }
                    else if (chapterIntro.TryGetValue(j.Path, out var ci))
                    {
                        found[j.Path] = ci;
                    }
                    else if (found.TryGetValue(j.Path, out var seeded))
                    {
                        // A previous scan already produced this result; keep it instead of
                        // marking the episode skipped.
                    }
                    else
                    {
                        // Skipped diagnostics: make the reason visible instead of the file
                        // being silently absent from the Intros tab.
                        found[j.Path] = SkippedResult(j, "Skipped: needs 2+ episodes for matching");
                    }
                }

                continue;
            }

            if (missing.Count == 0 && !settingsChanged)
            {
                // Every fingerprint came from the cache and the scanner settings did not
                // change: pairing would reproduce the exact same results (it is a pure
                // function of the fingerprints), so keep the seeded values and skip both the
                // O(n^2) comparison and the ffmpeg silence snap pass.
                _lastMessage = $"season {seasonIndex}/{seasons.Count}: all cached, kept {queue.Count} intro(s)";
                WriteState();
                continue;
            }

            // Replace this season's entries with fresh results (a match that disappeared
            // must disappear from the results too); seasons not reached keep their old value.
            var seasonPaths = new HashSet<string>(season.Select(j => j.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in found.Keys.Where(k => seasonPaths.Contains(k)).ToList())
            {
                found.Remove(stale);
            }

            // Re-stamp overrides and chapter results for this season: the wipe above must
            // not drop them.
            foreach (var j in season)
            {
                if (overrideMap.TryGetValue(j.Path, out var ov))
                {
                    found[j.Path] = ov;
                }
                else if (chapterIntro.TryGetValue(j.Path, out var ci))
                {
                    found[j.Path] = ci;
                }
            }

            _lastMessage = $"season {seasonIndex}/{seasons.Count}: comparing {queue.Count} episode(s)";

            // Collect every pairwise match first; the season-wide consensus decides which
            // clusters are real intros (a recap or an OVA can no longer poison every file
            // it paired with via longest-match).
            var pairMatches = new List<(string A, string B, double Start, double End)>();
            for (var i = 0; i < queue.Count; i++)
            {
                // Build the left side's inverted index once per episode and reuse it across
                // every partner in the inner loop (was: one rebuild per pair).
                var lhsIndex = BuildIndex(queue[i].Points!);
                for (var k = i + 1; k < queue.Count; k++)
                {
                    var match = CompareEpisodes(queue[i].Points!, lhsIndex, queue[k].Points!, minDuration, maxDuration, window);
                    if (match is not null)
                    {
                        pairMatches.Add((queue[i].Path, queue[k].Path, match.Value.Item1, match.Value.Item2));
                    }
                }
            }

            var bounds = BuildConsensus(queue, pairMatches, logger, $"season {seasonIndex}/{seasons.Count}");

            // Snap intro ends to silence, like the official scanner does. Each snap is an
            // independent ffmpeg pass, so they run in parallel with the same parallelism as
            // fingerprinting instead of one-after-another.
            var toSnap = queue
                .Where(j => bounds.ContainsKey(j.Path))
                .Select(j => (Job: j, Bound: bounds[j.Path]))
                .ToList();

            if (toSnap.Count > 0)
            {
                _lastMessage = $"season {seasonIndex}/{seasons.Count}: snapping {toSnap.Count} intro(s)";
                var snapped = new ConcurrentDictionary<string, IntroResult>(StringComparer.OrdinalIgnoreCase);

                await Parallel.ForEachAsync(
                    toSnap,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = ct
                    },
                    async (item, token) =>
                    {
                        var b = item.Bound;
                        var (end, ambiguous) = await AdjustEndToSilence(item.Job.Path, b.Start, b.End, logger, token).ConfigureAwait(false);
                        if (end == b.End && ambiguous)
                        {
                            // Silence existed near the boundary but further away than the
                            // snap tolerance (ambiguous fade/cut): try the visual black
                            // frame refinement before falling back to the raw match.
                            var visual = await AdjustBoundaryToBlackFrame(item.Job.Path, b.End, logger, token).ConfigureAwait(false);
                            if (visual is { } v && v - b.Start >= minDuration)
                            {
                                end = v;
                            }
                        }

                        if (end - b.Start < minDuration)
                        {
                            // The snap shrank the intro below the configured minimum (quiet
                            // audio near the start); keep the fingerprinted end instead.
                            end = b.End;
                        }

                        snapped[item.Job.Path] = new IntroResult
                        {
                            File = item.Job.Path,
                            Show = item.Job.Show,
                            Season = item.Job.Season,
                            Start = Math.Round(b.Start, 2),
                            End = Math.Round(end, 2),
                            Source = "fingerprint",
                            Detail = $"Fingerprint ({b.Agree}/{b.Total})"
                        };
                    }).ConfigureAwait(false);

                foreach (var kvp in snapped)
                {
                    found[kvp.Key] = kvp.Value;
                }
            }

            // Skipped diagnostics: everything that went through the fresh pipeline without
            // a result gets a tombstone entry with the exact reason, so the Intros tab can
            // show why an episode has no skip button.
            foreach (var j in season)
            {
                if (overrideMap.ContainsKey(j.Path) || chapterIntro.ContainsKey(j.Path) || found.ContainsKey(j.Path))
                {
                    continue;
                }

                found[j.Path] = new IntroResult
                {
                    File = j.Path,
                    Show = j.Show,
                    Season = j.Season,
                    Source = "skipped",
                    Detail = j.Points is { Length: > 0 }
                        ? "Skipped: no match (pairing)"
                        : "Skipped: fingerprint failed"
                };
            }

            WriteState();
            _lastMessage = $"season {seasonIndex}/{seasons.Count}: {found.Count} intro(s) so far";
            logger.LogInformation(
                "[Episode Tools] Intro scan: season {Index}/{Total} done; {Count} intro(s) so far.",
                seasonIndex,
                seasons.Count,
                found.Count);
        }

        WriteState();

        // Cache index: one file mapping every live episode to its cache file name, so
        // pruning only has to stat-and-unlink dead entries instead of parsing thousands
        // of JSON files (which was minutes of I/O at the end of scans on huge libraries).
        var liveCacheFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            liveCacheFiles.Add(Path.GetFileName(CacheFileFor(cacheDir, job.Path, window)));
            liveCacheFiles.Add(Path.GetFileName(MetaCacheFileFor(cacheDir, job.Path)));
        }

        SaveCacheIndex(cacheDir, liveCacheFiles);
        PruneCache(cacheDir, liveCacheFiles);

        // Outros/credits (optional, off by default): a second pass over the same season
        // groups, fingerprinting the last minutes of each file and matching those.
        if (cfg.DetectOutros)
        {
            await ScanOutrosAsync(cfg, jobs, seasons, cacheDir, meta, chapterOutro, minDuration, maxDuration, found, WriteState, logger, ct)
                .ConfigureAwait(false);
        }

        var results2 = found.Values.OrderBy(r => r.Show, StringComparer.OrdinalIgnoreCase).ToList();
        _lastMessage = $"finished: {results2.Count} intro(s) in {jobs.Count} episode(s)";
        _lastProgress = 100;
        logger.LogInformation(
            "[Episode Tools] Fast intro scan finished: {Count} intro(s) from {Episodes} episode(s).",
            results2.Count,
            jobs.Count);
    }

    // ------------------------------------------------------------------ consensus (season-wide)

    // Two pairwise matches belong to the same cluster when their (start, end) agree within
    // this tolerance; cluster bounds are the median of their members.
    private const double ConsensusToleranceSeconds = 5;

    /// <summary>Consensus bounds for one episode, plus its match confidence.</summary>
    private sealed class ConsensusBound
    {
        public double Start { get; set; }

        public double End { get; set; }

        /// <summary>Pair matches of this episode that agree with the assigned cluster.</summary>
        public int Agree { get; set; }

        /// <summary>Pair matches of this episode in total (worst case: every partner).</summary>
        public int Total { get; set; }
    }

    private sealed class MatchCluster
    {
        public List<(string A, string B, double Start, double End)> Items { get; } = new();
    }

    /// <summary>
    /// Turns a season's pairwise matches into per-episode bounds by consensus voting
    /// instead of longest-match-per-file:
    /// matches are greedily clustered by (start, end) within tolerance, then winning
    /// clusters are extracted one at a time — each must cover at least half of the files
    /// still unassigned (by file count or by possible-pair count). Recaps and OVAs that
    /// match nobody consistently never reach that threshold and are dropped instead of
    /// poisoning the season; a second winning cluster (mid-season OP/ED swap, 24-episode
    /// two-cour show) automatically covers the remaining files. Cluster bounds are the
    /// median of the members, and the (nearly) zero cold-open rule is applied to the
    /// median, not hardcoded per match.
    /// </summary>
    private static Dictionary<string, ConsensusBound> BuildConsensus(
        List<Job> queue,
        List<(string A, string B, double Start, double End)> matches,
        ILogger logger,
        string context)
    {
        var result = new Dictionary<string, ConsensusBound>(StringComparer.OrdinalIgnoreCase);
        if (matches.Count == 0)
        {
            return result;
        }

        var remaining = new HashSet<string>(queue.Select(j => j.Path), StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var active = matches.Where(m => remaining.Contains(m.A) && remaining.Contains(m.B)).ToList();
            if (active.Count == 0)
            {
                break;
            }

            MatchCluster? best = null;
            var bestFiles = 0;
            var bestMedian = (Start: 0.0, End: 0.0);
            foreach (var cluster in ClusterRanges(active))
            {
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in cluster.Items)
                {
                    files.Add(item.A);
                    files.Add(item.B);
                }

                if (files.Count < 2)
                {
                    continue;
                }

                var remainingCount = Math.Max(1, remaining.Count);
                var pairFraction = (double)(files.Count * (files.Count - 1)) / (remainingCount * (remainingCount - 1));
                var fileFraction = (double)files.Count / remainingCount;
                if (pairFraction < 0.5 && fileFraction < 0.5)
                {
                    continue;
                }

                var median = MedianRange(cluster.Items);
                if (best is null ||
                    files.Count > bestFiles ||
                    (files.Count == bestFiles && median.End - median.Start > bestMedian.End - bestMedian.Start))
                {
                    best = cluster;
                    bestFiles = files.Count;
                    bestMedian = median;
                }
            }

            if (best is null)
            {
                break;
            }

            var medianStart = bestMedian.Start;
            var medianEnd = bestMedian.End;
            if (medianStart <= 0.5)
            {
                // Only the consensus may flatten the start to 0 (an intro that genuinely
                // begins at absolute zero) — per-match "start <= 5 → 0" flattening used
                // to swallow 3-5s cold-open bumpers.
                medianStart = 0;
            }

            foreach (var file in best.Items.SelectMany(m => new[] { m.A, m.B }).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!remaining.Remove(file))
                {
                    continue;
                }

                var agree = 0;
                var total = 0;
                foreach (var m in matches)
                {
                    if (!string.Equals(m.A, file, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(m.B, file, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    total++;
                    if (Math.Abs(m.Start - medianStart) <= ConsensusToleranceSeconds &&
                        Math.Abs(m.End - medianEnd) <= ConsensusToleranceSeconds)
                    {
                        agree++;
                    }
                }

                result[file] = new ConsensusBound { Start = medianStart, End = medianEnd, Agree = agree, Total = total };
            }

            logger.LogInformation(
                "[Episode Tools] {Context}: {Count} episode(s) share an intro around {Start:0.0}-{End:0.0}s (cluster of {Pairs} pair match(es)).",
                context,
                bestFiles,
                medianStart,
                medianEnd,
                best.Items.Count);
        }

        return result;
    }

    /// <summary>
    /// Greedily groups matches into clusters whose (start, end) agree within tolerance,
    /// longest matches first so the strongest evidence seeds each cluster.
    /// </summary>
    private static List<MatchCluster> ClusterRanges(List<(string A, string B, double Start, double End)> matches)
    {
        var clusters = new List<MatchCluster>();
        foreach (var match in matches.OrderByDescending(m => m.End - m.Start))
        {
            MatchCluster? target = null;
            foreach (var cluster in clusters)
            {
                var (s, e) = MedianRange(cluster.Items);
                if (Math.Abs(match.Start - s) <= ConsensusToleranceSeconds &&
                    Math.Abs(match.End - e) <= ConsensusToleranceSeconds)
                {
                    target = cluster;
                    break;
                }
            }

            if (target is null)
            {
                target = new MatchCluster();
                clusters.Add(target);
            }

            target.Items.Add(match);
        }

        return clusters;
    }

    private static (double Start, double End) MedianRange(List<(string A, string B, double Start, double End)> items)
    {
        return (Median(items.Select(m => m.Start).OrderBy(v => v).ToList()), Median(items.Select(m => m.End).OrderBy(v => v).ToList()));
    }

    private static double Median(List<double> values)
    {
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    private static List<Job> EnumerateJobs(List<string> folders)
    {
        var videoExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"
        };

        var jobs = new List<Job>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4
        };

        foreach (var folder in folders)
        {
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var showDir in Directory.EnumerateDirectories(folder, "*", options))
            {
                // Deduplicate by full path, never by leaf name: names like "Movie",
                // "Specials" or "Season 1" repeat across trees, and a name-based check
                // silently skipped every folder after the first with that name.
                var dirKey = showDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!seenDirs.Add(dirKey))
                {
                    continue;
                }

                var show = Path.GetFileName(dirKey);
                foreach (var file in SafeEnumerateFiles(dirKey, options))
                {
                    var ext = Path.GetExtension(file.Path);
                    if (!videoExt.Contains(ext) || !seenFiles.Add(file.Path))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(file.Path);
                    var m = SeasonRegex.Match(name);
                    var season = m.Success ? $"S{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture):00}" : "S?";

                    // Compare only within the file's own directory: episodes of one season
                    // live in one folder, and this keeps merged/cross-show groups (whole NAS
                    // shares enumerated as a single show) from producing O(n^2) garbage work.
                    var seasonKey = Path.GetDirectoryName(file.Path) + "|" + season;

                    jobs.Add(new Job
                    {
                        Path = file.Path,
                        Show = show,
                        Season = season,
                        SeasonKey = seasonKey,

                        // Size/mtime come from the same directory enumeration (no extra
                        // SMB/NFS round trip) and back every cache validation below.
                        Size = file.Size,
                        ModifiedUtc = file.MtimeUtc
                    });
                }
            }
        }

        return jobs;
    }

    /// <summary>
    /// Enumerates video files with their size and mtime in the same directory listing pass
    /// (FileSystemEnumerable reads the dirent metadata, so no extra per-file stat is needed
    /// on SMB/NFS mounts).
    /// </summary>
    private static IEnumerable<(string Path, long Size, double MtimeUtc)> SafeEnumerateFiles(string dir, EnumerationOptions options)
    {
        try
        {
            var enumerable = new FileSystemEnumerable<(string Path, long Size, double MtimeUtc)>(
                dir,
                (ref FileSystemEntry entry) =>
                    (entry.ToFullName(), entry.Length, entry.LastWriteTimeUtc.UtcDateTime.Subtract(DateTime.UnixEpoch).TotalSeconds),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory
            };

            return enumerable.ToList();
        }
        catch (Exception)
        {
            return Array.Empty<(string Path, long Size, double MtimeUtc)>();
        }
    }

    // ------------------------------------------------------------------ fingerprinting

    private static string CacheFileFor(string cacheDir, string path, int window, string kind = "")
    {
        // Intros keep the original key so existing caches stay valid; outros get their own
        // key space (separate suffix), and the window is part of the key either way, so a
        // window change naturally invalidates the affected entries.
        var key = kind.Length == 0
            ? path + "|" + window.ToString(CultureInfo.InvariantCulture)
            : kind + "|" + path + "|" + window.ToString(CultureInfo.InvariantCulture);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, hash + ".json");
    }

    private static bool TryLoadCached(
        string cacheDir, string path, int window, long size, double mtimeUtc, out uint[] points)
    {
        points = Array.Empty<uint>();
        try
        {
            var cacheFile = CacheFileFor(cacheDir, path, window);
            if (!File.Exists(cacheFile))
            {
                return false;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cacheFile));
            var root = doc.RootElement;
            var cachedSize = root.GetProperty("size").GetInt64();
            var cachedMtime = root.GetProperty("mtime").GetDouble();
            if (cachedSize != size || Math.Abs(cachedMtime - mtimeUtc) > 2)
            {
                return false;
            }

            var b64 = root.GetProperty("points").GetString() ?? string.Empty;
            var raw = Convert.FromBase64String(b64);
            points = new uint[raw.Length / 4];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = BitConverter.ToUInt32(raw, i * 4);
            }

            return points.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SaveCached(string cacheDir, Job job, int window)
    {
        try
        {
            var bytes = new byte[job.Points!.Length * 4];
            for (var i = 0; i < job.Points.Length; i++)
            {
                BitConverter.GetBytes(job.Points[i]).CopyTo(bytes, i * 4);
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                path = job.Path,
                size = job.Size,
                mtime = job.ModifiedUtc,
                points = Convert.ToBase64String(bytes)
            });
            File.WriteAllText(CacheFileFor(cacheDir, job.Path, window), payload);
        }
        catch (Exception)
        {
            // Caching is best effort.
        }
    }

    /// <summary>Path of the cache index file that powers parse-free pruning.</summary>
    private static string CacheIndexPath(string cacheDir) =>
        Path.Combine(cacheDir, "index.json");

    /// <summary>
    /// Writes the cache index (one JSON file listing every live episode's cache file).
    /// Best effort: scanning works without it, pruning just finds nothing to drop.
    /// </summary>
    private static void SaveCacheIndex(string cacheDir, IReadOnlyCollection<string> cacheFileNames)
    {
        try
        {
            var tmp = CacheIndexPath(cacheDir) + ".tmp";
            File.WriteAllText(
                tmp,
                System.Text.Json.JsonSerializer.Serialize(
                    cacheFileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)));
            File.Move(tmp, CacheIndexPath(cacheDir), true);
        }
        catch (Exception)
        {
            // The index only powers pruning; a failed write costs some re-pruning.
        }
    }

    /// <summary>
    /// Deletes cache entries that the index does not list (deleted or renamed episodes,
    /// corrupt or foreign files), so the cache does not grow forever. Fingerprints are
    /// recomputed on demand, so deleting can never lose data.
    /// </summary>
    private static void PruneCache(string cacheDir, HashSet<string> liveCacheFiles)
    {
        try
        {
            foreach (var cacheFile in Directory.EnumerateFiles(cacheDir, "*.json"))
            {
                var name = Path.GetFileName(cacheFile);
                if (string.Equals(name, "index.json", StringComparison.OrdinalIgnoreCase) ||
                    liveCacheFiles.Contains(name))
                {
                    continue;
                }

                try
                {
                    File.Delete(cacheFile);
                }
                catch (Exception)
                {
                    // Ignore; it will be retried on the next scan.
                }
            }
        }
        catch (Exception)
        {
            // Pruning is best effort.
        }
    }

    private static async Task<uint[]?> FingerprintAsync(
        string path, int windowSeconds, ILogger logger, CancellationToken ct)
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "warning",
            "-t", windowSeconds.ToString(CultureInfo.InvariantCulture),
            "-vn", "-sn", "-dn",
            "-map", "0:a:0",
            "-i", path,
            "-ac", "2",
            "-f", "chromaprint",
            "-fp_format", "raw",
            "-"
        };

        try
        {
            var raw = await RunProcessAsync(FfmpegPath(), args, false, logger, ct).ConfigureAwait(false);
            if (raw.Length == 0 || raw.Length % 4 != 0)
            {
                logger.LogWarning(
                    "[Episode Tools] chromaprint returned {Count} bytes for \"{Path}\".",
                    raw.Length,
                    path);
                return null;
            }

            var points = new uint[raw.Length / 4];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = BitConverter.ToUInt32(raw, i * 4);
            }

            return points;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[Episode Tools] fingerprinting failed for \"{Path}\".",
                path);
            return null;
        }
    }

    private static string? _ffmpegPath;

    /// <summary>
    /// Resolves the ffmpeg executable. Jellyfin's Docker images ship it outside PATH at
    /// /usr/lib/jellyfin-ffmpeg/ffmpeg and expose the location through the JELLYFIN_FFMPEG
    /// environment variable; bare installs usually have it on PATH.
    /// </summary>
    private static string FfmpegPath()
    {
        if (_ffmpegPath is not null)
        {
            return _ffmpegPath;
        }

        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG"),
                     "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                     "/usr/bin/ffmpeg",
                     "/usr/local/bin/ffmpeg",
                     "ffmpeg"
                 })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            try
            {
                if (candidate.Contains(Path.DirectorySeparatorChar) && !File.Exists(candidate))
                {
                    continue;
                }
            }
            catch
            {
                // Fall through to the next candidate.
            }

            _ffmpegPath = candidate;
            return candidate;
        }

        return "ffmpeg";
    }

    private static async Task<byte[]> RunProcessAsync(
        string executable, string[] args, bool stderr, ILogger logger, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = !stderr,
            RedirectStandardError = stderr
        };

        // ArgumentList instead of a formatted command string: paths containing quotes,
        // spaces or other metacharacters must never be re-tokenized by hand (the runtime
        // parses an Arguments string itself on Unix, and a raw double quote breaks it).
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore races on exit
            }
        });

        process.Start();
        try
        {
            // Run below normal priority so scanning never competes with playback or
            // transcoding for CPU (same idea as nice 19 in the convert tasks).
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception)
        {
            // Priority is best effort (e.g. not permitted on some platforms).
        }

        if (stderr)
        {
            var text = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(text);
        }

        var output = new MemoryStream();
        var buffer = new byte[8192];
        var stream = process.StandardOutput;

        while (true)
        {
            var read = await stream.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return output.ToArray();
    }

    // ------------------------------------------------------------------ comparison (ported)

    private static (double Start, double End)? CompareEpisodes(
        uint[] lhs, Dictionary<uint, int> lhsIndex, uint[] rhs, int minIntroDuration, int maxIntroDuration, int windowSeconds)
    {
        var ranges = SearchInvertedIndex(lhs, lhsIndex, rhs);
        TimeRange? best = null;

        foreach (var range in ranges)
        {
            if (best is null || range.Duration > best.Duration)
            {
                best = range;
            }
        }

        if (best is null)
        {
            return null;
        }

        var (start, end) = (best.Start, best.End);

        if (end - start > maxIntroDuration)
        {
            // An over-long shared stretch is not an intro (e.g. whole episodes that match).
            return null;
        }

        // Identical-first-minutes guard: two files sharing most of the fingerprint window
        // (special vs episode 1, identical NCOP + recap builds) are not an intro — a match
        // covering more than 60% of the decoded window is the whole shared head.
        if (windowSeconds > 0 && end - start > 0.6 * windowSeconds)
        {
            return null;
        }

        if (end - start < minIntroDuration)
        {
            return null;
        }

        return (start, end);
    }

    /// <summary>
    /// Builds the value→last-position inverted index for a fingerprint. Built once per left
    /// episode in the O(n²) pairing loop and reused for every partner.
    /// </summary>
    private static Dictionary<uint, int> BuildIndex(uint[] fingerprint)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < fingerprint.Length; i++)
        {
            index[fingerprint[i]] = i;
        }

        return index;
    }

    private static List<TimeRange> SearchInvertedIndex(uint[] lhs, Dictionary<uint, int> lhsIndex, uint[] rhs)
    {
        var rhsIndex = BuildIndex(rhs);

        const int indexShift = 2;
        var shifts = new HashSet<int>();

        foreach (var kvp in lhsIndex)
        {
            for (var i = -indexShift; i <= indexShift; i++)
            {
                var modified = (uint)(kvp.Key + i);
                if (rhsIndex.TryGetValue(modified, out var rhsFirst))
                {
                    shifts.Add(rhsFirst - kvp.Value);
                }
            }
        }

        var result = new List<TimeRange>();
        foreach (var shift in shifts)
        {
            var range = FindContiguous(lhs, rhs, shift, maxDifferences: 6, maximumTimeSkip: 3.5);
            if (range is not null)
            {
                result.Add(range);
            }
        }

        return result;
    }

    private static TimeRange? FindContiguous(uint[] lhs, uint[] rhs, int shift, int maxDifferences, double maximumTimeSkip)
    {
        var leftOffset = 0;
        var rightOffset = 0;
        if (shift < 0)
        {
            leftOffset -= shift;
        }
        else
        {
            rightOffset += shift;
        }

        var lhsTimes = new List<double>();
        var rhsTimes = new List<double>();
        var upperLimit = Math.Min(lhs.Length, rhs.Length) - Math.Abs(shift);

        for (var i = 0; i < upperLimit; i++)
        {
            var diff = lhs[i + leftOffset] ^ rhs[i + rightOffset];
            if (BitOperations.PopCount(diff) > maxDifferences)
            {
                continue;
            }

            lhsTimes.Add((i + leftOffset) * SamplesToSeconds);
            rhsTimes.Add((i + rightOffset) * SamplesToSeconds);
        }

        lhsTimes.Add(double.MaxValue);
        rhsTimes.Add(double.MaxValue);

        var lhsRange = TimeRangeHelpers.FindContiguous(lhsTimes.ToArray(), maximumTimeSkip);
        if (lhsRange is null || lhsRange.Duration < 15)
        {
            return null;
        }

        // Density guard: a contiguous match must contain at least one matching point per
        // ~0.5s of audio, otherwise maximumTimeSkip has bridged a large gap and stitched
        // unrelated stretches (dialogue-heavy shows) into a fake "intro".
        var pointsInRange = 0;
        foreach (var t in lhsTimes)
        {
            if (t >= lhsRange.Start && t <= lhsRange.End)
            {
                pointsInRange++;
            }
        }

        if (pointsInRange / lhsRange.Duration < 2.0)
        {
            return null;
        }

        return lhsRange;
    }

    // ------------------------------------------------------------------ silence snap

    // A snap may only move a boundary this far from the fingerprinted position: a dramatic
    // pause inside the OP song used to pull the skip button up to 15s early (mid-song clip).
    private const double SnapToleranceSeconds = 1.5;

    // Silence near-but-not-at the boundary marks an ambiguous fade/cut; those get a
    // visual (black frame) refinement instead of an audio snap.
    private const double AmbiguousWindowSeconds = 8;

    private static readonly Regex BlackFrameRegex = new(
        @"black_start:(?<start>[0-9.]+)\s*black_end:(?<end>[0-9.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Snaps the end of a matched intro to a silence that sits within ±<see cref="SnapToleranceSeconds"/> of the fingerprinted boundary.
    /// Returns the (possibly unchanged) boundary plus whether an ambiguous silence (within
    /// ±<see cref="AmbiguousWindowSeconds"/>) was rejected by the tolerance — the caller can
    /// then try the visual black-frame refinement.
    /// </summary>
    private static async Task<(double Snapped, bool Ambiguous)> AdjustEndToSilence(
        string path, double introStart, double introEnd, ILogger logger, CancellationToken ct)
    {
        try
        {
            // Decode only the seconds around the matched end: the intro's start region cannot
            // contain the silence we are looking for, so this cuts the decode roughly 5x.
            // Input seeking rebases output timestamps to 0, hence the seek offset below.
            var seek = Math.Max(0, (int)introEnd - 17);
            var args = new[]
            {
                "-hide_banner", "-loglevel", "info",
                "-vn", "-sn", "-dn",
                "-ss", seek.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-to", Math.Max(1, (int)introEnd + 2 - seek).ToString(CultureInfo.InvariantCulture),
                "-af", "silencedetect=noise=-50dB:duration=0.1",
                "-f", "null",
                "-"
            };

            var raw = Encoding.UTF8.GetString(
                await RunProcessAsync(FfmpegPath(), args, true, logger, ct).ConfigureAwait(false));
            var originalEnd = new TimeRange(introEnd - 15, introEnd);
            var ambiguousZone = new TimeRange(introEnd - AmbiguousWindowSeconds, introEnd + AmbiguousWindowSeconds);

            // Among the silences that intersect the end of the matched intro, pick the one
            // that starts CLOSEST to the matched end — but never move the boundary further
            // than SnapToleranceSeconds: a dramatic pause inside the song must not clip it.
            double bestStart = double.MinValue;
            var found = false;
            var ambiguous = false;
            double start = double.MinValue, end = double.MinValue;
            var hasStart = false;
            foreach (Match match in SilenceRegex.Matches(raw))
            {
                var isStart = match.Groups["type"].Value == "start";
                var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture) + seek;
                if (isStart)
                {
                    start = time;
                    hasStart = true;
                }
                else if (hasStart)
                {
                    end = time;
                    var candidate = new TimeRange(start, end);
                    if (candidate.Duration < 0.33 || candidate.Start < introStart)
                    {
                        continue;
                    }

                    if (candidate.Start >= introEnd - SnapToleranceSeconds &&
                        candidate.Start <= introEnd + SnapToleranceSeconds &&
                        candidate.Start > bestStart)
                    {
                        bestStart = candidate.Start;
                        found = true;
                    }
                    else if (ambiguousZone.Intersects(candidate))
                    {
                        ambiguous = true;
                    }
                }
            }

            return (found ? bestStart : introEnd, ambiguous);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Episode Tools] silence detection failed for \"{Path}\".", path);
            return (introEnd, false);
        }
    }

    /// <summary>
    /// Visual refinement for ambiguous boundaries: decodes a few seconds of video around
    /// the boundary and snaps it to the start of a black frame (a fade or a hard cut to
    /// black), which is exact even when music-over-cut makes silence useless. Returns null
    /// when no qualifying black frame exists (caller keeps the fingerprinted boundary).
    /// </summary>
    private static async Task<double?> AdjustBoundaryToBlackFrame(
        string path, double boundary, ILogger logger, CancellationToken ct)
    {
        try
        {
            var seek = Math.Max(0, (int)boundary - 4);
            var args = new[]
            {
                "-hide_banner", "-loglevel", "info",
                "-ss", seek.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-t", "8",
                "-vf", "blackdetect=d=0.06:pic_th=0.98",
                "-f", "null",
                "-"
            };

            var raw = Encoding.UTF8.GetString(
                await RunProcessAsync(FfmpegPath(), args, true, logger, ct).ConfigureAwait(false));

            double? best = null;
            foreach (Match match in BlackFrameRegex.Matches(raw))
            {
                var blackStart = Convert.ToDouble(match.Groups["start"].Value, CultureInfo.InvariantCulture) + seek;
                var blackEnd = Convert.ToDouble(match.Groups["end"].Value, CultureInfo.InvariantCulture) + seek;

                // The boundary sits inside a black frame, or a black frame starts within
                // tolerance of it: snap to the exact black start (never further away than
                // the audio snap tolerance).
                if ((blackStart < boundary && blackEnd > boundary) ||
                    Math.Abs(blackStart - boundary) <= SnapToleranceSeconds)
                {
                    var distance = Math.Abs(blackStart - boundary);
                    if (best is not { } current || distance < Math.Abs(current - boundary))
                    {
                        best = blackStart;
                    }
                }
            }

            if (best is { } snapped)
            {
                logger.LogDebug(
                    "[Episode Tools] boundary for \"{Path}\" refined to a black frame at {Time:0.00}s (was {Boundary:0.00}s).",
                    path,
                    snapped,
                    boundary);
                return Math.Max(0, snapped);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Episode Tools] black frame detection failed for \"{Path}\".", path);
        }

        return null;
    }

    /// <summary>
    /// Outro counterpart of <see cref="AdjustEndToSilence"/>: snaps the START of a matched
    /// credits sequence to a silence within ±<see cref="SnapToleranceSeconds"/> of the
    /// fingerprinted boundary, so a pause before the credits cannot clip the last seconds
    /// of dialogue. Returns the (possibly unchanged) boundary plus whether an ambiguous
    /// silence (within ±<see cref="AmbiguousWindowSeconds"/>) was rejected by the tolerance.
    /// </summary>
    private static async Task<(double Snapped, bool Ambiguous)> AdjustStartToSilence(
        string path, double outroStart, ILogger logger, CancellationToken ct)
    {
        try
        {
            var seek = Math.Max(0, (int)outroStart - 2);
            var args = new[]
            {
                "-hide_banner", "-loglevel", "info",
                "-vn", "-sn", "-dn",
                "-ss", seek.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-to", Math.Max(1, (int)outroStart + 17 - seek).ToString(CultureInfo.InvariantCulture),
                "-af", "silencedetect=noise=-50dB:duration=0.1",
                "-f", "null",
                "-"
            };

            var raw = Encoding.UTF8.GetString(
                await RunProcessAsync(FfmpegPath(), args, true, logger, ct).ConfigureAwait(false));
            var ambiguousZone = new TimeRange(outroStart - AmbiguousWindowSeconds, outroStart + AmbiguousWindowSeconds);

            // Among the silences near the start of the matched credits, pick the one that
            // starts closest to it (dialogue stops, credits begin) — bounded like the intro
            // end snap so mid-credits-music pauses or early dialogue stops cannot move the
            // boundary meaningfully.
            double bestStart = double.MaxValue;
            var matched = false;
            var ambiguous = false;
            double start = double.MinValue, end = double.MinValue;
            var hasStart = false;
            foreach (Match match in SilenceRegex.Matches(raw))
            {
                var isStart = match.Groups["type"].Value == "start";
                var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture) + seek;
                if (isStart)
                {
                    start = time;
                    hasStart = true;
                }
                else if (hasStart)
                {
                    end = time;
                    var candidate = new TimeRange(start, end);
                    if (candidate.Duration < 0.33)
                    {
                        continue;
                    }

                    if (candidate.Start >= outroStart - SnapToleranceSeconds &&
                        candidate.Start <= outroStart + SnapToleranceSeconds &&
                        candidate.Start < bestStart)
                    {
                        bestStart = candidate.Start;
                        matched = true;
                    }
                    else if (ambiguousZone.Intersects(candidate))
                    {
                        ambiguous = true;
                    }
                }
            }

            return (matched ? Math.Max(0, bestStart) : outroStart, ambiguous);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Episode Tools] outro silence detection failed for \"{Path}\".", path);
            return (outroStart, false);
        }
    }

    // ------------------------------------------------------------------ outros (F2)

    private static string OutroCacheFileFor(string cacheDir, string path, int window) =>
        CacheFileFor(cacheDir, path, window, "outro");

    private static bool TryLoadCachedOutro(
        string cacheDir, string path, int window, long size, double mtimeUtc, out (uint[] Points, double Duration) cached)
    {
        cached = (Array.Empty<uint>(), 0);
        try
        {
            var cacheFile = OutroCacheFileFor(cacheDir, path, window);
            if (!File.Exists(cacheFile))
            {
                return false;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cacheFile));
            var root = doc.RootElement;
            var cachedSize = root.GetProperty("size").GetInt64();
            var cachedMtime = root.GetProperty("mtime").GetDouble();
            if (cachedSize != size || Math.Abs(cachedMtime - mtimeUtc) > 2)
            {
                return false;
            }

            var b64 = root.GetProperty("points").GetString() ?? string.Empty;
            var raw = Convert.FromBase64String(b64);
            var points = new uint[raw.Length / 4];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = BitConverter.ToUInt32(raw, i * 4);
            }

            if (points.Length == 0)
            {
                return false;
            }

            var duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
            if (duration <= 0)
            {
                return false;
            }

            cached = (points, duration);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SaveCachedOutro(
        string cacheDir, Job job, int window, uint[] points, double duration)
    {
        try
        {
            var bytes = new byte[points.Length * 4];
            for (var i = 0; i < points.Length; i++)
            {
                BitConverter.GetBytes(points[i]).CopyTo(bytes, i * 4);
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                path = job.Path,
                size = job.Size,
                mtime = job.ModifiedUtc,
                duration,
                points = Convert.ToBase64String(bytes)
            });
            File.WriteAllText(OutroCacheFileFor(cacheDir, job.Path, window), payload);
        }
        catch (Exception)
        {
            // Caching is best effort.
        }
    }

    private static string FfprobePath()
    {
        var env = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG");
        foreach (var candidate in new[]
                 {
                     string.IsNullOrEmpty(env) ? null : Path.Combine(Path.GetDirectoryName(env) ?? ".", "ffprobe"),
                     "/usr/lib/jellyfin-ffmpeg/ffprobe",
                     "/usr/bin/ffprobe",
                     "/usr/local/bin/ffprobe",
                     "ffprobe"
                 })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            if (!candidate.Contains(Path.DirectorySeparatorChar) || File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffprobe";
    }

    /// <summary>Path of the per-file metadata cache entry (chapters + duration).</summary>
    private static string MetaCacheFileFor(string cacheDir, string path) =>
        CacheFileFor(cacheDir, path, 0, "meta");

    private static bool TryLoadMeta(
        string cacheDir,
        string path,
        long size,
        double mtimeUtc,
        out (double Duration, List<ChapterPoint> Chapters) meta)
    {
        meta = (0, new List<ChapterPoint>());
        try
        {
            var cacheFile = MetaCacheFileFor(cacheDir, path);
            if (!File.Exists(cacheFile))
            {
                return false;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cacheFile));
            var root = doc.RootElement;
            if (root.GetProperty("size").GetInt64() != size ||
                Math.Abs(root.GetProperty("mtime").GetDouble() - mtimeUtc) > 2)
            {
                return false;
            }

            var duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
            var chapters = new List<ChapterPoint>();
            if (root.TryGetProperty("chapters", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    chapters.Add(new ChapterPoint
                    {
                        Start = c.GetProperty("s").GetDouble(),
                        End = c.GetProperty("e").GetDouble(),
                        Title = c.TryGetProperty("t", out var t) ? (t.GetString() ?? string.Empty) : string.Empty
                    });
                }
            }

            meta = (duration, chapters);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SaveMeta(
        string cacheDir, Job job, double duration, List<ChapterPoint> chapters)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                path = job.Path,
                size = job.Size,
                mtime = job.ModifiedUtc,
                duration,
                chapters = chapters.Select(c => new { s = c.Start, e = c.End, t = c.Title }).ToList()
            });
            File.WriteAllText(MetaCacheFileFor(cacheDir, job.Path), payload);
        }
        catch (Exception)
        {
            // Caching is best effort.
        }
    }

    /// <summary>
    /// Probes chapters and duration with a single ffprobe call. Returns null when the file
    /// could not be probed at all (the scanner then treats it as chapterless and the outro
    /// pass skips it, matching the old per-pass behavior).
    /// </summary>
    private static async Task<(double Duration, List<ChapterPoint> Chapters)?> ProbeMetadataAsync(
        string path, ILogger logger, CancellationToken ct)
    {
        try
        {
            var args = new[]
            {
                "-v", "error",
                "-show_chapters",
                "-show_entries", "format=duration",
                "-of", "json",
                path
            };

            var raw = Encoding.UTF8.GetString(
                await RunProcessAsync(FfprobePath(), args, false, logger, ct).ConfigureAwait(false));
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            double duration = 0;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var dur) &&
                !double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration))
            {
                duration = 0;
            }

            var chapters = new List<ChapterPoint>();
            if (root.TryGetProperty("chapters", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    // start_time/end_time are the human-seconds variants ffprobe prints for
                    // chapters; the raw start/end fields are in the (variable) timebase.
                    if (!c.TryGetProperty("start_time", out var st) ||
                        !c.TryGetProperty("end_time", out var et) ||
                        !double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
                        !double.TryParse(et.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
                    {
                        continue;
                    }

                    var title = string.Empty;
                    if (c.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var t))
                    {
                        title = t.GetString() ?? string.Empty;
                    }

                    chapters.Add(new ChapterPoint { Start = start, End = end, Title = title });
                }
            }

            return (duration, chapters);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Episode Tools] metadata probe failed for \"{Path}\".", path);
            return null;
        }
    }

    /// <summary>
    /// Fingerprints the last <paramref name="windowSeconds"/> seconds of an episode
    /// (outro/credits live at the end). The file duration comes from the shared metadata
    /// probe (no second ffprobe here) and converts relative point indices into seconds.
    /// </summary>
    private static async Task<(uint[] Points, double Duration)?> FingerprintOutroAsync(
        string path, int windowSeconds, double duration, ILogger logger, CancellationToken ct)
    {
        try
        {
            if (duration <= 0)
            {
                logger.LogWarning(
                    "[Episode Tools] could not read the duration of \"{Path}\"; skipping its outro fingerprint.",
                    path);
                return null;
            }

            var args = new[]
            {
                "-hide_banner", "-loglevel", "warning",
                "-sseof", "-" + windowSeconds.ToString(CultureInfo.InvariantCulture),
                "-i", path,
                "-t", windowSeconds.ToString(CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn",
                "-map", "0:a:0",
                "-ac", "2",
                "-f", "chromaprint",
                "-fp_format", "raw",
                "-"
            };

            var raw = await RunProcessAsync(FfmpegPath(), args, false, logger, ct).ConfigureAwait(false);
            if (raw.Length == 0 || raw.Length % 4 != 0)
            {
                logger.LogWarning(
                    "[Episode Tools] chromaprint returned {Count} bytes for the outro of \"{Path}\".",
                    raw.Length,
                    path);
                return null;
            }

            var points = new uint[raw.Length / 4];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = BitConverter.ToUInt32(raw, i * 4);
            }

            return points.Length > 0 ? (points, duration) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Episode Tools] outro fingerprinting failed for \"{Path}\".", path);
            return null;
        }
    }

    /// <summary>
    /// Second scan pass for outros/credits: fingerprints the last minutes of every episode
    /// (separate cache), pairs them per season with the same inverted-index matching, then
    /// snaps the matched start to silence. Results are written into the shared
    /// <see cref="IntroResult"/> entries as OutroStart/OutroEnd.
    /// </summary>
    private static async Task ScanOutrosAsync(
        PluginConfiguration cfg,
        List<Job> jobs,
        List<IGrouping<string, Job>> seasons,
        string introCacheDir,
        Dictionary<string, (double Duration, List<ChapterPoint> Chapters)> meta,
        Dictionary<string, (double Start, double End, string Label)> chapterOutro,
        int minDuration,
        int maxDuration,
        Dictionary<string, IntroResult> found,
        Action writeState,
        ILogger logger,
        CancellationToken ct)
    {
        var window = Math.Clamp(cfg.OutroWindowMinutes, 1, 15) * 60;
        var parallelism = Math.Clamp(cfg.IntroParallelism, 1, 8);
        var cacheDir = Path.Combine(introCacheDir, "outros");
        Directory.CreateDirectory(cacheDir);

        logger.LogInformation(
            "[Episode Tools] Outro scan: {Jobs} episode(s), last {Minutes} minute(s) of each.",
            jobs.Count,
            window / 60);

        var points = new Dictionary<string, (uint[] Points, double Duration)>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<Job>();
        var noDuration = 0;
        foreach (var job in jobs)
        {
            if (chapterOutro.ContainsKey(job.Path))
            {
                // Frame-exact from the container: no fingerprint, no pairing needed.
                continue;
            }

            if (TryLoadCachedOutro(cacheDir, job.Path, window, job.Size, job.ModifiedUtc, out var cached))
            {
                points[job.Path] = cached;
            }
            else if (meta.TryGetValue(job.Path, out var m) && m.Duration > 0)
            {
                missing.Add(job);
            }
            else
            {
                noDuration++;
            }
        }

        if (noDuration > 0)
        {
            logger.LogInformation(
                "[Episode Tools] Outro scan: skipping {Count} episode(s) whose duration could not be read.",
                noDuration);
        }

        // Chapter-derived outros are frame-exact; stamp them before the pairing pass (with
        // the same base start/end the intro pass found) so they survive the season loop.
        foreach (var job in jobs)
        {
            if (chapterOutro.TryGetValue(job.Path, out var co))
            {
                StampOutro(found, job, co.Start, co.End, "chapter", $"Chapter: \"{co.Label}\"");
            }
        }

        if (chapterOutro.Count > 0)
        {
            writeState();
        }

        var totalMissing = missing.Count;
        var done = 0;
        if (totalMissing > 0)
        {
            _lastMessage = $"outro fingerprinting 0/{totalMissing} episode(s)";
            await Parallel.ForEachAsync(
                missing,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = ct
                },
                async (job, token) =>
                {
                    var duration = meta.TryGetValue(job.Path, out var m) ? m.Duration : 0;
                    var res = await FingerprintOutroAsync(job.Path, window, duration, logger, token).ConfigureAwait(false);
                    if (res is { } r)
                    {
                        points[job.Path] = r;
                        SaveCachedOutro(cacheDir, job, window, r.Points, r.Duration);
                    }

                    var count = Interlocked.Increment(ref done);
                    _lastProgress = 100.0 * count / Math.Max(1, totalMissing);
                    _lastMessage = $"outro fingerprinting {count}/{totalMissing} episode(s)";
                }).ConfigureAwait(false);
        }

        var groups = seasons.Where(s => s.Count() >= 2).ToList();
        var seasonIndex = 0;
        foreach (var season in groups)
        {
            seasonIndex++;
            ct.ThrowIfCancellationRequested();
            await PlaybackGuard.WaitWhilePlaybackActiveAsync(logger, ct).ConfigureAwait(false);

            var queue = season.Where(j => points.ContainsKey(j.Path)).ToList();
            if (queue.Count < 2)
            {
                continue;
            }

            _lastMessage = $"outro matching season {seasonIndex}/{groups.Count} ({queue.Count} episode(s))";

            var pairMatches = new List<(string A, string B, double Start, double End)>();
            for (var i = 0; i < queue.Count; i++)
            {
                // One inverted index per left episode, reused across the whole inner loop.
                var lhsIndex = BuildIndex(queue[i].Points!);
                for (var k = i + 1; k < queue.Count; k++)
                {
                    var match = CompareEpisodes(queue[i].Points!, lhsIndex, queue[k].Points!, minDuration, maxDuration, window);
                    if (match is not null)
                    {
                        pairMatches.Add((queue[i].Path, queue[k].Path, match.Value.Item1, match.Value.Item2));
                    }
                }
            }

            var bounds = BuildConsensus(queue, pairMatches, logger, $"outro season {seasonIndex}/{groups.Count}");

            // A match that disappeared must disappear from the results too: clear this
            // season's stored outros before writing the fresh ones. Chapter-derived
            // outros are frame-exact and never cleared by the pairing result.
            foreach (var job in season)
            {
                if (!chapterOutro.ContainsKey(job.Path) &&
                    found.TryGetValue(job.Path, out var entry) &&
                    (entry.OutroStart is not null || entry.OutroEnd is not null))
                {
                    found[job.Path] = new IntroResult
                    {
                        File = entry.File,
                        Show = entry.Show,
                        Season = entry.Season,
                        Start = entry.Start,
                        End = entry.End,
                        Source = entry.Source,
                        Detail = entry.Detail
                    };
                }
            }

            var toSnap = queue
                .Where(j => bounds.ContainsKey(j.Path))
                .Select(j => (Job: j, Bound: bounds[j.Path]))
                .ToList();
            if (toSnap.Count == 0)
            {
                writeState();
                continue;
            }

            _lastMessage = $"outro snapping {toSnap.Count} credit(s) (season {seasonIndex}/{groups.Count})";
            var matched = new ConcurrentDictionary<string, (Job Job, double Start, double End)>(StringComparer.OrdinalIgnoreCase);

            await Parallel.ForEachAsync(
                toSnap,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism,
                    CancellationToken = ct
                },
                async (item, token) =>
                {
                    var b = item.Bound;
                    var duration = points[item.Job.Path].Duration;
                    var baseOffset = Math.Max(0, duration - window);
                    var start = baseOffset + b.Start;
                    var end = Math.Min(baseOffset + b.End, Math.Max(duration, start + 1));
                    var (snapped, ambiguous) = await AdjustStartToSilence(item.Job.Path, start, logger, token).ConfigureAwait(false);
                    if (snapped == start && ambiguous)
                    {
                        // Ambiguous silence near the credits start (fade/cut without audio
                        // change): try the visual black frame refinement.
                        var visual = await AdjustBoundaryToBlackFrame(item.Job.Path, start, logger, token).ConfigureAwait(false);
                        if (visual is { } v)
                        {
                            snapped = v;
                        }
                    }

                    if (end - snapped < minDuration)
                    {
                        // The snap shrank the outro below the configured minimum; keep the
                        // fingerprinted start instead.
                        snapped = start;
                    }

                    matched[item.Job.Path] = (item.Job, snapped, end);
                }).ConfigureAwait(false);

            foreach (var kvp in matched)
            {
                var bound = bounds.TryGetValue(kvp.Value.Job.Path, out var bb) ? bb : null;
                StampOutro(
                    found,
                    kvp.Value.Job,
                    kvp.Value.Start,
                    kvp.Value.End,
                    "fingerprint",
                    bound is null ? null : $"Fingerprint ({bound.Agree}/{bound.Total})");
            }

            writeState();
            logger.LogInformation(
                "[Episode Tools] Outro scan: season {Index}/{Total} done; {Count} outro(s) so far.",
                seasonIndex,
                groups.Count,
                found.Values.Count(r => r.OutroStart is not null));
        }

        // Prune the outro cache with its own index (it lives in a subfolder, so the intro
        // prune above never touches it).
        var liveOutroCacheFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (points.ContainsKey(job.Path))
            {
                liveOutroCacheFiles.Add(Path.GetFileName(OutroCacheFileFor(cacheDir, job.Path, window)));
            }
        }

        SaveCacheIndex(cacheDir, liveOutroCacheFiles);
        PruneCache(cacheDir, liveOutroCacheFiles);

        var outroCount = found.Values.Count(r => r.OutroStart is not null);
        logger.LogInformation(
            "[Episode Tools] Outro scan finished: {Count} outro(s) detected.",
            outroCount);
    }

    /// <summary>Tombstone entry marking an episode that produced no usable segment.</summary>
    private static IntroResult SkippedResult(Job job, string reason) => new()
    {
        File = job.Path,
        Show = job.Show,
        Season = job.Season,
        Source = "skipped",
        Detail = reason
    };

    /// <summary>
    /// Writes one outro (from chapters or from pairing) into the results, preserving the
    /// intro bounds the intro pass found for the same file. A tombstone (skipped intro)
    /// is replaced by the outro's own provenance.
    /// </summary>
    private static void StampOutro(
        Dictionary<string, IntroResult> found,
        Job job,
        double start,
        double end,
        string? source,
        string? detail)
    {
        var baseResult = found.TryGetValue(job.Path, out var existing) ? existing : null;
        var keepBase = baseResult is not null && baseResult.Source != "skipped";
        found[job.Path] = new IntroResult
        {
            File = job.Path,
            Show = job.Show,
            Season = job.Season,
            Start = keepBase ? baseResult!.Start : 0,
            End = keepBase ? baseResult!.End : 0,
            Source = keepBase ? baseResult!.Source : source,
            Detail = keepBase ? baseResult!.Detail : detail,
            OutroStart = Math.Round(start, 2),
            OutroEnd = Math.Round(end, 2)
        };
    }

    /// <summary>Range of contiguous time.</summary>
    private sealed class TimeRange : IComparable
    {
        public TimeRange()
        {
        }

        public TimeRange(double start, double end)
        {
            Start = start;
            End = end;
        }

        public TimeRange(TimeRange original)
        {
            Start = original.Start;
            End = original.End;
        }

        public double Start { get; set; }

        public double End { get; set; }

        public double Duration => End - Start;

        public int CompareTo(object? obj)
        {
            if (obj is not TimeRange tr)
            {
                throw new ArgumentException("obj must be a TimeRange");
            }

            return tr.Duration.CompareTo(Duration);
        }

        public bool Intersects(TimeRange tr)
        {
            return
                (Start < tr.Start && tr.Start < End) ||
                (Start < tr.End && tr.End < End) ||
                (tr.Start <= Start && End <= tr.End);
        }
    }

    private static class TimeRangeHelpers
    {
        public static TimeRange? FindContiguous(double[] times, double maximumDistance)
        {
            if (times.Length == 0)
            {
                return null;
            }

            Array.Sort(times);
            var ranges = new List<TimeRange>();
            var current = new TimeRange(times[0], times[0]);

            for (var i = 0; i < times.Length - 1; i++)
            {
                var next = times[i + 1];
                if (next - times[i] <= maximumDistance)
                {
                    current.End = next;
                    continue;
                }

                ranges.Add(new TimeRange(current));
                current = new TimeRange(next, next);
            }

            ranges.Sort();
            return ranges.Count > 0 ? ranges[0] : null;
        }
    }
}
