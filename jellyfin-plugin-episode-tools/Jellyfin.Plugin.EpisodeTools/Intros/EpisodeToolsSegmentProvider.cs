using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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

        return ValueTask.FromResult(IntrosRunner.HasAnySegment(episode.Path));
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
            if (intro is null)
            {
                return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(results);
            }

            // Skip when an identical segment from an earlier run is already stored.
            var startTicks = (long)(intro.Start * TimeSpan.TicksPerSecond);
            var endTicks = (long)(intro.End * TimeSpan.TicksPerSecond);

            // Clamp to the file runtime: a mistyped override end must not publish an intro
            // segment that reaches past the end of the video. Drop inverted/zero-length
            // windows instead of emitting a broken segment.
            if (item?.RunTimeTicks is > 0)
            {
                endTicks = Math.Min(endTicks, item.RunTimeTicks.Value - 1);
            }

            if (startTicks < endTicks)
            {
                var hasIntro = false;
                foreach (var existing in request.ExistingSegments)
                {
                    if (existing.Type == MediaSegmentType.Intro &&
                        Math.Abs(existing.StartTicks - startTicks) < TimeSpan.TicksPerSecond &&
                        Math.Abs(existing.EndTicks - endTicks) < TimeSpan.TicksPerSecond)
                    {
                        hasIntro = true;
                        break;
                    }
                }

                if (!hasIntro)
                {
                    results.Add(new MediaSegmentDto
                    {
                        Id = StableId(path, "Intro"),
                        ItemId = request.ItemId,
                        Type = MediaSegmentType.Intro,
                        StartTicks = startTicks,
                        EndTicks = endTicks
                    });
                }
            }

            // Outros/credits (F2): published only while the setting is on, so turning it
            // off stops new segments without touching stored ones.
            var detectOutros = (Plugin.Instance?.Configuration?.DetectOutros).GetValueOrDefault(false);
            if (detectOutros && intro.OutroStart is { } outroStart && intro.OutroEnd is { } outroEnd &&
                outroEnd - outroStart >= 1)
            {
                var outroStartTicks = (long)(outroStart * TimeSpan.TicksPerSecond);
                var outroEndTicks = (long)(outroEnd * TimeSpan.TicksPerSecond);
                if (item?.RunTimeTicks is > 0)
                {
                    outroEndTicks = Math.Min(outroEndTicks, item.RunTimeTicks.Value - 1);
                }

                if (outroStartTicks < outroEndTicks)
                {
                    var hasOutro = false;
                    foreach (var existing in request.ExistingSegments)
                    {
                        if (existing.Type == MediaSegmentType.Outro &&
                            Math.Abs(existing.StartTicks - outroStartTicks) < TimeSpan.TicksPerSecond &&
                            Math.Abs(existing.EndTicks - outroEndTicks) < TimeSpan.TicksPerSecond)
                        {
                            hasOutro = true;
                            break;
                        }
                    }

                    if (!hasOutro)
                    {
                        results.Add(new MediaSegmentDto
                        {
                            Id = StableId(path, "Outro"),
                            ItemId = request.ItemId,
                            Type = MediaSegmentType.Outro,
                            StartTicks = outroStartTicks,
                            EndTicks = outroEndTicks
                        });
                    }
                }
            }
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

    private static Guid StableId(string path, string kind)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("EpisodeTools" + kind + ":" + path.ToUpperInvariant());
        return new Guid(System.Security.Cryptography.MD5.HashData(bytes));
    }
}
