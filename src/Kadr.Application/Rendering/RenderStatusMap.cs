using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Rendering;

public enum RenderRangeState
{
    Invalid,
    Rendering,
    Ready,
    Failed
}

public enum RenderPipeline
{
    Video,
    Audio,
    Overlay
}

public sealed record RenderStatusRange(TimeRange Range, RenderRangeState State);

/// <summary>
/// Immutable range status for partial render reuse. Every operation returns a
/// new map; playback and export can observe snapshots without shared locks.
/// </summary>
public sealed record RenderStatusMap
{
    public ImmutableArray<RenderStatusRange> Video { get; init; } = [];
    public ImmutableArray<RenderStatusRange> Audio { get; init; } = [];
    public ImmutableArray<RenderStatusRange> Overlay { get; init; } = [];

    public static RenderStatusMap Create(TimeRange projectRange, RenderRangeState initial = RenderRangeState.Invalid)
    {
        var status = ImmutableArray.Create(new RenderStatusRange(projectRange, initial));
        return new RenderStatusMap { Video = status, Audio = status, Overlay = status };
    }

    public RenderStatusMap Apply(ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return this with
        {
            Video = changes.InvalidatesVideo ? SetRanges(Video, changes.VideoRanges, RenderRangeState.Invalid) : Video,
            Audio = changes.InvalidatesAudio ? SetRanges(Audio, changes.AudioRanges, RenderRangeState.Invalid) : Audio,
            Overlay = changes.InvalidatesOverlay ? SetRanges(Overlay, changes.OverlayRanges, RenderRangeState.Invalid) : Overlay
        };
    }

    public RenderStatusMap Set(RenderPipeline pipeline, TimeRange range, RenderRangeState state)
        => pipeline switch
        {
            RenderPipeline.Video => this with { Video = SetRanges(Video, [range], state) },
            RenderPipeline.Audio => this with { Audio = SetRanges(Audio, [range], state) },
            RenderPipeline.Overlay => this with { Overlay = SetRanges(Overlay, [range], state) },
            _ => throw new ArgumentOutOfRangeException(nameof(pipeline))
        };

    public RenderRangeState Get(RenderPipeline pipeline, TimelineTime time)
    {
        var ranges = pipeline switch
        {
            RenderPipeline.Video => Video,
            RenderPipeline.Audio => Audio,
            RenderPipeline.Overlay => Overlay,
            _ => throw new ArgumentOutOfRangeException(nameof(pipeline))
        };
        return ranges.FirstOrDefault(item => item.Range.Contains(time))?.State ?? RenderRangeState.Invalid;
    }

    private static ImmutableArray<RenderStatusRange> SetRanges(
        ImmutableArray<RenderStatusRange> source,
        IEnumerable<TimeRange> ranges,
        RenderRangeState state)
    {
        var result = source;
        foreach (var range in ranges) result = SetOne(result, range, state);
        return Merge(result);
    }

    private static ImmutableArray<RenderStatusRange> SetOne(
        ImmutableArray<RenderStatusRange> source,
        TimeRange target,
        RenderRangeState state)
    {
        var result = ImmutableArray.CreateBuilder<RenderStatusRange>();
        foreach (var item in source)
        {
            if (!item.Range.Overlaps(target))
            {
                result.Add(item);
                continue;
            }
            if (item.Range.Start < target.Start)
                result.Add(new RenderStatusRange(
                    new TimeRange(item.Range.Start, target.Start - item.Range.Start), item.State));
            var overlapStart = item.Range.Start >= target.Start ? item.Range.Start : target.Start;
            var overlapEnd = item.Range.End <= target.End ? item.Range.End : target.End;
            result.Add(new RenderStatusRange(new TimeRange(overlapStart, overlapEnd - overlapStart), state));
            if (item.Range.End > target.End)
                result.Add(new RenderStatusRange(
                    new TimeRange(target.End, item.Range.End - target.End), item.State));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<RenderStatusRange> Merge(IEnumerable<RenderStatusRange> source)
    {
        var ordered = source.OrderBy(item => item.Range.Start).ToArray();
        if (ordered.Length == 0) return [];
        var result = ImmutableArray.CreateBuilder<RenderStatusRange>();
        var current = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (current.State == next.State && current.Range.End == next.Range.Start)
            {
                current = current with
                {
                    Range = new TimeRange(current.Range.Start, next.Range.End - current.Range.Start)
                };
                continue;
            }
            result.Add(current);
            current = next;
        }
        result.Add(current);
        return result.ToImmutable();
    }
}
