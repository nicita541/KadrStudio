using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

[Flags]
public enum ProjectChangeKind
{
    None = 0,
    Metadata = 1,
    Video = 2,
    Audio = 4,
    Overlay = 8,
    Timeline = 16
}

/// <summary>
/// The smallest known render result invalidation produced by an immutable edit.
/// Cache consumers use this contract instead of guessing from UI command names.
/// </summary>
public sealed record ProjectChangeSet
{
    public static ProjectChangeSet Empty { get; } = new();

    public ProjectChangeKind Kind { get; init; }
    public ImmutableArray<TimeRange> VideoRanges { get; init; } = [];
    public ImmutableArray<TimeRange> AudioRanges { get; init; } = [];
    public ImmutableArray<TimeRange> OverlayRanges { get; init; } = [];
    public ImmutableHashSet<Guid> SourceIds { get; init; } = ImmutableHashSet<Guid>.Empty;
    public ImmutableHashSet<Guid> TrackIds { get; init; } = ImmutableHashSet<Guid>.Empty;
    public ImmutableHashSet<Guid> EntityIds { get; init; } = ImmutableHashSet<Guid>.Empty;

    public bool IsEmpty => Kind == ProjectChangeKind.None;
    public bool InvalidatesVideo => (Kind & ProjectChangeKind.Video) != 0;
    public bool InvalidatesAudio => (Kind & ProjectChangeKind.Audio) != 0;
    public bool InvalidatesOverlay => (Kind & ProjectChangeKind.Overlay) != 0;

    public static ProjectChangeSet Between(ProjectState before, ProjectState after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(before, after) || before == after) return Empty;

        var video = new List<TimeRange>();
        var audio = new List<TimeRange>();
        var overlay = new List<TimeRange>();
        var sourceIds = ImmutableHashSet.CreateBuilder<Guid>();
        var trackIds = ImmutableHashSet.CreateBuilder<Guid>();
        var entityIds = ImmutableHashSet.CreateBuilder<Guid>();
        var kind = ProjectChangeKind.Metadata;

        var beforeTracks = before.Tracks.ToDictionary(item => item.Id);
        var afterTracks = after.Tracks.ToDictionary(item => item.Id);
        foreach (var id in beforeTracks.Keys.Union(afterTracks.Keys))
        {
            beforeTracks.TryGetValue(id, out var oldTrack);
            afterTracks.TryGetValue(id, out var newTrack);
            if (oldTrack == newTrack) continue;
            trackIds.Add(id);
            entityIds.Add(id);
            kind |= ProjectChangeKind.Timeline;
            AddTrackRanges(oldTrack, before, video, audio, overlay, ref kind);
            AddTrackRanges(newTrack, after, video, audio, overlay, ref kind);
        }

        foreach (var id in before.Sources.Keys.Union(after.Sources.Keys))
        {
            before.Sources.TryGetValue(id, out var oldSource);
            after.Sources.TryGetValue(id, out var newSource);
            if (oldSource == newSource) continue;
            sourceIds.Add(id);
            entityIds.Add(id);
            foreach (var clip in before.MediaClips.Where(item => item.SourceId == id))
                AddMediaRange(before, clip, video, audio, ref kind);
            foreach (var clip in after.MediaClips.Where(item => item.SourceId == id))
                AddMediaRange(after, clip, video, audio, ref kind);
        }

        var beforeMedia = before.MediaClips.ToDictionary(item => item.Id);
        var afterMedia = after.MediaClips.ToDictionary(item => item.Id);
        foreach (var id in beforeMedia.Keys.Union(afterMedia.Keys))
        {
            beforeMedia.TryGetValue(id, out var oldClip);
            afterMedia.TryGetValue(id, out var newClip);
            if (oldClip == newClip) continue;
            entityIds.Add(id);
            kind |= ProjectChangeKind.Timeline;
            if (oldClip is not null) AddMediaRange(before, oldClip, video, audio, ref kind);
            if (newClip is not null) AddMediaRange(after, newClip, video, audio, ref kind);
        }

        var beforeText = before.TextClips.ToDictionary(item => item.Id);
        var afterText = after.TextClips.ToDictionary(item => item.Id);
        foreach (var id in beforeText.Keys.Union(afterText.Keys))
        {
            beforeText.TryGetValue(id, out var oldClip);
            afterText.TryGetValue(id, out var newClip);
            if (oldClip == newClip) continue;
            entityIds.Add(id);
            kind |= ProjectChangeKind.Overlay | ProjectChangeKind.Timeline;
            if (oldClip is not null) overlay.Add(oldClip.Range);
            if (newClip is not null) overlay.Add(newClip.Range);
        }

