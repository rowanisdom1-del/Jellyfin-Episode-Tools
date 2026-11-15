using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public EpisodeToolsReportController(ILogger<EpisodeToolsReportController> logger)
    {
        _logger = logger;
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
                var name = Path.GetFileName(showDir);
                if (!seen.Add(name))
                {
                    continue;
                }

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
            aud2 = p.AudioFallback
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
        var existing = list.FirstOrDefault(p => string.Equals(p.Show, show, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            list.Add(new ShowPreference(show, sub, aud, audFallback));
        }
        else
        {
            existing.SubtitleLanguage = sub;
            existing.AudioLanguage = aud;
            existing.AudioFallback = audFallback;
        }

        cfg.SaveConfiguration();
        _logger.LogInformation("[Episode Tools] Saved track preference for '{Show}': subtitles='{Sub}', audio='{Aud}'.", show, sub, aud);
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
    /// Returns the intro scan status plus the latest results.
    /// </summary>
    [HttpGet("IntrosStatus")]
    public IActionResult IntrosStatus()
    {
        var results = IntrosRunner.LoadResults();
        var resultsFile = System.IO.Path.Combine(Plugin.Instance?.WorkFolder ?? ".", "intros.json");
        var scannedAt = System.IO.File.Exists(resultsFile)
            ? System.IO.File.GetLastWriteTimeUtc(resultsFile)
            : (DateTime?)null;

        var payload = new
        {
            scan = IntrosRunner.Status(),
            scannedAt,
            intros = results.Select(r => new
            {
                show = r.Show,
                season = r.Season,
                file = r.File,
                start = r.Start,
                end = r.End
            })
        };

        return ContentJson(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        }));
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
        foreach (var (path, _) in files)
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

                if (action == "delete-others")
                {
                    System.IO.File.Delete(path);
                }
                else
                {
                    System.IO.File.Move(path, Path.Combine(trashDir, Path.GetFileName(path)), overwrite: true);
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
    }

    /// <summary>Request body for ConvertShow.</summary>
    public sealed class ConvertShowBody
    {
        /// <summary>Gets or sets the absolute show folder to convert.</summary>
        [JsonPropertyName("folder")]
        public string? Folder { get; set; }
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
