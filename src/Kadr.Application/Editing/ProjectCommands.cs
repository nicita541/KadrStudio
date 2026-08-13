using System.Collections.Immutable;
using KadrStudio.Core.Domain;
using static KadrStudio.Application.Editing.CommandHelpers;
using KadrStudio.Application.Media;

namespace KadrStudio.Application.Editing;

/// <summary>
/// Adapter command for UI migrations: the mutable view is mapped back to a full
/// immutable candidate and the editor session validates it before committing.
/// </summary>
public sealed record ReplaceProjectStateCommand(ProjectState Candidate, string Description) : IEditCommand
{
    public ProjectState Apply(ProjectState project) => Candidate;
}

public sealed record AddSourcesCommand(IReadOnlyList<MediaSource> Sources) : IEditCommand
{
    public string Description => "Добавить исходники";

    public ProjectState Apply(ProjectState project)
    {
        var sources = project.Sources;
        foreach (var source in Sources)
        {
            if (sources.ContainsKey(source.Id))
                throw new EditRejectedException($"Исходник {source.Id} уже существует.");
            sources = sources.Add(source.Id, source);
        }
        return project with { Sources = sources };
    }
}

public sealed record RelinkSourcesCommand(IReadOnlyList<RelinkCandidate> Candidates) : IEditCommand
{
    public string Description => "Relink media";

    public ProjectState Apply(ProjectState project)
    {
        var sources = project.Sources;
        foreach (var candidate in Candidates)
        {
            if (!candidate.CanApply || candidate.Probe is null)
                throw new EditRejectedException($"Source {candidate.SourceId} has no compatible relink candidate.");
            if (!sources.TryGetValue(candidate.SourceId, out var source))
                throw new EditRejectedException($"Source {candidate.SourceId} does not exist in the project.");
            sources = sources.SetItem(source.Id, MediaRelink.Apply(source, candidate.Probe));
        }
        return project with { Sources = sources };
    }
}

public sealed record RefreshMediaOnlineStateCommand(IReadOnlyDictionary<Guid, bool> OnlineBySource) : IEditCommand
{
    public string Description => "Refresh media online state";

    public ProjectState Apply(ProjectState project) => project with
    {
        Sources = project.Sources.ToImmutableDictionary(
            pair => pair.Key,
            pair => OnlineBySource.TryGetValue(pair.Key, out var online)
                ? pair.Value with { OnlineState = online ? MediaOnlineState.Online : MediaOnlineState.Offline }
                : pair.Value)
    };
}

public sealed record AddTrackCommand(TimelineTrack Track) : IEditCommand
{
    public string Description => "Добавить дорожку";
    public ProjectState Apply(ProjectState project)
        => project with { Tracks = project.Tracks.Add(Track) };
}

public sealed record AddMediaClipsCommand(IReadOnlyList<MediaClip> Clips) : IEditCommand
{
    public string Description => "Добавить клипы";
    public ProjectState Apply(ProjectState project)
        => project with { MediaClips = project.MediaClips.AddRange(Clips) };
}

public sealed record EnsureTrackAndAddMediaClipsCommand(
    IReadOnlyList<(TrackKind Kind, int Index, MediaClip Clip)> Items) : IEditCommand
{
    public string Description => "Добавить клипы на дорожки";

    public ProjectState Apply(ProjectState project)
    {
        var tracks = project.Tracks;
        var clips = project.MediaClips;
        foreach (var (kind, index, template) in Items)
        {
            var track = tracks.FirstOrDefault(item => item.Kind == kind && item.Index == index);
            if (track is null)
            {
                track = new TimelineTrack(Guid.NewGuid(), kind, index, $"{(kind == TrackKind.Visual ? 'V' : 'A')}{index + 1}");
                tracks = tracks.Add(track);
            }
            if (!project.Sources.ContainsKey(template.SourceId))
                throw new EditRejectedException("Исходник клипа не найден в проекте.");
            clips = clips.Add(template with { TrackId = track.Id });
        }
        return project with { Tracks = tracks, MediaClips = clips };
    }
}

public sealed record DeleteMediaClipsCommand(IReadOnlySet<Guid> ClipIds, bool IncludeLinked = true) : IEditCommand
{
    public string Description => "Удалить клипы";