        var beforeTransitions = before.Transitions.ToDictionary(item => item.Id);
        var afterTransitions = after.Transitions.ToDictionary(item => item.Id);
        foreach (var id in beforeTransitions.Keys.Union(afterTransitions.Keys))
        {
            beforeTransitions.TryGetValue(id, out var oldTransition);
            afterTransitions.TryGetValue(id, out var newTransition);
            if (oldTransition == newTransition) continue;
            entityIds.Add(id);
            kind |= ProjectChangeKind.Timeline;
            AddTransitionRange(before, oldTransition, video, audio, ref kind);
            AddTransitionRange(after, newTransition, video, audio, ref kind);
        }

        if (!before.Markers.SequenceEqual(after.Markers) || before.InPoint != after.InPoint || before.OutPoint != after.OutPoint)
            kind |= ProjectChangeKind.Timeline;

        if (before.CanvasWidth != after.CanvasWidth || before.CanvasHeight != after.CanvasHeight || before.FrameRate != after.FrameRate)
        {
            AddWholeProjectRange(before, video, overlay, ref kind);
            AddWholeProjectRange(after, video, overlay, ref kind);
        }

        return new ProjectChangeSet
        {
            Kind = kind,
            VideoRanges = Merge(video),
            AudioRanges = Merge(audio),
            OverlayRanges = Merge(overlay),
            SourceIds = sourceIds.ToImmutable(),
            TrackIds = trackIds.ToImmutable(),
            EntityIds = entityIds.ToImmutable()
        };
    }

    private static void AddTrackRanges(
        TimelineTrack? track,
        ProjectState project,
        ICollection<TimeRange> video,
        ICollection<TimeRange> audio,
        ICollection<TimeRange> overlay,
        ref ProjectChangeKind kind)
    {
        if (track is null) return;
        if (track.Kind == TrackKind.Text)
        {
            kind |= ProjectChangeKind.Overlay;
            foreach (var clip in project.TextClips.Where(item => item.TrackId == track.Id)) overlay.Add(clip.Range);
            return;
        }
        foreach (var clip in project.MediaClips.Where(item => item.TrackId == track.Id))
            AddMediaRange(project, clip, video, audio, ref kind);
    }

    private static void AddMediaRange(
        ProjectState project,
        MediaClip clip,
        ICollection<TimeRange> video,
        ICollection<TimeRange> audio,
        ref ProjectChangeKind kind)
    {
        switch (project.FindTrack(clip.TrackId)?.Kind)
        {
            case TrackKind.Visual:
                kind |= ProjectChangeKind.Video;
                video.Add(clip.Range);
                break;
            case TrackKind.Audio:
                kind |= ProjectChangeKind.Audio;
                audio.Add(clip.Range);
                break;
        }
    }

    private static void AddWholeProjectRange(
        ProjectState project,
        ICollection<TimeRange> video,
        ICollection<TimeRange> overlay,
        ref ProjectChangeKind kind)
    {
        if (project.Duration <= TimelineTime.Zero) return;
        var range = new TimeRange(TimelineTime.Zero, project.Duration);
        video.Add(range);
        overlay.Add(range);
        kind |= ProjectChangeKind.Video | ProjectChangeKind.Overlay;
    }

    private static void AddTransitionRange(
        ProjectState project,
        TimelineTransition? transition,
        ICollection<TimeRange> video,
        ICollection<TimeRange> audio,
        ref ProjectChangeKind kind)
    {
        if (transition is null) return;
        if (project.FindTrack(transition.TrackId)?.Kind == TrackKind.Audio)
        {
            audio.Add(transition.Range);
            kind |= ProjectChangeKind.Audio;
        }
        else
        {
            video.Add(transition.Range);
            kind |= ProjectChangeKind.Video;
        }
    }

    private static ImmutableArray<TimeRange> Merge(IEnumerable<TimeRange> ranges)
    {
        var ordered = ranges.OrderBy(item => item.Start).ThenBy(item => item.End).ToArray();
        if (ordered.Length == 0) return [];
        var result = ImmutableArray.CreateBuilder<TimeRange>();
        var start = ordered[0].Start;
        var end = ordered[0].End;
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.Start <= end)
            {
                if (next.End > end) end = next.End;
                continue;
            }
            result.Add(new TimeRange(start, end - start));
            start = next.Start;
            end = next.End;
        }
        result.Add(new TimeRange(start, end - start));
        return result.ToImmutable();
    }
}
