using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EpisodeTools.Intros;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools.Tasks;

/// <summary>
/// Fast intro scanner: fingerprints only the first few minutes of every episode in the
/// configured folders, compares each season's episodes and stores the shared opening as
/// media segments (via the plugin's segment provider). Much faster than fingerprinting
/// whole episodes.
/// </summary>
public sealed class ScanIntrosTask : IScheduledTask
{
    private readonly ILogger<ScanIntrosTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="ScanIntrosTask"/> class.</summary>
    public ScanIntrosTask(ILogger<ScanIntrosTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "8. Scan intros (fast)";

    /// <inheritdoc />
    public string Key => "EpisodeToolsScanIntros";

    /// <inheritdoc />
    public string Description => "Fingerprints the first few minutes of every episode and finds the shared opening of each season. Results become skippable intro segments. Read the result under Dashboard > Plugins > Episode Tools > Intros.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!IntrosRunner.TryStart(_logger))
        {
            _logger.LogInformation("[Episode Tools] Intro scan already running; not started again.");
            return Task.CompletedTask;
        }

        // The runner reports its own progress through the status endpoint; wait until it stops.
        return Task.Run(async () =>
        {
            while (IntrosRunner.Running)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cancellationToken.IsCancellationRequested)
                {
                    IntrosRunner.Stop();
                    return;
                }

                progress.Report(IntrosRunner.LastProgress);
                await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
            }

            progress.Report(100);
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