    public ProjectState Apply(ProjectState project)
    {
        var ids = ClipIds.ToHashSet();
        if (IncludeLinked)
        {
            var groups = project.MediaClips
                .Where(item => ids.Contains(item.Id) && item.LinkGroupId.HasValue)
                .Select(item => item.LinkGroupId!.Value)
                .ToHashSet();
            ids.UnionWith(project.MediaClips.Where(item => item.LinkGroupId is { } group && groups.Contains(group)).Select(item => item.Id));
        }
        return project with
        {
            MediaClips = project.MediaClips.Where(item => !ids.Contains(item.Id)).ToImmutableArray(),
            Transitions = RemoveTransitionsForClips(project.Transitions, ids)
        };
    }
}

public sealed record MoveMediaClipCommand(Guid ClipId, Guid TargetTrackId, TimelineTime NewStart) : IEditCommand
{
    public string Description => "Переместить клип";

    public ProjectState Apply(ProjectState project)
    {
        var selected = RequireClip(project, ClipId);
        var target = project.FindTrack(TargetTrackId)
            ?? throw new EditRejectedException("Целевая дорожка не найдена.");
        var sourceTrack = project.FindTrack(selected.TrackId)!;
        if (target.Kind != sourceTrack.Kind)
            throw new EditRejectedException("Клип нельзя переместить на дорожку другого типа.");
        if (NewStart < TimelineTime.Zero)
            throw new EditRejectedException("Позиция клипа не может быть отрицательной.");

        var delta = NewStart - selected.Start;
        var linkedIds = selected.LinkGroupId is { } group
            ? project.MediaClips.Where(item => item.LinkGroupId == group).Select(item => item.Id).ToHashSet()
            : new HashSet<Guid> { selected.Id };
        return project with
        {
            MediaClips = project.MediaClips.Select(item => item.Id == selected.Id
                ? item with { TrackId = TargetTrackId, Start = NewStart }
                : linkedIds.Contains(item.Id)
                    ? item with { Start = item.Start + delta }
                    : item).ToImmutableArray(),
            Transitions = RemoveTransitionsForClips(project.Transitions, linkedIds)
        };
    }
}

public enum TrimEdge
{
    Left,
    Right
}

public sealed record TrimMediaClipCommand(Guid ClipId, TrimEdge Edge, TimelineTime NewEdge) : IEditCommand
{
    public string Description => "Обрезать клип";

    public ProjectState Apply(ProjectState project)
    {
        var selected = RequireClip(project, ClipId);
        var source = project.Sources[selected.SourceId];
        var linked = selected.LinkGroupId is { } group
            ? project.MediaClips.Where(item => item.LinkGroupId == group).Select(item => item.Id).ToHashSet()
            : new HashSet<Guid> { selected.Id };
        TimelineTime startDelta;
        TimelineTime durationDelta;
        if (Edge == TrimEdge.Left)
        {
            startDelta = NewEdge - selected.Start;
            if (NewEdge < TimelineTime.Zero || NewEdge >= selected.End)
                throw new EditRejectedException("Левая граница обрезки находится вне клипа.");
            if (source.Kind != MediaKind.Image && selected.SourceIn + startDelta < TimelineTime.Zero)
                throw new EditRejectedException("Обрезка выходит за начало исходника.");
            durationDelta = -startDelta;
        }
        else
        {
            startDelta = TimelineTime.Zero;
            durationDelta = NewEdge - selected.End;
            if (NewEdge <= selected.Start)
                throw new EditRejectedException("Правая граница обрезки находится перед началом клипа.");
        }

        return project with
        {
            MediaClips = project.MediaClips.Select(item =>
            {
                if (!linked.Contains(item.Id)) return item;
                var itemSource = project.Sources[item.SourceId];
                var newDuration = item.Duration + durationDelta;
                if (newDuration <= TimelineTime.Zero)
                    throw new EditRejectedException("Длительность клипа должна быть положительной.");
                return item with
                {
                    Start = item.Start + startDelta,
                    SourceIn = itemSource.Kind == MediaKind.Image ? item.SourceIn : item.SourceIn + startDelta,
                    Duration = newDuration,
                    Audio = ClampAudioFades(item.Audio, newDuration)
                };
            }).ToImmutableArray(),
            Transitions = RemoveTransitionsForClips(project.Transitions, linked)
        };
    }
}

public sealed record SplitMediaClipsCommand(TimelineTime Position) : IEditCommand
{
    public string Description => "Разрезать активные клипы";

