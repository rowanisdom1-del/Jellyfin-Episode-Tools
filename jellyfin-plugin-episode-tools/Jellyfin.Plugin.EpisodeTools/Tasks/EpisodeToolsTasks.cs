using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools.Tasks;

/// <summary>Shows which files would be renamed. Changes nothing.</summary>
public sealed class RenamePreviewTask : IScheduledTask
{
    private readonly ILogger<RenamePreviewTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="RenamePreviewTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public RenamePreviewTask(ILogger<RenamePreviewTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "1. Rename episodes: preview (changes nothing)";

    /// <inheritdoc />
    public string Key => "EpisodeToolsRenamePreview";

    /// <inheritdoc />
    public string Description => "Lists how episode files would be renamed to 'Show - S01E02 - Title'. Nothing is changed. Read the result in Dashboard > Logs.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ScriptRunner.RunRenameAsync(_logger, false, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}

/// <summary>Renames episode files for real.</summary>
public sealed class RenameApplyTask : IScheduledTask
{
    private readonly ILogger<RenameApplyTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="RenameApplyTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public RenameApplyTask(ILogger<RenameApplyTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "2. Rename episodes";

    /// <inheritdoc />
    public string Key => "EpisodeToolsRename";

    /// <inheritdoc />
    public string Description => "Renames episode files to 'Show - S01E02 - Title'. Nothing is overwritten. Run the preview first. Afterwards run 'Scan Media Library' if Jellyfin does not notice the changes by itself.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ScriptRunner.RunRenameAsync(_logger, true, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}

/// <summary>Shows which MKV files would be converted. Changes nothing.</summary>
public sealed class ConvertPreviewTask : IScheduledTask
{
    private readonly ILogger<ConvertPreviewTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConvertPreviewTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ConvertPreviewTask(ILogger<ConvertPreviewTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "3. Convert MKV to MP4: preview (changes nothing)";

    /// <inheritdoc />
    public string Key => "EpisodeToolsConvertPreview";

    /// <inheritdoc />
    public string Description => "Lists the .mkv files that would be converted to .mp4 and any that would be skipped. Nothing is changed. Read the result in Dashboard > Logs.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ScriptRunner.RunConvertAsync(_logger, false, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}

/// <summary>Converts MKV to MP4 without re-encoding.</summary>
public sealed class ConvertApplyTask : IScheduledTask
{
    private readonly ILogger<ConvertApplyTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConvertApplyTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ConvertApplyTask(ILogger<ConvertApplyTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "4. Convert MKV to MP4 (lossless)";

    /// <inheritdoc />
    public string Key => "EpisodeToolsConvert";

    /// <inheritdoc />
    public string Description => "Copies video and audio of each .mkv into an .mp4 without re-encoding, so quality stays identical. The .mkv is kept unless you turned on deleting in the plugin settings. Can be stopped with the stop button.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ScriptRunner.RunConvertAsync(_logger, true, progress, cancellationToken, redo: false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}

/// <summary>Checks subtitle tracks for signs/songs/lyrics problems. Changes nothing.</summary>
public sealed class SubCheckTask : IScheduledTask
{
    private readonly ILogger<SubCheckTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubCheckTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public SubCheckTask(ILogger<SubCheckTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "5. Check subtitles (reports problems, changes nothing)";

    /// <inheritdoc />
    public string Key => "EpisodeToolsSubCheck";

    /// <inheritdoc />
    public string Description => "Analyses the subtitle tracks of every episode: flags signs/songs/lyrics tracks, lyrics-heavy content and broken timing, and warns when the default track is the wrong one. Read the result under Dashboard > Plugins > Episode Tools > Episode Tools Sub Check.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ScriptRunner.RunSubCheckAsync(_logger, progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}

/// <summary>Re-converts MKV to MP4, replacing existing .mp4 files after verification.</summary>
public sealed class ConvertRedoTask : IScheduledTask
{
    private readonly ILogger<ConvertRedoTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="ConvertRedoTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ConvertRedoTask(ILogger<ConvertRedoTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "6. Re-convert MKV to MP4 (replaces existing .mp4)";

    /// <inheritdoc />
    public string Key => "EpisodeToolsConvertRedo";

    /// <inheritdoc />
    public string Description => "Same as task 4, but existing .mp4 files are replaced (only after the new file passes verification). Use this after changing track preferences or settings so already-converted episodes pick them up.";

    /// <inheritdoc />
    public string Category => "Episode Tools";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return ScriptRunner.RunConvertAsync(_logger, true, progress, cancellationToken, redo: true);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }
}
