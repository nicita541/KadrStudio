using System.Collections.Immutable;

namespace KadrStudio.Core.Domain;

public enum SequenceStatus
{
    Original,
    Draft,
    Accepted
}

public enum SequenceTargetFormat
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

/// <summary>
/// Immutable timeline snapshot. AI work is isolated in a Draft sequence; it never
/// carries a reference to a transient planning object or an execution checkpoint.
/// </summary>
public sealed record SequenceState(
    Guid Id,
    string Name,
    long Revision,
    SequenceStatus Status,
    SequenceTargetFormat TargetFormat,
    SequenceSettings Settings,
    ImmutableArray<TimelineTrack> Tracks,
    ImmutableArray<MediaClip> MediaClips,
    ImmutableArray<TextClip> TextClips,
    ImmutableArray<TimelineTransition> Transitions,
    ImmutableArray<TimelineMarker> Markers,
    TimelineTime? InPoint = null,
    TimelineTime? OutPoint = null,
    Guid? ParentSequenceId = null,
    ImmutableArray<SubtitleClip> SubtitleClips = default,
    ImmutableArray<TrackRenditionGroup> RenditionGroups = default,
    ImmutableArray<UpscaleJob> UpscaleJobs = default)
{
    public ImmutableArray<SubtitleClip> SubtitleClips { get; init; } =
        SubtitleClips.IsDefault ? [] : SubtitleClips;
    public ImmutableArray<TrackRenditionGroup> RenditionGroups { get; init; } =
        RenditionGroups.IsDefault ? [] : RenditionGroups;
    public ImmutableArray<UpscaleJob> UpscaleJobs { get; init; } =
        UpscaleJobs.IsDefault ? [] : UpscaleJobs;

    public TimelineTime Duration
    {
        get
        {
            var mediaEnd = MediaClips.IsDefaultOrEmpty ? TimelineTime.Zero : MediaClips.Max(item => item.End);
            var textEnd = TextClips.IsDefaultOrEmpty ? TimelineTime.Zero : TextClips.Max(item => item.End);
            var subtitleEnd = SubtitleClips.IsDefaultOrEmpty ? TimelineTime.Zero : SubtitleClips.Max(item => item.End);
            return mediaEnd >= textEnd
                ? mediaEnd >= subtitleEnd ? mediaEnd : subtitleEnd
                : textEnd >= subtitleEnd ? textEnd : subtitleEnd;
        }
    }

    public static SequenceState Capture(
        ProjectState project,
        Guid id,
        string name,
        SequenceStatus status = SequenceStatus.Original,
        SequenceTargetFormat targetFormat = SequenceTargetFormat.Source,
        long revision = 0,
        Guid? parentSequenceId = null)
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
            project.SubtitleClips,
            project.RenditionGroups,
            project.UpscaleJobs);

    public bool Matches(ProjectState project)
        => Settings == project.Sequence &&
           Tracks.SequenceEqual(project.Tracks) &&
           MediaClips.SequenceEqual(project.MediaClips) &&
           SubtitleClips.SequenceEqual(project.SubtitleClips) &&
           TextClips.SequenceEqual(project.TextClips) &&
           Transitions.SequenceEqual(project.Transitions) &&
           Markers.SequenceEqual(project.Markers) &&
           RenditionGroups.SequenceEqual(project.RenditionGroups) &&
           UpscaleJobs.SequenceEqual(project.UpscaleJobs) &&
           InPoint == project.InPoint &&
           OutPoint == project.OutPoint;

    public SequenceState CaptureTimeline(ProjectState project, bool incrementRevision)
        => this with
        {
            Revision = incrementRevision ? checked(Revision + 1) : Revision,
            Settings = project.Sequence,
            Tracks = project.Tracks,
            MediaClips = project.MediaClips,
            SubtitleClips = project.SubtitleClips,
            TextClips = project.TextClips,
            Transitions = project.Transitions,
            Markers = project.Markers,
            RenditionGroups = project.RenditionGroups,
            UpscaleJobs = project.UpscaleJobs,
            InPoint = project.InPoint,
            OutPoint = project.OutPoint
        };
}

/// <summary>User-authored protected/excluded source range consumed by the v2 director.</summary>
public sealed record SourceAnnotation(
    Guid Id,
    Guid SourceId,
    SourceAnnotationKind Kind,
    TimeRange SourceRange,
    string Note,
    DateTimeOffset CreatedAt);

public enum AiChatRole
{
    User,
    Assistant
}

// Explicit values preserve old persisted messages without keeping the old plan-approval API.
public enum AiChatMessageKind
{
    Text = 0,
    Progress = 1,
    Error = 2,
    Draft = 5,
    AgentMemory = 6
}

public enum AiChatOperationState
{
    Completed,
    Running,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record AiChatMessage(
    Guid Id,
    AiChatRole Role,
    AiChatMessageKind Kind,
    string Text,
    DateTimeOffset CreatedAt,
    AiChatOperationState OperationState = AiChatOperationState.Completed,
    int ProgressPercent = 100,
    Guid? SequenceId = null,
    Guid? AgentTaskId = null);

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