    public ProjectState Apply(ProjectState project)
    {
        var targets = project.MediaClips.Where(item => Position > item.Start && Position < item.End).ToArray();
        if (targets.Length == 0) return project;
        var rightGroups = targets.Where(item => item.LinkGroupId.HasValue)
            .Select(item => item.LinkGroupId!.Value)
            .Distinct()
            .ToDictionary(item => item, _ => Guid.NewGuid());
        var replacements = new Dictionary<Guid, MediaClip>();
        var additions = new List<MediaClip>();
        foreach (var clip in targets)
        {
            var source = project.Sources[clip.SourceId];
            var leftDuration = Position - clip.Start;
            var rightDuration = clip.Duration - leftDuration;
            replacements[clip.Id] = clip with
            {
                Duration = leftDuration,
                Audio = ClampAudioFades(clip.Audio, leftDuration)
            };
            additions.Add(clip with
            {
                Id = Guid.NewGuid(),
                LinkGroupId = clip.LinkGroupId is { } group ? rightGroups[group] : null,
                Start = Position,
                SourceIn = source.Kind == MediaKind.Image ? clip.SourceIn : clip.SourceIn + leftDuration,
                Duration = rightDuration,
                Audio = ClampAudioFades(clip.Audio, rightDuration)
            });
        }
        return project with
        {
            MediaClips = project.MediaClips.Select(item => replacements.GetValueOrDefault(item.Id, item))
                .Concat(additions).ToImmutableArray(),
            Transitions = RemoveTransitionsForClips(
                project.Transitions, targets.Select(item => item.Id).ToHashSet())
        };
    }
}

public sealed record SplitSelectedMediaClipCommand(Guid ClipId, TimelineTime Position, Guid? SelectedRightId = null) : IEditCommand
{
    public string Description => "Разрезать выбранный клип";

    public ProjectState Apply(ProjectState project)
    {
        var selected = RequireClip(project, ClipId);
        if (Position <= selected.Start || Position >= selected.End) return project;
        var targets = selected.LinkGroupId is { } group
            ? project.MediaClips.Where(item => item.LinkGroupId == group && Position > item.Start && Position < item.End).ToArray()
            : [selected];
        var rightGroup = targets.Length > 1 ? Guid.NewGuid() : (Guid?)null;
        var left = new Dictionary<Guid, MediaClip>();
        var right = new List<MediaClip>();
        foreach (var clip in targets)
        {
            var source = project.Sources[clip.SourceId];
            var leftDuration = Position - clip.Start;
            var rightDuration = clip.Duration - leftDuration;
            left[clip.Id] = clip with { Duration = leftDuration, Audio = ClampAudioFades(clip.Audio, leftDuration) };
            right.Add(clip with
            {
                Id = clip.Id == selected.Id && SelectedRightId.HasValue ? SelectedRightId.Value : Guid.NewGuid(),
                LinkGroupId = rightGroup, Start = Position,
                SourceIn = source.Kind == MediaKind.Image ? clip.SourceIn : clip.SourceIn + leftDuration,
                Duration = rightDuration, Audio = ClampAudioFades(clip.Audio, rightDuration)
            });
        }
        return project with
        {
            MediaClips = project.MediaClips.Select(item => left.GetValueOrDefault(item.Id, item))
                .Concat(right).ToImmutableArray(),
            Transitions = RemoveTransitionsForClips(
                project.Transitions, targets.Select(item => item.Id).ToHashSet())
        };
    }
}

public sealed record UnlinkMediaClipCommand(Guid ClipId) : IEditCommand
{
    public string Description => "Разорвать связь";
    public ProjectState Apply(ProjectState project)
    {
        var clip = RequireClip(project, ClipId);
        if (clip.LinkGroupId is not { } group) return project;
        return project with
        {
            MediaClips = project.MediaClips.Select(item => item.LinkGroupId == group
                ? item with { LinkGroupId = null }
                : item).ToImmutableArray()
        };
    }
}

public sealed record RippleDeleteRangeCommand(TimeRange Range) : IEditCommand
{
    public string Description => "Удалить диапазон со сдвигом";

