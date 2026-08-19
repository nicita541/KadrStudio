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
    Superseded,
    NeedsInput
}

public enum MaterialProfileKind
{
    Game,
    Anime,
    General
}

public enum AutomationRecipeKind
{
    Highlights,
    MergeEpisodes
}

public enum AnalysisStrategyKind
{
    TechnicalThenVision
}

public enum StructuralSegmentKind
{
    Unknown,
    Opening,
    Ending,
    Recap,
    Preview,
    Credits,
    Story,
    PostCreditsStory
}

public enum StructuralSegmentDisposition
{
    Retain,
    Remove,
    NeedsInput
}

public enum BoundaryResolutionStatus
{
    Suggested,
    Verified,
    UserConfirmed
}

public enum BoundaryPrecision
{
    Coarse,
    Frame,
    ExactPresentationTimestamp
}

public enum MontageDecisionKind
{
    SourceOrder,
    OpeningSelection,
    SegmentClassification,
    SegmentStart,
    SegmentEnd
}

public enum MontageDecisionStatus
{
    Pending,
    Resolved
}

public enum MontageRole
{
    Hook,
    Setup,
    Development,
    Payoff,
    Ending,
    Opening
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

public sealed record ResolvedBoundary(
    TimelineTime ProposedTime,
    TimelineTime ResolvedTime,
    BoundaryResolutionStatus Status,
    BoundaryPrecision Precision,
    double Confidence,
    ImmutableArray<AnalysisEvidence> Evidence)
{
    public bool IsConfirmed => Status is BoundaryResolutionStatus.Verified or BoundaryResolutionStatus.UserConfirmed;
}

public enum AiChatRole
{
    User,
    Assistant
}

public enum AiChatMessageKind
{
    Text,
    Progress,
    Error,
    Plan,
    Question,
    Draft
}

public enum AiChatOperationState
{
    Completed,
    Running,
    Failed,
    Cancelled,
    Interrupted
}

public enum AiChatEditCommandKind
{
    DeleteRange,
    SplitAt,
    DeleteSelected
}

public sealed record StructuralSegment(
    Guid Id,
    Guid SourceId,
    StructuralSegmentKind Kind,
    TimeRange SourceRange,
    StructuralSegmentDisposition Disposition,
    double Confidence,
    ResolvedBoundary StartBoundary,
    ResolvedBoundary EndBoundary,
    ImmutableArray<AnalysisEvidence> Evidence)
{
    public bool HasConfirmedBoundaries => StartBoundary.IsConfirmed && EndBoundary.IsConfirmed;
}

public sealed record MediaAnalysisManifest(
    Guid SourceId,
    string SourceFingerprint,
    string PipelineVersion,
    string Model,
    string ProfileId,
    int ProfileVersion,
    DateTimeOffset CreatedAt,
    ImmutableArray<AnalysisSegment> Segments,
    ImmutableArray<StructuralSegment> StructuralSegments = default)
{
    public ImmutableArray<StructuralSegment> StructuralSegments { get; init; } =
        StructuralSegments.IsDefault ? [] : StructuralSegments;
}

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
    string PlanningGuidance,
    MaterialProfileKind Kind = MaterialProfileKind.Game)
{
    public string ContentFamily => GameFamily;
}

public sealed record AutomationPreset(
    string Id,
    int Version,
    string DisplayName,
    string ProfileId,
    AutomationRecipeKind Recipe,
    AnalysisStrategyKind AnalysisStrategy,
    double RequiredConfidence = 0.85);

public sealed record MontageDecisionOption(
    string Id,
    string Label,
    string Description = "");

public sealed record MontageDecision(
    Guid Id,
    MontageDecisionKind Kind,
    string Prompt,
    ImmutableArray<MontageDecisionOption> Options,
    MontageDecisionStatus Status = MontageDecisionStatus.Pending,
    string Answer = "",
    Guid? SourceId = null,
    Guid? SegmentId = null,
    TimelineTime? SuggestedTime = null,
    TimelineTime? ResolvedTime = null)
{
    public bool IsResolved => Status == MontageDecisionStatus.Resolved;
}

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
    ImmutableArray<MontageConstraint> Constraints,
    AutomationPreset? Preset = null);

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
    DateTimeOffset UpdatedAt,
    AutomationPreset? PresetSnapshot = null,
    ImmutableArray<MontageDecision> Decisions = default,
    ImmutableArray<StructuralSegment> StructuralSegments = default)
{
    public ImmutableArray<MontageDecision> Decisions { get; init; } =
        Decisions.IsDefault ? [] : Decisions;
    public ImmutableArray<StructuralSegment> StructuralSegments { get; init; } =
        StructuralSegments.IsDefault ? [] : StructuralSegments;

    public TimelineTime Duration => Items.IsDefaultOrEmpty
        ? TimelineTime.Zero
        : new TimelineTime(Items.Sum(item => item.SourceRange.Duration.Ticks));
}

public sealed record AiPlanCardSnapshot(
    string Title,
    string Summary,
    TimelineTime Duration,
    ImmutableArray<string> RetainedItems,
    ImmutableArray<string> RemovedItems,
    ImmutableArray<string> Warnings,
    bool CanCreateDraft)
{
    public ImmutableArray<string> RetainedItems { get; init; } =
        RetainedItems.IsDefault ? [] : RetainedItems;
    public ImmutableArray<string> RemovedItems { get; init; } =
        RemovedItems.IsDefault ? [] : RemovedItems;
    public ImmutableArray<string> Warnings { get; init; } =
        Warnings.IsDefault ? [] : Warnings;
}

public sealed record AiChatEditCommand(
    AiChatEditCommandKind Kind,
    double Start,
    double End,
    string Reason);

public sealed record AiChatMessage(
    Guid Id,
    AiChatRole Role,
    AiChatMessageKind Kind,
    string Text,
    DateTimeOffset CreatedAt,
    AiChatOperationState OperationState = AiChatOperationState.Completed,
    int ProgressPercent = 100,
    Guid? PlanId = null,
    Guid? DecisionId = null,
    Guid? SequenceId = null,
    string Answer = "",
    AiPlanCardSnapshot? PlanSnapshot = null,
    ImmutableArray<AiChatEditCommand> EditCommands = default)
{
    public ImmutableArray<AiChatEditCommand> EditCommands { get; init; } =
        EditCommands.IsDefault ? [] : EditCommands;
    public bool IsPendingQuestion => Kind == AiChatMessageKind.Question && string.IsNullOrWhiteSpace(Answer);
}

public sealed record AiConversation(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ImmutableArray<AiChatMessage> Messages)
{
    public ImmutableArray<AiChatMessage> Messages { get; init; } = Messages.IsDefault ? [] : Messages;

    public static AiConversation Create()
    {
        var now = DateTimeOffset.UtcNow;
        return new AiConversation(Guid.NewGuid(), now, now, []);
    }

    public AiConversation RecoverInterruptedOperations(DateTimeOffset? now = null)
    {
        if (Messages.All(message => message.OperationState != AiChatOperationState.Running)) return this;
        var recoveredAt = now ?? DateTimeOffset.UtcNow;
        return this with
        {
            UpdatedAt = recoveredAt,
            Messages = Messages.Select(message => message.OperationState == AiChatOperationState.Running
                ? message with
                {
                    Text = "Операция была прервана. Отправьте команду ещё раз.",
                    Kind = AiChatMessageKind.Error,
                    OperationState = AiChatOperationState.Interrupted
                }
                : message).ToImmutableArray()
        };
    }
}
