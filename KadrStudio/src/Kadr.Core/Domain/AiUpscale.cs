using System.Collections.Immutable;

namespace KadrStudio.Core.Domain;

public enum UpscaleScaleMode
{
    Auto2160p,
    X2,
    X4
}

public enum UpscaleJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum TrackRenditionKind
{
    Original,
    Upscaled
}

public sealed record BoundaryCandidate(
    Guid Id,
    Guid SourceId,
    SegmentRole Role,
    bool IsStart,
    TimelineTime Time,
    FrameRate FrameRate,
    double Confidence,
    ImmutableArray<Guid> EvidenceFactIds,
    ImmutableArray<CoverageChannel> EvidenceChannels,
    string AnalyzerId,
    string AnalyzerVersion,
    string SourceLabel = "")
{
    public ImmutableArray<Guid> EvidenceFactIds { get; init; } = EvidenceFactIds.IsDefault ? [] : EvidenceFactIds;
    public ImmutableArray<CoverageChannel> EvidenceChannels { get; init; } = EvidenceChannels.IsDefault ? [] : EvidenceChannels;
    public long FrameNumber => Time.ToNearestFrame(FrameRate);
    public bool RequiresConfirmation => Confidence < 0.80 || EvidenceChannels.Distinct().Count() < 2;
}

public sealed record BoundaryConfirmation(
    Guid CandidateId,
    TimelineTime ConfirmedTime,
    long ConfirmedFrameNumber,
    DateTimeOffset ConfirmedAt,
    bool Accepted);

public sealed record BoundaryReviewRequest(
    Guid TaskId,
    ImmutableArray<BoundaryCandidate> Candidates)
{
    public ImmutableArray<BoundaryCandidate> Candidates { get; init; } = Candidates.IsDefault ? [] : Candidates;
}

public sealed record TrackRenditionGroup(
    Guid Id,
    Guid SequenceId,
    Guid OriginalTrackId,
    Guid UpscaledTrackId,
    TrackRenditionKind ActiveRendition,
    long SourceSequenceRevision,
    string InputFingerprint,
    string ModelId,
    string ModelSha256,
    UpscaleScaleMode ScaleMode,
    bool IsStale,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UpscaleJob(
    Guid Id,
    Guid SequenceId,
    ImmutableArray<Guid> OriginalTrackIds,
    UpscaleScaleMode ScaleMode,
    UpscaleJobState State,
    double Progress,
    string Message,
    string ModelSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Error = "")
{
    public ImmutableArray<Guid> OriginalTrackIds { get; init; } = OriginalTrackIds.IsDefault ? [] : OriginalTrackIds;
}

public sealed record UpscaleRequest(
    Guid SequenceId,
    ImmutableArray<Guid> VisualTrackIds,
    UpscaleScaleMode ScaleMode = UpscaleScaleMode.Auto2160p)
{
    public ImmutableArray<Guid> VisualTrackIds { get; init; } = VisualTrackIds.IsDefault ? [] : VisualTrackIds;
}

public sealed record UpscaleProgress(
    Guid JobId,
    double Value,
    string Message,
    int ProcessedFrames = 0,
    int TotalFrames = 0,
    TimeSpan? EstimatedRemaining = null);
