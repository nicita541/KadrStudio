using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Upscaling;

public sealed record UpscaleTrackResult(
    TimelineTrack UpscaledTrack,
    ImmutableArray<MediaSource> Sources,
    ImmutableArray<MediaClip> Clips,
    TrackRenditionGroup RenditionGroup);

public sealed record UpscaleResult(
    Guid JobId,
    ImmutableArray<UpscaleTrackResult> Tracks);

public sealed record UpscaleComparisonFrames(
    string OriginalFramePath,
    string UpscaledFramePath,
    string TemporaryDirectory);

public interface IAiUpscaleService
{
    Task<UpscaleResult> UpscaleAsync(
        ProjectState project,
        Guid jobId,
        UpscaleRequest request,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<UpscaleComparisonFrames> CreateComparisonFramesAsync(
        ProjectState project,
        Guid renditionGroupId,
        TimelineTime timelineTime,
        CancellationToken cancellationToken = default);
}