    public ProjectState Apply(ProjectState project)
    {
        var rightLinkGroups = project.MediaClips
            .Where(item => item.LinkGroupId.HasValue && item.Start < Range.Start && item.End > Range.End)
            .Select(item => item.LinkGroupId!.Value)
            .Distinct()
            .ToDictionary(item => item, _ => Guid.NewGuid());
        var media = new List<MediaClip>(project.MediaClips.Length);
        foreach (var clip in project.MediaClips)
            TransformMediaClip(project, clip, Range, rightLinkGroups, media);

        var text = new List<TextClip>(project.TextClips.Length);
        foreach (var clip in project.TextClips)
            TransformTextClip(clip, Range, text);

        var markers = new List<TimelineMarker>(project.Markers.Length);
        foreach (var marker in project.Markers)
            TransformMarker(marker, Range, markers);

        return project with
        {
            MediaClips = media.ToImmutableArray(),
            TextClips = text.ToImmutableArray(),
            Markers = markers.ToImmutableArray(),
            Transitions = TransformTransitions(project.Transitions, Range, media),
            InPoint = TransformPoint(project.InPoint, Range),
            OutPoint = TransformPoint(project.OutPoint, Range)
        };
    }

    private static void TransformMediaClip(
        ProjectState project,
        MediaClip clip,
        TimeRange range,
        IReadOnlyDictionary<Guid, Guid> rightLinkGroups,
        ICollection<MediaClip> output)
    {
        if (clip.End <= range.Start)
        {
            output.Add(clip);
            return;
        }
        if (clip.Start >= range.End)
        {
            output.Add(clip with { Start = clip.Start - range.Duration });
            return;
        }
        if (clip.Start >= range.Start && clip.End <= range.End) return;

        var source = project.Sources[clip.SourceId];
        if (clip.Start < range.Start && clip.End > range.End)
        {
            var leftDuration = range.Start - clip.Start;
            var rightDuration = clip.End - range.End;
            output.Add(clip with
            {
                Duration = leftDuration,
                Audio = ClampAudioFades(clip.Audio, leftDuration)
            });
            output.Add(clip with
            {
                Id = Guid.NewGuid(),
                LinkGroupId = clip.LinkGroupId is { } group ? rightLinkGroups[group] : null,
                Start = range.Start,
                SourceIn = source.Kind == MediaKind.Image ? clip.SourceIn : clip.SourceIn + (range.End - clip.Start),
                Duration = rightDuration,
                Audio = ClampAudioFades(clip.Audio, rightDuration)
            });
            return;
        }
        if (clip.Start < range.Start)
        {
            var duration = range.Start - clip.Start;
            output.Add(clip with { Duration = duration, Audio = ClampAudioFades(clip.Audio, duration) });
            return;
        }

        var trimmed = range.End - clip.Start;
        var remaining = clip.End - range.End;
        output.Add(clip with
        {
            Start = range.Start,
            SourceIn = source.Kind == MediaKind.Image ? clip.SourceIn : clip.SourceIn + trimmed,
            Duration = remaining,
            Audio = ClampAudioFades(clip.Audio, remaining)
        });
    }

    private static void TransformTextClip(TextClip clip, TimeRange range, ICollection<TextClip> output)
    {
        if (clip.End <= range.Start)
        {
            output.Add(clip);
            return;
        }
        if (clip.Start >= range.End)
        {
            output.Add(clip with { Start = clip.Start - range.Duration });
            return;
        }
        if (clip.Start >= range.Start && clip.End <= range.End) return;
        if (clip.Start < range.Start && clip.End > range.End)
        {
            output.Add(clip with { Duration = clip.Duration - range.Duration });
            return;
        }
        if (clip.Start < range.Start)
        {
            output.Add(clip with { Duration = range.Start - clip.Start });
            return;
        }
        output.Add(clip with { Start = range.Start, Duration = clip.End - range.End });
    }

    private static void TransformMarker(TimelineMarker marker, TimeRange range, ICollection<TimelineMarker> output)
    {
        if (marker.End <= range.Start)
        {
            output.Add(marker);
            return;
        }
        if (marker.Start >= range.End)
        {
            output.Add(marker with { Start = marker.Start - range.Duration });
            return;
        }
        if (marker.Start >= range.Start && marker.End <= range.End) return;
        if (marker.Start < range.Start && marker.End > range.End)
        {
            output.Add(marker with { Duration = marker.Duration - range.Duration });
            return;
        }
        if (marker.Start < range.Start)
        {
            output.Add(marker with { Duration = range.Start - marker.Start });
            return;
        }
        output.Add(marker with
        {
            Start = range.Start,
            SourceStart = marker.SourceStart + (range.End - marker.Start),
            Duration = marker.End - range.End
        });
    }

