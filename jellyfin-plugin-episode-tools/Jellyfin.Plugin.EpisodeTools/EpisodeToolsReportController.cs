using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.EpisodeTools.Intros;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Serves the convert report: every .mkv that could not be converted and why.
/// The report page in the dashboard fetches this endpoint.
/// </summary>
[ApiController]
[Route("EpisodeTools")]
[Route("emby/EpisodeTools")]
[Authorize]
public class EpisodeToolsReportController : ControllerBase
{
    private readonly ILogger<EpisodeToolsReportController> _logger;

    /// <summary>Initializes a new instance of the <see cref="EpisodeToolsReportController"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="sessionManager">Jellyfin sessions, for the playback guard (per-show convert).</param>
    /// <param name="taskManager">Jellyfin's task manager, for the post-convert library scan.</param>
    public EpisodeToolsReportController(
        ILogger<EpisodeToolsReportController> logger,
        ISessionManager sessionManager,
        ITaskManager taskManager)
    {
        _logger = logger;
        PlaybackGuard.Configure(sessionManager);
        ScriptRunner.ConfigureTaskManager(taskManager);
    }

    private static string WorkDir =>
        Plugin.Instance?.WorkFolder ?? Path.Combine(Path.GetTempPath(), "episode-tools");

    private static string TrashRoot => Path.Combine(WorkDir, "trash");

    /// <summary>Group folder names may never carry separators or traversal.</summary>
    private static string SanitizeGroup(string group)
    {
        var sb = new StringBuilder();
        foreach (var c in group)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }

