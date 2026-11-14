using System;
using System.IO;
using System.Text;
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
}
