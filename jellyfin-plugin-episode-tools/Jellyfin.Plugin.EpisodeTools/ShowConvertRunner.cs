using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Runs one convert-at-a-time conversion of a single show folder, triggered from the Shows tab.
/// Only one background run exists at any time; the Shows tab polls the status.
/// </summary>
internal static class ShowConvertRunner
{
    private static readonly object Lock = new();
    private static Task? _running;
    private static CancellationTokenSource? _cancellation;
    private static string? _folder;
    private static DateTime _started;
    private static string _lastMessage = string.Empty;
    private static string _result = string.Empty;

    /// <summary>
    /// Starts a conversion of a single show folder. Returns false when a run is already active.
    /// </summary>
    public static bool TryStart(string folder, ILogger logger)
    {
        lock (Lock)
        {
            if (_running is not null && !_running.IsCompleted)
            {
                return false;
            }

            _folder = folder;
            _started = DateTime.UtcNow;
            _lastMessage = "starting";
            _result = string.Empty;
            _cancellation = new CancellationTokenSource();
            var progress = new Progress<double>(p => _lastMessage = $"converting… {p:0}%");
            var loggerLocal = logger;
            var ct = _cancellation.Token;
            _running = Task.Run(async () =>
            {
                try
                {
                    await ScriptRunner.RunConvertAsync(
                        loggerLocal, true, progress, ct, redo: false, folderOverride: folder, remuxExisting: true)
                        .ConfigureAwait(false);
                    _result = "finished";
                    _lastMessage = "finished";
                }
                catch (OperationCanceledException)
                {
                    _result = "stopped";
                    _lastMessage = "stopped";
                }
                catch (Exception ex)
                {
                    _result = "failed: " + ex.Message;
                    _lastMessage = _result;
                    loggerLocal.LogError(ex, "[Episode Tools] Per-show conversion failed.");
                }
            }, CancellationToken.None);
            return true;
        }
    }

    /// <summary>Stops the running per-show conversion politely.</summary>
    public static void Stop()
    {
        lock (Lock)
        {
            _cancellation?.Cancel();
        }
    }

    /// <summary>Snapshot for the status endpoint.</summary>
    public static object Status()
    {
        lock (Lock)
        {
            var running = _running is not null && !_running.IsCompleted;
            return new
            {
                running,
                folder = _folder,
                started = running ? _started : (DateTime?)null,
                message = _lastMessage,
                result = _result
            };
        }
    }
}
