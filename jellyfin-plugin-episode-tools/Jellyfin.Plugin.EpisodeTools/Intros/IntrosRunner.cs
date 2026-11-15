using System;
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

    /// <summary>Starts a scan. Returns false when one is already running.</summary>
    public static bool TryStart(ILogger logger)
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
                        await RunAsync(logger, ct).ConfigureAwait(false);
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
                message = Running ? _lastMessage : string.Empty,
                progress = Running ? _lastProgress : 0.0
            };
        }
    }

    /// <summary>Path of the results file.</summary>
    private static string ResultsPath =>
        Path.Combine(Plugin.Instance?.WorkFolder ?? ".", "intros.json");

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

    /// <summary>Loads the latest results (empty list when nothing was scanned yet).</summary>
    public static IReadOnlyList<IntroResult> LoadResults()
    {
        try
        {
            var file = ResultsPath;
            if (Plugin.Instance is null || !File.Exists(file))
            {
                return Array.Empty<IntroResult>();
            }

            var json = File.ReadAllText(file);
            var state = System.Text.Json.JsonSerializer.Deserialize<IntrosState>(
                json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            return state?.Intros ?? new List<IntroResult>();
        }
        catch (Exception)
        {
            return Array.Empty<IntroResult>();
        }
    }

    private sealed class IntrosState
    {
        public int Version { get; set; } = 1;

        public DateTime ScannedAt { get; set; }

        public int WindowSeconds { get; set; }

        public List<IntroResult> Intros { get; set; } = new();
    }

    private sealed class Job
    {
        public string Path { get; set; } = string.Empty;

        public string Show { get; set; } = string.Empty;

        public string Season { get; set; } = string.Empty;

        public string SeasonKey { get; set; } = string.Empty;

        public uint[]? Points { get; set; }
    }

    /// <summary>The scan itself.</summary>
    private static async Task RunAsync(ILogger logger, CancellationToken ct)
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

        // Every video file under every configured show folder, grouped per (show folder, season).
        var jobs = EnumerateJobs(cfg);
        if (jobs.Count == 0)
        {
            _lastMessage = "no episodes found in the configured folders";
            return;
        }

        var cacheDir = Path.Combine(work, "intros-fpcache");
        Directory.CreateDirectory(cacheDir);

        // Fingerprint what is missing from the cache, in parallel.
        var missing = new List<Job>();
        foreach (var job in jobs)
        {
            if (TryLoadCached(cacheDir, job.Path, window, out var points))
            {
                job.Points = points;
            }
            else
            {
                missing.Add(job);
            }
        }

        var done = 0;
        var total = missing.Count;
        _lastMessage = $"fingerprinting {total} episode(s)";

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
                _lastProgress = 100.0 * count / Math.Max(1, total);
                _lastMessage = $"fingerprinting {count}/{total} episodes";
            }).ConfigureAwait(false);

        // Compare seasons: every episode against the episodes that come after it.
        _lastMessage = "comparing episodes";
        var found = new Dictionary<string, IntroResult>(StringComparer.OrdinalIgnoreCase);
        var seasons = jobs.GroupBy(j => j.SeasonKey, StringComparer.OrdinalIgnoreCase);

        foreach (var season in seasons)
        {
            ct.ThrowIfCancellationRequested();

            var queue = new List<Job>(season.Where(j => j.Points is { Length: > 0 }));
            var bounds = new Dictionary<string, (double Start, double End)>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < queue.Count; i++)
            {
                for (var k = i + 1; k < queue.Count; k++)
                {
                    var match = CompareEpisodes(queue[i].Points!, queue[k].Points!, minDuration, maxDuration);
                    if (match is null)
                    {
                        continue;
                    }

                    SaveBetter(bounds, queue[i].Path, match.Value.Item1, match.Value.Item2);
                    SaveBetter(bounds, queue[k].Path, match.Value.Item1, match.Value.Item2);
                }
            }

            // Snap intro ends to silence, like the official scanner does.
            foreach (var job in queue)
            {
                if (!bounds.TryGetValue(job.Path, out var b))
                {
                    continue;
                }

                var end = AdjustEndToSilence(job.Path, b.Start, b.End, logger, ct);
                found[job.Path] = new IntroResult
                {
                    File = job.Path,
                    Show = job.Show,
                    Season = job.Season,
                    Start = Math.Round(b.Start, 2),
                    End = Math.Round(end, 2)
                };
            }
        }

        var results = found.Values
            .OrderBy(r => r.Show, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var state = new IntrosState
        {
            ScannedAt = DateTime.UtcNow,
            WindowSeconds = window,
            Intros = results
        };

        Directory.CreateDirectory(work);
        File.WriteAllText(
            ResultsPath,
            System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            }));

        _lastMessage = $"finished: {results.Count} intro(s) in {jobs.Count} episode(s)";
        _lastProgress = 100;
        logger.LogInformation(
            "[Episode Tools] Fast intro scan finished: {Count} intro(s) from {Episodes} episode(s).",
            results.Count,
            jobs.Count);
    }

    private static void SaveBetter(
        Dictionary<string, (double Start, double End)> bounds,
        string path,
        double start,
        double end)
    {
        var duration = end - start;
        if (bounds.TryGetValue(path, out var existing) && existing.End - existing.Start >= duration)
        {
            return;
        }

        bounds[path] = (start, end);
    }

    private static List<Job> EnumerateJobs(PluginConfiguration cfg)
    {
        var folders = new List<string>(cfg.EffectiveRenameFolders);
        foreach (var f in cfg.EffectiveConvertFolders)
        {
            if (!folders.Contains(f, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(f);
            }
        }

        var videoExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"
        };

        var jobs = new List<Job>();
        var seenShows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };

        foreach (var folder in folders)
        {
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var showDir in Directory.EnumerateDirectories(folder, "*", options))
            {
                var show = Path.GetFileName(showDir);
                if (!seenShows.Add(show))
                {
                    continue;
                }

                foreach (var file in SafeEnumerateFiles(showDir))
                {
                    var ext = Path.GetExtension(file);
                    if (!videoExt.Contains(ext))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(file);
                    var m = SeasonRegex.Match(name);
                    var season = m.Success ? $"S{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture):00}" : "S?";

                    jobs.Add(new Job
                    {
                        Path = file,
                        Show = show,
                        Season = season,
                        SeasonKey = showDir + "|" + season
                    });
                }
            }
        }

        return jobs;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------ fingerprinting

    private static string CacheFileFor(string cacheDir, string path, int window)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            path + "|" + window.ToString(CultureInfo.InvariantCulture))));
        return Path.Combine(cacheDir, hash + ".json");
    }

    private static bool TryLoadCached(string cacheDir, string path, int window, out uint[] points)
    {
        points = Array.Empty<uint>();
        try
        {
            var cacheFile = CacheFileFor(cacheDir, path, window);
            if (!File.Exists(cacheFile))
            {
                return false;
            }

            var info = new FileInfo(path);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cacheFile));
            var root = doc.RootElement;
            var size = root.GetProperty("size").GetInt64();
            var mtime = root.GetProperty("mtime").GetDouble();
            if (size != info.Length || Math.Abs(mtime - info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds) > 2)
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
            var info = new FileInfo(job.Path);
            var bytes = new byte[job.Points!.Length * 4];
            for (var i = 0; i < job.Points.Length; i++)
            {
                BitConverter.GetBytes(job.Points[i]).CopyTo(bytes, i * 4);
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                size = info.Length,
                mtime = info.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalSeconds,
                points = Convert.ToBase64String(bytes)
            });
            File.WriteAllText(CacheFileFor(cacheDir, job.Path, window), payload);
        }
        catch (Exception)
        {
            // Caching is best effort.
        }
    }

    private static async Task<uint[]?> FingerprintAsync(
        string path, int windowSeconds, ILogger logger, CancellationToken ct)
    {
        var args = string.Format(
            CultureInfo.InvariantCulture,
            "-hide_banner -loglevel warning -t {0} -i \"{1}\" -ac 2 -f chromaprint -fp_format raw -",
            windowSeconds,
            path);

        try
        {
            var raw = await RunProcessAsync("ffmpeg", args, false, logger, ct).ConfigureAwait(false);
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

    private static async Task<byte[]> RunProcessAsync(
        string executable, string args, bool stderr, ILogger logger, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = !stderr,
            RedirectStandardError = stderr
        };

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

        var output = new MemoryStream();
        var buffer = new byte[8192];
        var stream = stderr ? process.StandardError : process.StandardOutput;

        if (stderr)
        {
            var text = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(text);
        }

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
        uint[] lhs, uint[] rhs, int minIntroDuration, int maxIntroDuration)
    {
        var ranges = SearchInvertedIndex(lhs, rhs);
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
        if (start <= 5)
        {
            start = 0;
        }

        if (end - start > maxIntroDuration)
        {
            // Keep only the tail end of an over-long match; intros are short.
            return null;
        }

        if (end - start < minIntroDuration)
        {
            return null;
        }

        return (start, end);
    }

    private static List<TimeRange> SearchInvertedIndex(uint[] lhs, uint[] rhs)
    {
        var lhsIndex = new Dictionary<uint, int>();
        for (var i = 0; i < lhs.Length; i++)
        {
            lhsIndex[lhs[i]] = i;
        }

        var rhsIndex = new Dictionary<uint, int>();
        for (var i = 0; i < rhs.Length; i++)
        {
            rhsIndex[rhs[i]] = i;
        }

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

        return lhsRange;
    }

    // ------------------------------------------------------------------ silence snap

    private static double AdjustEndToSilence(
        string path, double introStart, double introEnd, ILogger logger, CancellationToken ct)
    {
        try
        {
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-hide_banner -loglevel info -vn -sn -dn -i \"{0}\" -to {1} -af \"silencedetect=noise=-50dB:duration=0.1\" -f null -",
                path,
                (int)introEnd + 2);

            var raw = Encoding.UTF8.GetString(RunProcessAsync("ffmpeg", args, true, logger, ct).ConfigureAwait(false).GetAwaiter().GetResult());
            var originalEnd = new TimeRange(introEnd - 15, introEnd);

            double start = double.MinValue, end = double.MinValue;
            var hasStart = false;
            foreach (Match match in SilenceRegex.Matches(raw))
            {
                var isStart = match.Groups["type"].Value == "start";
                var time = Convert.ToDouble(match.Groups["time"].Value, CultureInfo.InvariantCulture);
                if (isStart)
                {
                    start = time;
                    hasStart = true;
                }
                else if (hasStart)
                {
                    end = time;
                    var candidate = new TimeRange(start, end);
                    if (originalEnd.Intersects(candidate) &&
                        candidate.Duration >= 0.33 &&
                        candidate.Start >= introStart)
                    {
                        return candidate.Start;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Episode Tools] silence detection failed for \"{Path}\".", path);
        }

        return introEnd;
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
                (Start < tr.End && tr.End < End);
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
