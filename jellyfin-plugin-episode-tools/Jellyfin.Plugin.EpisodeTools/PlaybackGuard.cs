using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Pauses scripted work while Jellyfin users are actually watching something.
/// The intro scan awaits <see cref="WaitWhilePlaybackActiveAsync"/> between season groups;
/// the convert script polls a flag file that a background watchdog refreshes every few
/// seconds, so the heavy bash/ffmpeg side pauses between files without any cross-language
/// calls. Nothing is killed or cancelled by playback - work simply waits at file boundaries
/// and resumes within seconds of the last session stopping.
/// </summary>
internal static class PlaybackGuard
{
    private const int PollSeconds = 15;

    private static ISessionManager? _sessionManager;

    /// <summary>Wired up by the tasks/controllers that receive ISessionManager from DI.</summary>
    public static void Configure(ISessionManager? sessionManager)
    {
        if (sessionManager is not null)
        {
            _sessionManager = sessionManager;
        }
    }

    private static bool Enabled =>
        (Plugin.Instance?.Configuration?.PauseWhenPlayback).GetValueOrDefault(true);

    /// <summary>True while any Jellyfin session is playing media.</summary>
    public static bool IsPlaybackActive()
    {
        if (!Enabled)
        {
            return false;
        }

        var sessions = _sessionManager?.Sessions;
        if (sessions is null)
        {
            return false;
        }

        try
        {
            return sessions.Any(s => s?.NowPlayingItem is not null);
        }
        catch (Exception)
        {
            // A session manager hiccup must never block background work.
            return false;
        }
    }

    /// <summary>
    /// Waits until nobody is playing anymore (or the token fires). Polls in short steps so
    /// work resumes quickly after the last playback stops; cancellation always wins.
    /// </summary>
    public static async Task WaitWhilePlaybackActiveAsync(ILogger logger, CancellationToken ct)
    {
        var announced = false;
        while (IsPlaybackActive())
        {
            if (!announced)
            {
                announced = true;
                logger.LogInformation(
                    "[Episode Tools] Pausing: someone is watching; work resumes when playback stops.");
            }

            await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Starts a watchdog that keeps <paramref name="flagPath"/> in sync with the playback
    /// state ("paused" / "run") for the bash convert script to poll between files.
    /// Dispose stops it and removes the flag, releasing the script to run freely.
    /// </summary>
    public static IDisposable StartWatchdog(string flagPath, ILogger logger, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var task = Task.Run(
            async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        WriteFlag(flagPath, IsPlaybackActive() ? "paused" : "run");
                        await Task.Delay(TimeSpan.FromSeconds(PollSeconds), token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Episode Tools] Playback watchdog stopped.");
                }
            },
            CancellationToken.None);

        return new WatchdogHandle(cts, task, flagPath);
    }

    private static void WriteFlag(string path, string state)
    {
        try
        {
            // Tmp + rename so bash never reads a half-written flag.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, state);
            File.Move(tmp, path, true);
        }
        catch (Exception)
        {
            // A failed flag write must never stop the run.
        }
    }

    private sealed class WatchdogHandle : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private readonly string _path;
        private bool _disposed;

        public WatchdogHandle(CancellationTokenSource cts, Task task, string path)
        {
            _cts = cts;
            _task = task;
            _path = path;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            try
            {
                _task.Wait(2000);
            }
            catch (Exception)
            {
                // The watchdog ending is never critical.
            }

            try
            {
                File.Delete(_path);
            }
            catch (Exception)
            {
            }

            try
            {
                File.Delete(_path + ".tmp");
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }
    }
}
