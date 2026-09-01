using System.Collections.Immutable;
using KadrStudio.Core.Domain;
using static KadrStudio.Application.Editing.CommandHelpers;

namespace KadrStudio.Application.Editing;

public static class TimelineRangeTransformer
{
    public static ProjectState RippleDelete(ProjectState project, TimeRange range)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (range.Start < TimelineTime.Zero || range.Duration <= TimelineTime.Zero)
            throw new EditRejectedException("Ripple-delete range must be positive.");
        var rightLinkGroups = project.MediaClips
            .Where(item => item.LinkGroupId.HasValue && item.Start < range.Start && item.End > range.End)
            .Select(item => item.LinkGroupId!.Value)
            .Distinct()
            .ToDictionary(item => item, _ => Guid.NewGuid());
        foreach (var group in project.SubtitleClips
                     .Where(item => item.LinkGroupId.HasValue && item.Start < range.Start && item.End > range.End)
                     .Select(item => item.LinkGroupId!.Value)
                     .Distinct())
            rightLinkGroups.TryAdd(group, Guid.NewGuid());
        var media = new List<MediaClip>(project.MediaClips.Length);
        var transitionStartClipIds = new Dictionary<Guid, Guid>();
        var transitionEndClipIds = new Dictionary<Guid, Guid>();
        foreach (var clip in project.MediaClips)
        {
            var firstOutput = media.Count;
            TransformMediaClip(project, clip, range, rightLinkGroups, media);
            if (media.Count > firstOutput)
            {
                transitionStartClipIds[clip.Id] = media[firstOutput].Id;
                transitionEndClipIds[clip.Id] = media[^1].Id;
            }
        }
        var subtitles = new List<SubtitleClip>(project.SubtitleClips.Length);
        foreach (var clip in project.SubtitleClips)
            TransformSubtitleClip(clip, range, rightLinkGroups, subtitles);
        var text = new List<TextClip>(project.TextClips.Length);
        foreach (var clip in project.TextClips)
            TransformTextClip(clip, range, text);
        var markers = new List<TimelineMarker>(project.Markers.Length);
        foreach (var marker in project.Markers)
            TransformMarker(marker, range, markers);

        return project with
        {
            MediaClips = media.ToImmutableArray(),
            SubtitleClips = subtitles.ToImmutableArray(),
            TextClips = text.ToImmutableArray(),
            Markers = markers.ToImmutableArray(),
            Transitions = TransformTransitions(
                project.Transitions, range, media,
                transitionStartClipIds, transitionEndClipIds),
            InPoint = TransformPoint(project.InPoint, range),
            OutPoint = TransformPoint(project.OutPoint, range)
        };
    }

    private static void TransformMediaClip(
        ProjectState project,
        MediaClip clip,
        TimeRange range,
        IReadOnlyDictionary<Guid, Guid> rightLinkGroups,
        ICollection<MediaClip> output)
    {
        if (clip.End <= range.Start) { output.Add(clip); return; }
        if (clip.Start >= range.End) { output.Add(clip with { Start = clip.Start - range.Duration }); return; }
        if (clip.Start >= range.Start && clip.End <= range.End) return;
        var source = project.Sources[clip.SourceId];
        if (clip.Start < range.Start && clip.End > range.End)
        {
            var leftDuration = range.Start - clip.Start;
            var rightDuration = clip.End - range.End;
            output.Add(clip with { Duration = leftDuration, Audio = ClampAudioFades(clip.Audio, leftDuration) });
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

    private static void TransformSubtitleClip(
        SubtitleClip clip,
        TimeRange range,
        IReadOnlyDictionary<Guid, Guid> rightLinkGroups,
        ICollection<SubtitleClip> output)
    {
        if (clip.End <= range.Start) { output.Add(clip); return; }
        if (clip.Start >= range.End) { output.Add(clip with { Start = clip.Start - range.Duration }); return; }
        if (clip.Start >= range.Start && clip.End <= range.End) return;
        if (clip.Start < range.Start && clip.End > range.End)
        {
            var leftDuration = range.Start - clip.Start;
            output.Add(clip with { Duration = leftDuration });
            output.Add(clip with
            {
                Id = Guid.NewGuid(),
                LinkGroupId = clip.LinkGroupId is { } group ? rightLinkGroups[group] : null,
                Start = range.Start,
                SourceIn = clip.SourceIn + (range.End - clip.Start),
                Duration = clip.End - range.End
            });
            return;
        }
        if (clip.Start < range.Start)
        {
            output.Add(clip with { Duration = range.Start - clip.Start });
            return;
        }
        output.Add(clip with
        {
            Start = range.Start,
            SourceIn = clip.SourceIn + (range.End - clip.Start),
            Duration = clip.End - range.End
        });
    }

    private static void TransformTextClip(TextClip clip, TimeRange range, ICollection<TextClip> output)
    {
        if (clip.End <= range.Start) { output.Add(clip); return; }
        if (clip.Start >= range.End) { output.Add(clip with { Start = clip.Start - range.Duration }); return; }
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
        if (marker.End <= range.Start) { output.Add(marker); return; }
        if (marker.Start >= range.End) { output.Add(marker with { Start = marker.Start - range.Duration }); return; }
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
