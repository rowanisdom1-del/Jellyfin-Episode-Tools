using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools;

/// <summary>
/// Unpacks the embedded scripts and runs them, sending every output line to the Jellyfin log
/// (Dashboard > Logs). A script that ends with an error makes the scheduled task show as failed.
/// </summary>
internal static class ScriptRunner
{
    private const int TailLines = 40;
    private const string SecondsToWaitAfterStop = "30";

    /// <summary>
    /// Runs the rename script. Without <paramref name="apply"/> it only shows what would change.
    /// </summary>
    public static Task RunRenameAsync(ILogger logger, bool apply, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = GetConfig();
        var args = new List<string>();
        if (apply)
        {
            args.Add("--apply");
        }

        var dir = (cfg.RenameDirectory ?? string.Empty).Trim();
        if (dir.Length > 0)
        {
            args.Add(dir);
        }

        return RunAsync(logger, "python3", "rename_episodes.py", args, 0, progress, cancellationToken);
    }

    /// <summary>
    /// Runs the MKV to MP4 script. Without <paramref name="apply"/> it only lists what would be converted.
    /// </summary>
    public static Task RunConvertAsync(ILogger logger, bool apply, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = GetConfig();
        var args = new List<string>();
        if (apply)
        {
            args.Add("--apply");
            if (cfg.DeleteOriginalsAfterConvert)
            {
                args.Add("--delete-originals");
                logger.LogWarning("[Episode Tools] Settings say: delete each .mkv after its .mp4 has been checked.");
            }
        }

        if (cfg.ConvertUnusualAudioToAac)
        {
            args.Add("--audio-to-aac");
            if (apply && cfg.DeleteOriginalsAfterConvert)
            {
                logger.LogWarning("[Episode Tools] Audio such as DTS/TrueHD/FLAC becomes AAC. Together with deleting the .mkv, the original audio is gone for good.");
            }
        }

        var dir = (cfg.ConvertDirectory ?? string.Empty).Trim();
        if (dir.Length > 0)
        {
            args.Add(dir);
        }

        return RunAsync(logger, "bash", "convert_mkv_to_mp4.sh", args, CountMkvFiles(dir), progress, cancellationToken);
    }

    private static PluginConfiguration GetConfig()
    {
        return Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    private static int CountMkvFiles(string dir)
    {
        if (dir.Length == 0)
        {
            return 0;
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive
            };
            return Directory.EnumerateFiles(dir, "*.mkv", options).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Writes the embedded script to disk. The file is replaced in one step, so a script that is
    /// already running keeps working.
    /// </summary>
    private static string ExtractScript(string resourceName)
    {
        var baseDir = Plugin.Instance?.WorkFolder ?? Path.Combine(Path.GetTempPath(), "episode-tools");
        var dir = Path.Combine(baseDir, "scripts");
        Directory.CreateDirectory(dir);

        var target = Path.Combine(dir, resourceName);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        using (var stream = typeof(ScriptRunner).Assembly.GetManifestResourceStream(resourceName))
        {
            if (stream is null)
            {
                throw new InvalidOperationException("The script " + resourceName + " is missing inside the plugin.");
            }

            using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.CopyTo(file);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                temp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        File.Move(temp, target, true);
        return target;
    }

    private static async Task RunAsync(
        ILogger logger,
        string interpreter,
        string resourceName,
        List<string> scriptArgs,
        int totalFiles,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var scriptPath = ExtractScript(resourceName);

        // "sh -c" lets us set a umask first, so renamed and converted files stay readable for everybody
        // (Jellyfin may run with a strict umask). "$0" is the interpreter, "$@" the script and its arguments.
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("umask 0022; exec \"$0\" \"$@\"");
        psi.ArgumentList.Add(interpreter);
        if (interpreter == "python3")
        {
            psi.ArgumentList.Add("-u");
        }

        psi.ArgumentList.Add(scriptPath);
        foreach (var arg in scriptArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        // Jellyfin ships its own ffmpeg in this folder. It is added last, so a normal ffmpeg wins.
        var path = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.IsNullOrEmpty(path)
            ? "/usr/local/bin:/usr/bin:/bin:/usr/lib/jellyfin-ffmpeg"
            : path + ":/usr/lib/jellyfin-ffmpeg";
        psi.Environment["PYTHONUTF8"] = "1";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG")))
        {
            psi.Environment["LANG"] = "C.UTF-8";
        }

        logger.LogInformation("[Episode Tools] Starting: {Interpreter} {Script} {Args}", interpreter, resourceName, string.Join(' ', scriptArgs));
        progress.Report(0);

        var tail = new Queue<string>();
        var filesSeen = 0;

        void OnLine(string? line)
        {
            if (line is null)
            {
                return;
            }

            logger.LogInformation("[Episode Tools] {Line}", line);
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > TailLines)
                {
                    tail.Dequeue();
                }
            }

            if (totalFiles > 0 && line.StartsWith("File:", StringComparison.Ordinal))
            {
                var seen = Interlocked.Increment(ref filesSeen);
                progress.Report(Math.Min(99.0, seen * 100.0 / totalFiles));
            }
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("Could not start /bin/sh: " + ex.Message, ex);
        }

        process.StandardInput.Close();   // the scripts must never wait for typed input
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopGracefullyAsync(process, logger).ConfigureAwait(false);
            throw;
        }

        process.WaitForExit();   // makes sure all output lines have been delivered

        var exitCode = process.ExitCode;
        if (exitCode == 127)
        {
            throw new InvalidOperationException(
                "'" + interpreter + "' was not found on this server. Install it first (for example: sudo apt install python3 ffmpeg).");
        }

        if (exitCode != 0)
        {
            string lastLines;
            lock (tail)
            {
                lastLines = string.Join(" | ", tail.Skip(Math.Max(0, tail.Count - 5)));
            }

            throw new InvalidOperationException(
                resourceName + " ended with exit code " + exitCode + ". See Dashboard > Logs for details. Last lines: " + lastLines);
        }

        progress.Report(100);
        logger.LogInformation("[Episode Tools] {Script} finished.", resourceName);
    }

    /// <summary>
    /// Politely asks the script to stop (the convert script then removes its half-finished file),
    /// and ends it by force if it takes too long.
    /// </summary>
    private static async Task StopGracefullyAsync(Process process, ILogger logger)
    {
        logger.LogWarning("[Episode Tools] Task cancelled. Stopping the script...");
        try
        {
            var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using (var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", "pkill -TERM -P " + pid + " 2>/dev/null; kill -TERM " + pid + " 2>/dev/null; true" },
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                killer?.WaitForExit(3000);
            }

            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(SecondsToWaitAfterStop, System.Globalization.CultureInfo.InvariantCulture)));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "[Episode Tools] Could not stop the script politely.");
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (Exception)
            {
                // already gone
            }
        }
    }
}
