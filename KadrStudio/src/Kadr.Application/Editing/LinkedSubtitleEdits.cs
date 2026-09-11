using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

internal static class LinkedSubtitleEdits
{
    public static ImmutableArray<SubtitleClip> Move(ProjectState project, Guid? group, TimelineTime delta)
        => group is null ? project.SubtitleClips : project.SubtitleClips.Select(clip => clip.LinkGroupId == group
            ? clip with { Start = clip.Start + delta } : clip).ToImmutableArray();

    public static ImmutableArray<SubtitleClip> Trim(ProjectState project, Guid? group,
        TimelineTime startDelta, TimelineTime durationDelta)
        => group is null ? project.SubtitleClips : project.SubtitleClips.Select(clip => clip.LinkGroupId == group
            ? clip with { Start = clip.Start + startDelta, SourceIn = clip.SourceIn + startDelta,
                Duration = clip.Duration + durationDelta } : clip).ToImmutableArray();

    public static ImmutableArray<SubtitleClip> Unlink(ProjectState project, Guid? group)
        => group is null ? project.SubtitleClips : project.SubtitleClips.Select(clip => clip.LinkGroupId == group
            ? clip with { LinkGroupId = null } : clip).ToImmutableArray();

    public static ImmutableArray<SubtitleClip> Split(ProjectState project, TimelineTime position,
        IReadOnlyDictionary<Guid, Guid> rightGroups)
    {
        var result = ImmutableArray.CreateBuilder<SubtitleClip>();
        foreach (var clip in project.SubtitleClips)
        {
            if (clip.LinkGroupId is not { } group || !rightGroups.TryGetValue(group, out var rightGroup) || clip.End <= position)
            {
                result.Add(clip);
                continue;
            }
            if (clip.Start >= position)
            {
                result.Add(clip with { LinkGroupId = rightGroup });
                continue;
            }
            var leftDuration = position - clip.Start;
            result.Add(clip with { Duration = leftDuration });
            result.Add(clip with { Id = Guid.NewGuid(), Start = position, SourceIn = clip.SourceIn + leftDuration,
                Duration = clip.Duration - leftDuration, LinkGroupId = rightGroup });
        }
        return result.ToImmutable();
    }
}
