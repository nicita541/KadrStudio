using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

public enum TimelineEditOperation
{
    Move,
    TrimLeft,
    TrimRight
}

/// <summary>
/// Immutable result of a timeline gesture. The control may render a transient
/// draft while dragging, but only EditorSession is allowed to commit it.
/// </summary>
public abstract record TimelineEditIntent(Guid ItemId, TimelineEditOperation Operation);

public sealed record MediaTimelineEditIntent(
    Guid ClipId,
    TimelineEditOperation EditOperation,
    TrackKind TargetTrackKind,
    int TargetTrackIndex,
    TimelineTime Start,
    TimelineTime SourceIn,
    TimelineTime Duration)
    : TimelineEditIntent(ClipId, EditOperation);

public sealed record TextTimelineEditIntent(
    Guid TextClipId,
    TimelineEditOperation EditOperation,
    TimelineTime Start,
    TimelineTime Duration)
    : TimelineEditIntent(TextClipId, EditOperation);