    private static TimelineTime? TransformPoint(TimelineTime? point, TimeRange range)
    {
        if (point is null || point <= range.Start) return point;
        if (point >= range.End) return point - range.Duration;
        return range.Start;
    }
}

public sealed record LinkMediaClipsCommand(IReadOnlySet<Guid> ClipIds) : IEditCommand
{
    public string Description => "Связать клипы";
    public ProjectState Apply(ProjectState project)
    {
        var clips = project.MediaClips.Where(item => ClipIds.Contains(item.Id)).ToArray();
        if (clips.Length < 2 || clips.Length != ClipIds.Count)
            throw new EditRejectedException("Для связи требуется минимум два существующих клипа.");
        var first = clips[0];
        if (clips.Any(item => item.Start != first.Start || item.SourceIn != first.SourceIn || item.Duration != first.Duration))
            throw new EditRejectedException("Связываемые клипы должны иметь одинаковые временные диапазоны.");
        var kinds = clips.Select(item => project.FindTrack(item.TrackId)!.Kind).ToArray();
        if (kinds.Distinct().Count() != kinds.Length)
            throw new EditRejectedException("Нельзя связать два клипа одного типа.");
        var group = Guid.NewGuid();
        return project with
        {
            MediaClips = project.MediaClips.Select(item => ClipIds.Contains(item.Id)
                ? item with { LinkGroupId = group }
                : item).ToImmutableArray()
        };
    }
}

public sealed record UpsertTextClipCommand(TextClip Clip) : IEditCommand
{
    public string Description => "Изменить текст";
    public ProjectState Apply(ProjectState project)
    {
        var exists = project.TextClips.Any(item => item.Id == Clip.Id);
        return project with
        {
            TextClips = exists
                ? project.TextClips.Select(item => item.Id == Clip.Id ? Clip : item).ToImmutableArray()
                : project.TextClips.Add(Clip)
        };
    }
}

public sealed record DeleteTextClipsCommand(IReadOnlySet<Guid> ClipIds) : IEditCommand
{
    public string Description => "Удалить текст";
    public ProjectState Apply(ProjectState project)
        => project with { TextClips = project.TextClips.Where(item => !ClipIds.Contains(item.Id)).ToImmutableArray() };
}

public sealed record AddTextClipsCommand(IReadOnlyList<TextClip> Clips) : IEditCommand
{
    public string Description => "Add text clips";

    public ProjectState Apply(ProjectState project)
    {
        var duplicate = Clips.Select(item => item.Id)
            .Intersect(project.TextClips.Select(item => item.Id))
            .FirstOrDefault();
        if (duplicate != Guid.Empty)
            throw new EditRejectedException($"Text clip {duplicate} already exists.");
        return project with { TextClips = project.TextClips.AddRange(Clips) };
    }
}

public sealed record ReplaceMarkersCommand(IReadOnlyList<TimelineMarker> Markers) : IEditCommand
{
    public string Description => "Заменить метки";
    public ProjectState Apply(ProjectState project) => project with { Markers = Markers.ToImmutableArray() };
}

public sealed record UpsertTransitionCommand(TimelineTransition Transition) : IEditCommand
{
    public string Description => "Upsert transition";

    public ProjectState Apply(ProjectState project)
    {
        var normalized = NormalizeTransition(project, Transition);
        var exists = project.Transitions.Any(item => item.Id == Transition.Id);
        return project with
        {
            Transitions = exists
                ? project.Transitions.Select(item => item.Id == Transition.Id ? normalized : item).ToImmutableArray()
                : project.Transitions.Add(normalized)
        };
    }