        return sb.ToString();
    }

    private static string ManifestPathFor(string trashDir) => Path.Combine(trashDir, "manifest.json");

    /// <summary>One archived file's metadata, kept in a small per-group manifest.json.</summary>
    public sealed class TrashEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("originalPath")]
        public string? OriginalPath { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("trashedAt")]
        public DateTime TrashedAt { get; set; }
    }

    /// <summary>
    /// Moves one file into the trash archive under a group folder, picking a free name so
    /// same-basename files never overwrite each other, and appends a manifest entry with
    /// the original path (which powers one-click restore).
    /// </summary>
    private static void SaveToTrash(string sourcePath, string originalPath, string group)
    {
        var trashDir = Path.Combine(TrashRoot, SanitizeGroup(group));
        Directory.CreateDirectory(trashDir);

        var fileName = Path.GetFileName(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var dest = Path.Combine(trashDir, fileName);
        for (var n = 1; File.Exists(dest); n++)
        {
            dest = Path.Combine(trashDir, stem + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext);
        }

        File.Move(sourcePath, dest);

        var entry = new TrashEntry
        {
            Name = Path.GetFileName(dest),
            OriginalPath = originalPath,
            Size = new FileInfo(dest).Length,
            TrashedAt = DateTime.UtcNow
        };

        var entries = new List<TrashEntry>();
        try
        {
            if (File.Exists(ManifestPathFor(trashDir)))
            {
                entries = JsonSerializer.Deserialize<List<TrashEntry>>(File.ReadAllText(ManifestPathFor(trashDir)))
                    ?? new List<TrashEntry>();
            }
        }
        catch (Exception)
        {
            // A corrupt manifest starts fresh; the file itself stays restorable by hand.
        }

        entries.Add(entry);
        try
        {
            var tmp = ManifestPathFor(trashDir) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, ManifestPathFor(trashDir), true);
        }
        catch (Exception)
        {
            // Manifest writes are best effort.
        }
    }

    /// <summary>Returns raw JSON without re-serializing it into a JSON string.</summary>
    private ContentResult ContentJson(string json)
    {
        return new ContentResult
        {
            Content = json,
            ContentType = "application/json",
            StatusCode = 200
        };
    }

    /// <summary>Returns raw plain text.</summary>
    private ContentResult ContentText(string text)
    {
        return new ContentResult
        {
            Content = text,
            ContentType = "text/plain; charset=utf-8",
            StatusCode = 200
        };
    }

    /// <summary>
    /// Returns the report of the latest convert run as plain text.
    /// Lines are tab separated: RUN mode timestamp, or CANNOT path reason.
    /// </summary>
    [HttpGet("Report")]
    public ActionResult<string> GetReport()
    {
        var baseDir = Plugin.Instance?.WorkFolder ?? Path.Combine(Path.GetTempPath(), "episode-tools");
        var path = Path.Combine(baseDir, "convert-report.txt");

        try
        {
            if (!System.IO.File.Exists(path))
            {
                return ContentText("No report yet. Run one of the convert tasks (preview or real) first.");
            }

            return ContentText(System.IO.File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Episode Tools] Could not read the convert report.");
            return StatusCode(500, "Could not read the convert report: " + ex.Message);
        }
    }

    /// <summary>
    /// Returns the latest library audit report as plain text.
    /// Lines are tab separated: RUN kind timestamp, DUP key path size, BIG size path, TOTAL size folder.
    /// </summary>
    [HttpGet("Audit")]
    public ActionResult<string> GetAudit()
    {
        var baseDir = Plugin.Instance?.WorkFolder ?? Path.Combine(Path.GetTempPath(), "episode-tools");
        var path = Path.Combine(baseDir, "audit-report.txt");

        try
        {
            if (!System.IO.File.Exists(path))
            {
                return ContentText("No audit yet. Run task '7. Audit library' first.");
            }

            return ContentText(System.IO.File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Episode Tools] Could not read the audit report.");
            return StatusCode(500, "Could not read the audit report: " + ex.Message);
        }
    }

    /// <summary>
    /// Returns the latest subtitle check report as plain text.
    /// Lines are tab separated: RUN kind timestamp, SUBWARN path message, SUBOK path message,
    /// SUBINFO path message.
    /// </summary>
    [HttpGet("SubCheck")]
    public ActionResult<string> GetSubCheck()
    {
        var baseDir = Plugin.Instance?.WorkFolder ?? Path.Combine(Path.GetTempPath(), "episode-tools");
        var path = Path.Combine(baseDir, "subcheck-report.txt");

        try
        {
            if (!System.IO.File.Exists(path))
            {
                return ContentText("No subtitle check yet. Run task '5. Check subtitles' first.");
            }

            return ContentText(System.IO.File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Episode Tools] Could not read the subtitle check report.");
            return StatusCode(500, "Could not read the subtitle check report: " + ex.Message);
        }
    }

    /// <summary>
    /// Returns every show found under the configured series/scan folders, with file counts and
    /// the subtitle language tags found in file names.
    /// </summary>
    [HttpGet("Shows")]
    public ActionResult<string> GetShows()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return StatusCode(500, "Plugin not initialized.");
        }

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
        var subExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt"
        };
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false
        };

        var shows = new List<ShowRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        foreach (var folder in folders)
        {
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                continue;
            }

            scanned++;
            foreach (var showDir in Directory.EnumerateDirectories(folder, "*", options))
            {
                // Deduplicate by full path, never by leaf name: identically named folders in
                // different libraries (e.g. two shows called "ted") are different shows and
                // must both appear. Matches EnumerateJobs in IntrosRunner.
                var dirKey = showDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!seen.Add(dirKey))
                {
                    continue;
                }

                var name = Path.GetFileName(showDir);

                int episodes = 0, mkv = 0;
                var langs = new List<string>();

                foreach (var file in SafeEnumerateFiles(showDir))
                {
                    var ext = Path.GetExtension(file);
                    if (videoExt.Contains(ext))
                    {
                        episodes++;
                        if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                        {
                            mkv++;
                        }
                    }
                    else if (subExt.Contains(ext))
                    {
                        // Language tag: the token before the extension, e.g. "... .nl.srt"
                        var stem = Path.GetFileNameWithoutExtension(file);
                        var m = System.Text.RegularExpressions.Regex.Match(stem, @"\.([A-Za-z]{2,3})$");
                        var lang = m.Success ? m.Groups[1].Value.ToLowerInvariant() : string.Empty;
                        if (lang.Length > 0 && !langs.Contains(lang))
                        {
                            langs.Add(lang);
                        }
                    }
                }

                shows.Add(new ShowRow(name, episodes, mkv, langs, showDir));
            }
        }

        shows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var json = JsonSerializer.Serialize(
            new { foldersScanned = scanned, shows },
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        return ContentJson(json);
    }

    /// <summary>
    /// Returns the saved per-show subtitle/audio preferences.
    /// </summary>
    [HttpGet("ShowPrefs")]
    public ActionResult<string> GetShowPrefs()
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return StatusCode(500, "Plugin not initialized.");
        }

        var prefs = cfg.ShowPreferences.Select(p => new
        {
            show = p.Show,
            sub = p.SubtitleLanguage,
            aud = p.AudioLanguage,
            aud2 = p.AudioFallback,
            intro = p.IntroScanEnabled
        });

        return ContentJson(JsonSerializer.Serialize(new { prefs }));
    }

    /// <summary>
    /// Saves one show's subtitle/audio preference. Body: {show, sub, aud}.
    /// sub: "" keep all, "none" drop subtitles, or a language code. aud: "" or a language code.
    /// </summary>
    [HttpPost("ShowPrefs")]
    public IActionResult SetShowPrefs([FromBody] ShowPrefBody body)
    {
        var cfg = Plugin.Instance;
        if (cfg?.Configuration is null)
        {
            return StatusCode(500, "Plugin not initialized.");
        }

        var show = (body.Show ?? string.Empty).Trim();
        if (show.Length == 0)
        {
            return BadRequest("Missing show name.");
        }

        var sub = (body.Sub ?? string.Empty).Trim().ToLowerInvariant();
        var aud = (body.Aud ?? string.Empty).Trim().ToLowerInvariant();
        var audFallback = (body.Aud2 ?? string.Empty).Trim().ToLowerInvariant();
        if (sub is not ("" or "none") && sub.Length > 3)
        {
            return BadRequest("Subtitle language must be empty, 'none', or a 2-3 letter code.");
        }

        if (aud.Length > 3 || audFallback.Length > 3)
        {
            return BadRequest("Audio languages must be empty or a 2-3 letter code.");
        }

        var list = cfg.Configuration.ShowPreferences;
        var introScan = body.Intro ?? true;
        var existing = list.FirstOrDefault(p => string.Equals(p.Show, show, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            list.Add(new ShowPreference(show, sub, aud, audFallback) { IntroScanEnabled = introScan });
        }
        else
        {
            existing.SubtitleLanguage = sub;
            existing.AudioLanguage = aud;
            existing.AudioFallback = audFallback;
            existing.IntroScanEnabled = introScan;
        }

        cfg.SaveConfiguration();
        _logger.LogInformation(
            "[Episode Tools] Saved preference for '{Show}': subtitles='{Sub}', audio='{Aud}', introScan={Intro}.",
            show, sub, aud, introScan ? "on" : "off");
        return ContentText("saved");
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3
            };
            return Directory.EnumerateFiles(dir, "*", options);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Starts a conversion of a single show folder in the background. Body: {folder}.
    /// Returns 409 when a run is already active.
    /// </summary>
    [HttpPost("ConvertShow")]
    public IActionResult ConvertShow([FromBody] ConvertShowBody body)
    {
        var folder = (body.Folder ?? string.Empty).Trim();
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return StatusCode(500, "Plugin not initialized.");
        }

        // The folder must exist and sit under one of the configured series/scan folders.
        var allowed = new List<string>(cfg.EffectiveRenameFolders);
        allowed.AddRange(cfg.EffectiveConvertFolders);
        var ok = false;
        foreach (var root in allowed)
        {
            if (root.Length > 0 && folder.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
            {
                ok = true;
                break;
            }
        }

        if (!ok)
        {
            return BadRequest("Folder is not under a configured series/scan folder.");
        }

        if (!ShowConvertRunner.TryStart(folder, _logger))
        {
            return StatusCode(409, "A conversion is already running.");
        }

        _logger.LogInformation("[Episode Tools] Per-show conversion started for {Folder}.", folder);
        return ContentText("started");
    }

    /// <summary>
    /// Returns the state of the background per-show conversion.
    /// </summary>
    [HttpGet("ConvertStatus")]
    public IActionResult ConvertStatus()
    {
        return ContentJson(System.Text.Json.JsonSerializer.Serialize(ShowConvertRunner.Status()));
    }

    /// <summary>
    /// Starts the fast intro scan. Safe to call twice; the second call is a no-op while a
    /// scan is running.
    /// </summary>
    [HttpPost("IntrosStart")]
    public IActionResult StartIntros()
    {
        if (!IntrosRunner.TryStart(_logger))
        {
            return ContentText("already running");
        }

        return ContentText("started");
    }

    /// <summary>
    /// Stops a running intro scan.
    /// </summary>
    [HttpPost("IntrosStop")]
    public IActionResult StopIntros()
    {
        IntrosRunner.Stop();
        return ContentText("stopped");
    }

    /// <summary>
    /// One-click default subtitle track repair (Sub Check tab). Body: {path, wrong, pick}
    /// where wrong/pick are zero-based indices among the file's SUBTITLE streams (as
    /// reported by the SubFix lines). .mkv: instant in-place flag edit via mkvpropedit when
    /// available. .mp4/.m4v/.mov: lossless stream-copy remux with corrected dispositions,
    /// replaced only after verification; the original is archived to the plugin trash.
    /// </summary>
    [HttpPost("FixDefaultTrack")]
    public async Task<IActionResult> FixDefaultTrack([FromBody] FixDefaultBody body)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return StatusCode(500, "Plugin not initialized.");
        }

        var path = (body.Path ?? string.Empty).Trim();
        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            return NotFound("File not found.");
        }

        var allowed = new List<string>(cfg.EffectiveRenameFolders);
        allowed.AddRange(cfg.EffectiveConvertFolders);
        if (!allowed.Any(root => root.Length > 0 &&
            path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest("File is not under a configured series/scan folder.");
        }

        if (body.Wrong is null || body.Pick is null || body.Wrong < 0 || body.Pick < 0 || body.Wrong == body.Pick)
        {
            return BadRequest("Invalid track indices.");
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mkv" or ".mp4" or ".m4v" or ".mov"))
        {
            return BadRequest("Only .mkv, .mp4, .m4v and .mov files can be fixed.");
        }

        try
        {
            var result = await FixDefaultTrackAsync(path, ext, body.Wrong.Value, body.Pick.Value);
            _logger.LogInformation("[Episode Tools] Default subtitle flag fixed for {Path}: {Result}", path, result);
            return ContentText(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Episode Tools] Default track fix failed for {Path}.", path);
            return StatusCode(500, "Fix failed: " + ex.Message);
        }
    }

    private async Task<string> FixDefaultTrackAsync(string path, string ext, int wrongRel, int pickRel)
    {
        if (ext == ".mkv")
        {
            var mkvpropedit = await FindToolAsync("mkvpropedit").ConfigureAwait(false);
            if (mkvpropedit is not null)
            {
                // mkvpropedit uses 1-based track:s selectors; the edit is an in-place
                // header change, so no remux/replace cycle is needed at all.
                var args = new List<string>
                {
                    "--edit", $"track:s{wrongRel + 1}", "--set", "flag-default=0",
                    "--edit", $"track:s{pickRel + 1}", "--set", "flag-default=1",
                    path
                };
                var (code, output) = await RunToolAsync(mkvpropedit, args).ConfigureAwait(false);
                if (code != 0)
                {
                    throw new InvalidOperationException("mkvpropedit failed (exit " + code + "): " + output);
                }

                return "fixed in place with mkvpropedit";
            }
        }

        // Fallback for .mkv without mkvpropedit, and the main path for .mp4/.m4v/.mov:
        // a lossless stream-copy remux with the dispositions swapped. The tmp file is only
        // swapped in after verification, so the source can never end up broken.
        var dir = Path.GetDirectoryName(path) ?? ".";
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".subfix.part");
        try
        {
            var format = ext == ".mkv" ? "matroska" : "mp4";
            var args = new List<string>
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-i", path,
                "-map", "0", "-c", "copy",
                "-disposition:s:" + wrongRel.ToString(System.Globalization.CultureInfo.InvariantCulture), "0",
                "-disposition:s:" + pickRel.ToString(System.Globalization.CultureInfo.InvariantCulture), "default",
                "-f", format,
                tmp
            };
            if (format == "mp4")
            {
                // The bin_data trap: remuxing MP4s must never carry chapter/data tracks
                // (ExoPlayer refuses to play files that contain them).
                args.Insert(args.IndexOf("-f"), "-dn");
                args.Insert(args.IndexOf("-f"), "-map_chapters");
                args.Insert(args.IndexOf("-f"), "0");
            }

            var ffmpeg = FfmpegPath();
            var (code, output) = await RunToolAsync(ffmpeg, args).ConfigureAwait(false);
            if (code != 0 || !System.IO.File.Exists(tmp) || new FileInfo(tmp).Length < 1024)
            {
                throw new InvalidOperationException("ffmpeg remux failed (exit " + code + "): " + output);
            }

            if (!await VerifySingleDefaultTrackAsync(tmp, ext, pickRel).ConfigureAwait(false))
            {
                throw new InvalidOperationException("the fixed file did not pass verification (default flag not on the requested track)");
            }

            // Verified: swap atomically, keep the original restorable in the trash.
            var backup = path + ".subfix-old";
            System.IO.File.Move(path, backup);
            try
            {
                System.IO.File.Move(tmp, path);
            }
            catch (Exception)
            {
                System.IO.File.Move(backup, path);   // restore on failure
                throw;
            }

            SaveToTrash(backup, path, "default-track-fix");
            return "fixed with a verified lossless remux (original archived to the plugin trash)";
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tmp))
                {
                    System.IO.File.Delete(tmp);
                }
            }
            catch (Exception)
            {
                // Cleanup is best effort.
            }
        }
    }

    private static string FfmpegPath()
    {
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

            if (!candidate.Contains(Path.DirectorySeparatorChar) || System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffmpeg";
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

            if (!candidate.Contains(Path.DirectorySeparatorChar) || System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "ffprobe";
    }

    /// <summary>Probes a tool on PATH or at its usual absolute locations.</summary>
    private static async Task<string?> FindToolAsync(string tool)
    {
        foreach (var candidate in new[] { "/usr/bin/" + tool, "/usr/local/bin/" + tool, tool })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("--version");
                using var process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                process.WaitForExit(3000);
                if (process.HasExited && process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static async Task<(int Code, string Output)> RunToolAsync(string executable, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Could not start " + executable + ".");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, (stdout + " " + stderr).Trim());
    }

    /// <summary>
    /// Verifies that exactly one subtitle stream of the file is flagged default and that
    /// it is the requested one (relative index among the subtitle streams).
    /// </summary>
    private static async Task<bool> VerifySingleDefaultTrackAsync(string file, string ext, int pickRel)
    {
        try
        {
            var args = new List<string>
            {
                "-v", "error",
                "-select_streams", "s",
                "-show_entries", "stream=index:stream_disposition=default",
                "-of", "json",
                file
            };
            var (code, output) = await RunToolAsync(FfprobePath(), args).ConfigureAwait(false);
            if (code != 0)
            {
                return false;
            }

            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("streams", out var streams))
            {
                return false;
            }

            var defaults = 0;
            var defaultRel = -1;
            var rel = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("disposition", out var d) &&
                    d.TryGetProperty("default", out var dv) &&
                    dv.ValueKind == JsonValueKind.Number && dv.GetInt32() == 1)
                {
                    defaults++;
                    defaultRel = rel;
                }

                rel++;
            }

            _ = ext;
            return defaults == 1 && defaultRel == pickRel;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Lists the trash archive: one entry per group folder with manifest metadata (original
    /// path, size, trashed date) plus unmanifested stray files.
    /// </summary>
    [HttpGet("Trash")]
    public IActionResult GetTrash()
    {
        var root = TrashRoot;
        var groups = new List<object>();
        long totalBytes = 0;
        var totalFiles = 0;

        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var entries = new List<object>();
                var manifest = new List<TrashEntry>();
                try
                {
                    if (File.Exists(ManifestPathFor(dir)))
                    {
                        manifest = JsonSerializer.Deserialize<List<TrashEntry>>(File.ReadAllText(ManifestPathFor(dir)))
                            ?? new List<TrashEntry>();
                    }
                }
                catch (Exception)
                {
                    // Listed through the file fallback below.
                }

                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in manifest)
                {
                    var f = Path.Combine(dir, entry.Name);
                    if (!System.IO.File.Exists(f))
                    {
                        continue;
                    }

                    claimed.Add(entry.Name);
                    totalFiles++;
                    totalBytes += entry.Size;
                    entries.Add(new { name = entry.Name, size = entry.Size, originalPath = entry.OriginalPath, trashedAt = entry.TrashedAt });
                }

                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(f);
                    if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase) || claimed.Contains(name))
                    {
                        continue;
                    }

                    var fi = new FileInfo(f);
                    totalFiles++;
                    totalBytes += fi.Length;
                    entries.Add(new { name, size = fi.Length, originalPath = (string?)null, trashedAt = (DateTime?)fi.LastWriteTimeUtc });
                }

                if (entries.Count > 0)
                {
                    groups.Add(new { group = Path.GetFileName(dir), files = entries });
                }
            }
        }

        return ContentJson(JsonSerializer.Serialize(
            new { groups, totalFiles, totalBytes },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    /// <summary>
    /// Restores one archived file to its original location (from the manifest, or an
    /// explicitly supplied path). Never overwrites an existing target.
    /// </summary>
    [HttpPost("Trash/Restore")]
    public IActionResult RestoreTrash([FromBody] TrashRestoreBody body)
    {
        var group = (body.Group ?? string.Empty).Trim();
        var name = (body.Name ?? string.Empty).Trim();
        if (group.Length == 0 || name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            return BadRequest("Invalid trash entry.");
        }

        var trashDir = Path.Combine(TrashRoot, SanitizeGroup(group));
        var source = Path.Combine(trashDir, name);
        if (!System.IO.File.Exists(source))
        {
            return NotFound("That file is not in the trash archive.");
        }

        var dest = (body.Path ?? string.Empty).Trim();
        if (dest.Length == 0)
        {
            // Fall back to the manifest's original path.
            try
            {
                var manifest = JsonSerializer.Deserialize<List<TrashEntry>>(File.ReadAllText(ManifestPathFor(trashDir)));
                dest = manifest?.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.OriginalPath ?? string.Empty;
            }
            catch (Exception)
            {
                dest = string.Empty;
            }
        }

        if (dest.Length == 0)
        {
            return BadRequest("No restore destination known for this file.");
        }

        if (System.IO.File.Exists(dest) || Directory.Exists(dest))
        {
            return StatusCode(409, "Restore would overwrite: " + dest + " already exists.");
        }

        var destDir = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
        {
            return BadRequest("Restore destination folder does not exist: " + destDir);
        }

        try
        {
            System.IO.File.Move(source, dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Episode Tools] Could not restore {Name} to {Dest}.", name, dest);
            return StatusCode(500, "Restore failed: " + ex.Message);
        }

        // Drop the manifest entry so the listing stays truthful.
        try
        {
            var manifest = JsonSerializer.Deserialize<List<TrashEntry>>(File.ReadAllText(ManifestPathFor(trashDir)))
                ?? new List<TrashEntry>();
            var remaining = manifest.Where(e => !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var tmp = ManifestPathFor(trashDir) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(remaining, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, ManifestPathFor(trashDir), true);
        }
        catch (Exception)
        {
            // Best effort.
        }

        _logger.LogInformation("[Episode Tools] Restored {Name} from the trash to {Dest}.", name, dest);
        return ContentText("restored to " + dest);
    }

    /// <summary>Empties the whole trash archive (the files in it were already removed).</summary>
    [HttpPost("Trash/Empty")]
    public IActionResult EmptyTrash()
    {
        var root = TrashRoot;
        if (!Directory.Exists(root))
        {
            return ContentText("trash is already empty");
        }

        var dirs = Directory.EnumerateDirectories(root).ToList();
        var removed = 0;
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
                removed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Episode Tools] Could not remove trash folder {Dir}.", dir);
            }
        }

        _logger.LogInformation("[Episode Tools] Trash emptied: {Count} group folder(s) removed.", removed);
        return ContentText("trash emptied (" + removed + " group folder(s))");
    }

    /// <summary>
    /// Returns the intro scan status plus the latest results. While a scan is running the
    /// dashboard polls every two seconds, so only the progress fields plus the result
    /// count are sent then; serializing the full intros list each poll is pure churn on
    /// low-power servers. The full list comes back once no scan is running.
    /// </summary>
    [HttpGet("IntrosStatus")]
    public IActionResult IntrosStatus()
    {
        var scan = IntrosRunner.Status();
        var resultsFile = System.IO.Path.Combine(Plugin.Instance?.WorkFolder ?? ".", "intros.json");
        var scannedAt = System.IO.File.Exists(resultsFile)
            ? System.IO.File.GetLastWriteTimeUtc(resultsFile)
            : (DateTime?)null;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        if (IntrosRunner.Running)
        {
            return ContentJson(System.Text.Json.JsonSerializer.Serialize(new
            {
                scan,
                scannedAt,
                count = IntrosRunner.LoadResults().Count
            }, options));
        }

        var results = IntrosRunner.LoadResults();
        var payload = new
        {
            scan,
            scannedAt,
            count = results.Count,
            intros = results.Select(r => new
            {
                show = r.Show,
                season = r.Season,
                file = r.File,
                start = r.Start,
                end = r.End,
                outroStart = r.OutroStart,
                outroEnd = r.OutroEnd
            })
        };

        return ContentJson(System.Text.Json.JsonSerializer.Serialize(payload, options));
    }

    /// <summary>
    /// Resolves one duplicate group from the latest audit. Body: {key, action} where action is
    /// "keep-largest", "keep-newest" (others archived to the plugin trash folder) or
    /// "delete-others" (permanent). Only files listed in the audit are touched.
    /// </summary>
    [HttpPost("AuditAction")]
    public IActionResult AuditAction([FromBody] AuditActionBody body)
    {
        var key = (body.Key ?? string.Empty).Trim();
        var action = (body.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0 || action is not ("keep-largest" or "keep-newest" or "delete-others"))
        {
            return BadRequest("Unknown key or action.");
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return StatusCode(500, "Plugin not initialized.");
        }

        var allowed = new List<string>(cfg.EffectiveRenameFolders);
        allowed.AddRange(cfg.EffectiveConvertFolders);

        // Read the group out of the audit report.
        var baseDir = Plugin.Instance.WorkFolder;
        var reportPath = Path.Combine(baseDir, "audit-report.txt");
        if (!System.IO.File.Exists(reportPath))
        {
            return NotFound("No audit report yet.");
        }

        var files = new List<(string Path, long Size)>();
        foreach (var line in System.IO.File.ReadLines(reportPath))
        {
            var cols = line.Split('\t');
            if (cols.Length >= 4 && cols[0] == "DUP" && string.Equals(cols[1], key, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(cols[3], out var size))
            {
                files.Add((cols[2], size));
            }
        }

        if (files.Count < 2)
        {
            return NotFound("That duplicate group is not in the latest audit report.");
        }

        // Only touch files that live under the configured folders.
        files = files.Where(f => allowed.Any(root => root.Length > 0 &&
            f.Path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        if (files.Count < 2)
        {
            return BadRequest("None of the group's files are under the configured folders.");
        }

        string keep;
        if (action == "keep-newest")
        {
            keep = files.OrderByDescending(f => SafeLastWrite(f.Path)).First().Path;
        }
        else
        {
            keep = files.OrderByDescending(f => f.Size).First().Path;
        }

        var trashDir = Path.Combine(baseDir, "trash", key.Replace("|", "_"));
        Directory.CreateDirectory(trashDir);
        var removed = new List<string>();
        var errors = new List<string>();
        foreach (var (path, size) in files)
        {
            if (string.Equals(path, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!System.IO.File.Exists(path))
                {
                    continue;
                }

                // Guard against a stale audit: if the file was renamed/replaced after the
                // report was written, the path may now point at a different file. Only touch
                // paths whose current size still matches the recorded one.
                var currentSize = SafeLength(path);
                if (currentSize is null || currentSize.Value != size)
                {
                    _logger.LogWarning(
                        "[Episode Tools] Skipping {Path}: file changed since the audit (recorded size {Recorded}, current {Current}).",
                        path,
                        size,
                        currentSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable");
                    errors.Add($"{path}: file changed since the audit (skipped)");
                    continue;
                }

                if (action == "delete-others")
                {
                    System.IO.File.Delete(path);
                }
                else
                {
                    // Archive to the trash with a manifest entry (never overwriting an
                    // already-archived file), so it can be restored later.
                    SaveToTrash(path, path, key);
                }

                removed.Add(path);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
                _logger.LogWarning(ex, "[Episode Tools] Could not remove duplicate {Path}.", path);
            }
        }

        _logger.LogInformation(
            "[Episode Tools] Duplicate group {Key}: kept {Keep}, removed {Count} file(s).",
            key, keep, removed.Count);
        return ContentJson(System.Text.Json.JsonSerializer.Serialize(new
        {
            kept = keep,
            removed,
            errors,
            trash = action == "delete-others" ? null : trashDir
        }));
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>File length in bytes, or null when the file cannot be stat'ed.</summary>
    private static long? SafeLength(string path)
    {
        try
        {
            return new System.IO.FileInfo(path).Length;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record ShowRow(string Name, int Episodes, int Mkv, List<string> SubLangs, string Folder);

    /// <summary>Request body for SetShowPrefs.</summary>
    public sealed class ShowPrefBody
    {
        /// <summary>Gets or sets the show name.</summary>
        [JsonPropertyName("show")]
        public string? Show { get; set; }

        /// <summary>Gets or sets the subtitle language: "", "none" or a language code.</summary>
        [JsonPropertyName("sub")]
        public string? Sub { get; set; }

        /// <summary>Gets or sets the audio language: "" or a language code.</summary>
        [JsonPropertyName("aud")]
        public string? Aud { get; set; }

        /// <summary>Gets or sets the fallback audio language: "" or a language code.</summary>
        [JsonPropertyName("aud2")]
        public string? Aud2 { get; set; }

        /// <summary>Gets or sets whether the intro scanner includes this show (default true).</summary>
        [JsonPropertyName("intro")]
        public bool? Intro { get; set; }
    }

    /// <summary>Request body for ConvertShow.</summary>
    public sealed class ConvertShowBody
    {
        /// <summary>Gets or sets the absolute show folder to convert.</summary>
        [JsonPropertyName("folder")]
        public string? Folder { get; set; }
    }

    /// <summary>Request body for FixDefaultTrack.</summary>
    public sealed class FixDefaultBody
    {
        /// <summary>Gets or sets the absolute video file path.</summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>Gets or sets the wrong default subtitle track (relative index).</summary>
        [JsonPropertyName("wrong")]
        public int? Wrong { get; set; }

        /// <summary>Gets or sets the recommended dialogue track (relative index).</summary>
        [JsonPropertyName("pick")]
        public int? Pick { get; set; }
    }

    /// <summary>Request body for Trash/Restore.</summary>
    public sealed class TrashRestoreBody
    {
        /// <summary>Gets or sets the trash group folder name.</summary>
        [JsonPropertyName("group")]
        public string? Group { get; set; }

        /// <summary>Gets or sets the file name inside the group.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>Optional explicit destination (defaults to the manifest's original path).</summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    /// <summary>Request body for AuditAction.</summary>
    public sealed class AuditActionBody
    {
        /// <summary>Gets or sets the duplicate group key from the audit report.</summary>
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        /// <summary>Gets or sets the action: keep-largest, keep-newest or delete-others.</summary>
        [JsonPropertyName("action")]
        public string? Action { get; set; }
    }
}
