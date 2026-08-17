using System.Collections.Immutable;

namespace KadrStudio.Core.Domain;

public enum SequenceStatus
{
    Original,
    Draft,
    Accepted
}

public enum MontageTargetFormat
{
    Source,
    YouTube,
    Shorts
}

public enum SourceAnnotationKind
{
    Required,
    Excluded,
    Note
}

public enum MontageScopeKind
{
    MediaLibrary,
    SelectedSources,
    CurrentSequence,
    SelectedClips,
    InOutRange
}

public enum MontagePlanStatus
{
    Draft,
    Ready,
    Compiled,
    Superseded
}

public enum MontageRole
{
    Hook,
    Setup,
    Development,
    Payoff,
    Ending
}

public enum MontageEvidenceKind
{
    Technical,
    Transcript,
    Vision,
    UserAnnotation
}

public sealed record SequenceState(
    Guid Id,
    string Name,
    long Revision,
    SequenceStatus Status,
    MontageTargetFormat TargetFormat,
    SequenceSettings Settings,
    ImmutableArray<TimelineTrack> Tracks,
    ImmutableArray<MediaClip> MediaClips,
    ImmutableArray<TextClip> TextClips,
    ImmutableArray<TimelineTransition> Transitions,
    ImmutableArray<TimelineMarker> Markers,
    TimelineTime? InPoint = null,
    TimelineTime? OutPoint = null,
    Guid? ParentSequenceId = null,
    Guid? MontagePlanId = null)
{
    public TimelineTime Duration
    {
        get
        {
            var mediaEnd = MediaClips.IsDefaultOrEmpty ? TimelineTime.Zero : MediaClips.Max(item => item.End);
            var textEnd = TextClips.IsDefaultOrEmpty ? TimelineTime.Zero : TextClips.Max(item => item.End);
            return mediaEnd >= textEnd ? mediaEnd : textEnd;
        }
    }

    public static SequenceState Capture(
        ProjectState project,
        Guid id,
        string name,
        SequenceStatus status = SequenceStatus.Original,
        MontageTargetFormat targetFormat = MontageTargetFormat.Source,
        long revision = 0,
        Guid? parentSequenceId = null,
        Guid? montagePlanId = null)
        => new(
            id,
            string.IsNullOrWhiteSpace(name) ? "Последовательность" : name.Trim(),
            revision,
            status,
            targetFormat,
            project.Sequence,
            project.Tracks,
            project.MediaClips,
            project.TextClips,
            project.Transitions,
            project.Markers,
            project.InPoint,
            project.OutPoint,
            parentSequenceId,
            montagePlanId);

    public bool Matches(ProjectState project)
        => Settings == project.Sequence &&
           Tracks.SequenceEqual(project.Tracks) &&
           MediaClips.SequenceEqual(project.MediaClips) &&
           TextClips.SequenceEqual(project.TextClips) &&
           Transitions.SequenceEqual(project.Transitions) &&
           Markers.SequenceEqual(project.Markers) &&
           InPoint == project.InPoint &&
           OutPoint == project.OutPoint;

    public SequenceState CaptureTimeline(ProjectState project, bool incrementRevision)
        => this with
        {
            Revision = incrementRevision ? checked(Revision + 1) : Revision,
            Settings = project.Sequence,
            Tracks = project.Tracks,
            MediaClips = project.MediaClips,
            TextClips = project.TextClips,
            Transitions = project.Transitions,
            Markers = project.Markers,
            InPoint = project.InPoint,
            OutPoint = project.OutPoint
        };
}

public sealed record SourceAnnotation(
    Guid Id,
    Guid SourceId,
    SourceAnnotationKind Kind,
    TimeRange SourceRange,
    string Note,
    DateTimeOffset CreatedAt);

public sealed record AnalysisEvidence(
    MontageEvidenceKind Kind,
    string Summary,
    string Reference = "");

public sealed record AnalysisSegment(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    double MotionScore,
    double LoudnessScore,
    double SpeechScore,
    string Transcript,
    ImmutableDictionary<string, double> Tags,
    double Confidence,
    ImmutableArray<AnalysisEvidence> Evidence);

public sealed record MediaAnalysisManifest(
    Guid SourceId,
    string SourceFingerprint,
    string PipelineVersion,
    string Model,
    string ProfileId,
    int ProfileVersion,
    DateTimeOffset CreatedAt,
    ImmutableArray<AnalysisSegment> Segments);

public sealed record MediaAnalysisReference(
    Guid SourceId,
    string SourceFingerprint,
    string PipelineVersion,
    string Model,
    string ProfileId,
    int ProfileVersion,
    DateTimeOffset UpdatedAt);

public sealed record GameEditingProfile(
    string Id,
    int Version,
    string DisplayName,
    string GameFamily,
    ImmutableArray<string> EventTags,
    ImmutableDictionary<string, double> EventWeights,
    double MinimumSegmentSeconds,
    double MaximumSegmentSeconds,
    double ContextBeforeSeconds,
    double ContextAfterSeconds,
    string PlanningGuidance);

public sealed record MontageScope(
    MontageScopeKind Kind,
    ImmutableArray<Guid> SourceIds,
    Guid? SequenceId = null,
    ImmutableArray<Guid> ClipIds = default,
    TimeRange? TimelineRange = null);

public sealed record MontageConstraint(
    Guid Id,
    Guid SourceId,
    SourceAnnotationKind Kind,
    TimeRange SourceRange,
    string Note,
    bool IsHard = true);

public sealed record AutomationDependencyStamp(
    Guid ProjectId,
    Guid? InputSequenceId,
    long? InputSequenceRevision,
    ImmutableDictionary<Guid, string> SourceFingerprints,
    string AnalysisPipelineVersion,
    string Model,
    string ProfileId,
    int ProfileVersion);

public sealed record MontageRequest(
    Guid Id,
    MontageScope Scope,
    MontageTargetFormat TargetFormat,
    TimelineTime MinimumDuration,
    TimelineTime TargetDuration,
    TimelineTime MaximumDuration,
    string Brief,
    GameEditingProfile Profile,
    ImmutableArray<MontageConstraint> Constraints);

public sealed record MontagePlanItem(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    MontageRole Role,
    int Order,
    string Reason,
    double Confidence,
    ImmutableArray<AnalysisEvidence> Evidence,
    bool IsLocked = false,
    Guid? OriginClipId = null,
    TransitionKind? TransitionAfter = null,
    double Volume = 1,
    bool IncludeSubtitles = true,
    VideoParameters? Reframe = null);

public sealed record MontagePlan(
    Guid Id,
    Guid RequestId,
    string Title,
    string Summary,
    MontagePlanStatus Status,
    MontageTargetFormat TargetFormat,
    TimelineTime MinimumDuration,
    TimelineTime TargetDuration,
    TimelineTime MaximumDuration,
    GameEditingProfile ProfileSnapshot,
    AutomationDependencyStamp Dependencies,
    ImmutableArray<MontageConstraint> Constraints,
    ImmutableArray<MontagePlanItem> Items,
    ImmutableArray<string> Warnings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public TimelineTime Duration => Items.IsDefaultOrEmpty
        ? TimelineTime.Zero
        : new TimelineTime(Items.Sum(item => item.SourceRange.Duration.Ticks));
}