    private static TimelineTransition NormalizeTransition(ProjectState project, TimelineTransition transition)
    {
        var from = project.FindMediaClip(transition.FromClipId)
            ?? throw new EditRejectedException("Transition outgoing clip was not found.");
        var to = project.FindMediaClip(transition.ToClipId)
            ?? throw new EditRejectedException("Transition incoming clip was not found.");
        var track = project.FindTrack(transition.TrackId)
            ?? throw new EditRejectedException("Transition track was not found.");
        if (from.TrackId != track.Id || to.TrackId != track.Id || from.End != to.Start)
            throw new EditRejectedException("Transition clips must be adjacent on the same track.");
        if (track.Kind == TrackKind.Audio && transition.Kind != TransitionKind.ConstantPowerAudio ||
            track.Kind == TrackKind.Visual && transition.Kind == TransitionKind.ConstantPowerAudio)
            throw new EditRejectedException("Transition kind does not match the track kind.");

        var cut = from.End;
        var requestedStart = transition.Start < cut ? transition.Start : cut;
        var requestedEnd = transition.End > cut ? transition.End : cut;
        var earliest = from.Start;
        var latest = to.End;
        if (project.Sources.TryGetValue(to.SourceId, out var toSource) && toSource.Kind != MediaKind.Image)
        {
            var byIncomingHandle = cut - to.SourceIn;
            if (byIncomingHandle > earliest) earliest = byIncomingHandle;
        }
        if (project.Sources.TryGetValue(from.SourceId, out var fromSource) && fromSource.Kind != MediaKind.Image)
        {
            var outgoingHandle = fromSource.Duration - from.SourceIn - from.Duration;
            var byOutgoingHandle = cut + outgoingHandle;
            if (byOutgoingHandle < latest) latest = byOutgoingHandle;
        }
        var start = requestedStart >= earliest ? requestedStart : earliest;
        var end = requestedEnd <= latest ? requestedEnd : latest;
        if (start >= cut || end <= cut || end <= start)
            throw new EditRejectedException("The source clips do not have enough media handles for this transition.");
        return transition with { Start = start, Duration = end - start };
    }
}

public sealed record DeleteTransitionsCommand(IReadOnlySet<Guid> TransitionIds) : IEditCommand
{
    public string Description => "Delete transitions";

    public ProjectState Apply(ProjectState project) => project with
    {
        Transitions = project.Transitions.Where(item => !TransitionIds.Contains(item.Id)).ToImmutableArray()
    };
}

public sealed record SetInOutCommand(TimelineTime? InPoint, TimelineTime? OutPoint) : IEditCommand
{
    public string Description => "Изменить точки входа и выхода";
    public ProjectState Apply(ProjectState project) => project with { InPoint = InPoint, OutPoint = OutPoint };
}

public sealed record RenameProjectCommand(string Name) : IEditCommand
{
    public string Description => "Переименовать проект";
    public ProjectState Apply(ProjectState project)
    {
        var normalized = Name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new EditRejectedException("Название проекта не может быть пустым.");
        return project with { Name = normalized };
    }
}

internal static class CommandHelpers
{
    public static MediaClip RequireClip(ProjectState project, Guid id)
        => project.FindMediaClip(id) ?? throw new EditRejectedException($"Клип {id} не найден.");

    public static AudioParameters? ClampAudioFades(AudioParameters? audio, TimelineTime duration)
        => audio is null ? null : audio with
        {
            FadeIn = audio.FadeIn > duration ? duration : audio.FadeIn,
            FadeOut = audio.FadeOut > duration ? duration : audio.FadeOut
        };

    public static ImmutableArray<TimelineTransition> RemoveTransitionsForClips(
        IEnumerable<TimelineTransition> transitions,
        IReadOnlySet<Guid> clipIds)
        => transitions.Where(item => !clipIds.Contains(item.FromClipId) && !clipIds.Contains(item.ToClipId))
            .ToImmutableArray();

    public static ImmutableArray<TimelineTransition> TransformTransitions(
        IEnumerable<TimelineTransition> transitions,
        TimeRange removedRange,
        IReadOnlyCollection<MediaClip> media)
    {
        var clips = media.ToDictionary(item => item.Id);
        var result = ImmutableArray.CreateBuilder<TimelineTransition>();
        foreach (var transition in transitions)
        {
            if (!clips.TryGetValue(transition.FromClipId, out var from) ||
                !clips.TryGetValue(transition.ToClipId, out var to)) continue;
            TimelineTransition? transformed = transition.End <= removedRange.Start
                ? transition
                : transition.Start >= removedRange.End
                    ? transition with { Start = transition.Start - removedRange.Duration }
                    : null;
            if (transformed is null || from.TrackId != to.TrackId || from.End != to.Start ||
                transformed.Duration > from.Duration || transformed.Duration > to.Duration ||
                transformed.Start < from.Start || transformed.End > to.End ||
                !transformed.Range.Contains(from.End)) continue;
            result.Add(transformed);
        }
        return result.ToImmutable();
    }
}
