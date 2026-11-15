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
                return Ok("No report yet. Run one of the convert tasks (preview or real) first.");
            }

            return Ok(System.IO.File.ReadAllText(path, Encoding.UTF8));
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
                return Ok("No audit yet. Run task '7. Audit library' first.");
            }

            return Ok(System.IO.File.ReadAllText(path, Encoding.UTF8));
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
                return Ok("No subtitle check yet. Run task '5. Check subtitles' first.");
            }

            return Ok(System.IO.File.ReadAllText(path, Encoding.UTF8));
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

        foreach (var folder in folders)
        {
            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                continue;
            }

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

                shows.Add(new ShowRow(name, episodes, mkv, langs));
            }
        }

        shows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var json = JsonSerializer.Serialize(
            new { foldersScanned = folders.Count(f => f.Length > 0 && Directory.Exists(f)), shows },
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        return Ok(json);
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
            aud = p.AudioLanguage
        });

        return Ok(JsonSerializer.Serialize(new { prefs }));
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
        if (sub is not ("" or "none") && sub.Length > 3)
        {
            return BadRequest("Subtitle language must be empty, 'none', or a 2-3 letter code.");
        }

        if (aud.Length > 3)
        {
            return BadRequest("Audio language must be empty or a 2-3 letter code.");
        }

        var list = cfg.Configuration.ShowPreferences;
        var existing = list.FirstOrDefault(p => string.Equals(p.Show, show, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            list.Add(new ShowPreference(show, sub, aud));
        }
        else
        {
            existing.SubtitleLanguage = sub;
            existing.AudioLanguage = aud;
        }

        cfg.SaveConfiguration();
        _logger.LogInformation("[Episode Tools] Saved track preference for '{Show}': subtitles='{Sub}', audio='{Aud}'.", show, sub, aud);
        return Ok("saved");
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

    private sealed record ShowRow(string Name, int Episodes, int Mkv, List<string> SubLangs);

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
    }
}
