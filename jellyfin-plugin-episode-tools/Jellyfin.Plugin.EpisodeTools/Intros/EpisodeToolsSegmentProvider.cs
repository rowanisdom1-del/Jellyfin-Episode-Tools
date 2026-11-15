using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EpisodeTools.Intros;

/// <summary>
/// Serves the intros found by the fast intro scanner as standard media segments, so every
/// Jellyfin client can offer an intro skip button. Runs after the "8. Scan intros" task has
/// stored its results; the built-in "Extract media segments" task copies them into the server.
/// </summary>
public sealed class EpisodeToolsSegmentProvider : IMediaSegmentProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<EpisodeToolsSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeToolsSegmentProvider"/> class.
    /// </summary>
    public EpisodeToolsSegmentProvider(ILibraryManager libraryManager, ILogger<EpisodeToolsSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Episode Tools Intros";

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        if (item is not Episode episode || string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(IntrosRunner.HasIntro(episode.Path));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var results = new List<MediaSegmentDto>();

        try
        {
            var item = _libraryManager.GetItemById(request.ItemId);
            var path = item?.Path;
            if (string.IsNullOrEmpty(path))
            {
                return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(results);
            }

            var intro = IntrosRunner.LoadResults().FirstOrDefault(i =>
                string.Equals(i.File, path, StringComparison.OrdinalIgnoreCase));
            if (intro is null || intro.End - intro.Start < 1)
            {
                return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(results);
            }

            // Skip when an identical segment from an earlier run is already stored.
            var startTicks = (long)(intro.Start * TimeSpan.TicksPerSecond);
            var endTicks = (long)(intro.End * TimeSpan.TicksPerSecond);
            foreach (var existing in request.ExistingSegments)
            {
                if (existing.Type == MediaSegmentType.Intro &&
                    Math.Abs(existing.StartTicks - startTicks) < TimeSpan.TicksPerSecond &&
                    Math.Abs(existing.EndTicks - endTicks) < TimeSpan.TicksPerSecond)
                {
                    return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(results);
                }
            }

            results.Add(new MediaSegmentDto
            {
                Id = StableId(path),
                ItemId = request.ItemId,
                Type = MediaSegmentType.Intro,
                StartTicks = startTicks,
                EndTicks = endTicks
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Episode Tools] Providing intro segments failed.");
        }

        return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(results);
    }

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static Guid StableId(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("EpisodeToolsIntro:" + path.ToUpperInvariant());
        return new Guid(System.Security.Cryptography.MD5.HashData(bytes));
    }
}
