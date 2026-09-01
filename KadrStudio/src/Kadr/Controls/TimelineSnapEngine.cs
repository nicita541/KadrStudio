namespace KadrStudio.Controls;

public enum TimelineSnapTargetKind
{
    ClipEdge,
    Playhead,
    Marker,
    TimelineStart
}

public readonly record struct TimelineSnapTarget(
    double Time,
    TimelineSnapTargetKind Kind,
    Guid? ItemId = null);

public readonly record struct TimelineSnapResult(
    double Value,
    bool IsSnapped,
    TimelineSnapTarget? Target = null);

/// <summary>
/// Pure timeline snapping math. Pixel thresholds are converted at the call site
/// scale so the interaction feels identical at every zoom level.
/// </summary>
public static class TimelineSnapEngine
{
    public const double DefaultThresholdPixels = 8;

    public static TimelineSnapResult SnapTime(
        double proposedTime,
        double frameRate,
        double pixelsPerSecond,
        bool snappingEnabled,
        IEnumerable<TimelineSnapTarget> targets,
        double thresholdPixels = DefaultThresholdPixels)
    {
        var bounded = Math.Max(0, proposedTime);
        if (snappingEnabled && TryFindCorrection(
                [bounded],
                targets,
                pixelsPerSecond,
                thresholdPixels,
                out var correction,
                out var target))
        {
            return new TimelineSnapResult(Math.Max(0, bounded + correction), true, target);
        }

        var safeFrameRate = Math.Max(1, frameRate);
        return new TimelineSnapResult(
            Math.Round(bounded * safeFrameRate) / safeFrameRate,
            false);
    }

    public static TimelineSnapResult SnapDelta(
        double proposedDelta,
        double frameAlignedDelta,
        IReadOnlyList<double> movingAnchors,
        IEnumerable<TimelineSnapTarget> targets,
        double pixelsPerSecond,
        bool snappingEnabled,
        double thresholdPixels = DefaultThresholdPixels)
    {
        if (snappingEnabled && TryFindCorrection(
                movingAnchors.Select(anchor => anchor + proposedDelta),
                targets,
                pixelsPerSecond,
                thresholdPixels,
                out var correction,
                out var target))
        {
            return new TimelineSnapResult(proposedDelta + correction, true, target);
        }

        return new TimelineSnapResult(frameAlignedDelta, false);
    }

    private static bool TryFindCorrection(
        IEnumerable<double> proposedAnchors,
        IEnumerable<TimelineSnapTarget> targets,
        double pixelsPerSecond,
        double thresholdPixels,
        out double correction,
        out TimelineSnapTarget target)
    {
        var thresholdSeconds = Math.Max(0, thresholdPixels) / Math.Max(0.0001, pixelsPerSecond);
        var bestDistance = double.PositiveInfinity;
        var bestPriority = int.MaxValue;
        correction = 0;
        target = default;

        foreach (var anchor in proposedAnchors)
        {
            foreach (var candidate in targets)
            {
                var delta = candidate.Time - anchor;
                var distance = Math.Abs(delta);
                var priority = Priority(candidate.Kind);
                if (distance > thresholdSeconds + 0.0000001 ||
                    distance > bestDistance + 0.0000001 ||
                    (Math.Abs(distance - bestDistance) <= 0.0000001 && priority >= bestPriority))
                {
                    continue;
                }

                bestDistance = distance;
                bestPriority = priority;
                correction = delta;
                target = candidate;
            }
        }

        return !double.IsPositiveInfinity(bestDistance);
    }

    private static int Priority(TimelineSnapTargetKind kind) => kind switch
    {
        TimelineSnapTargetKind.ClipEdge => 0,
        TimelineSnapTargetKind.Playhead => 1,
        TimelineSnapTargetKind.Marker => 2,
        TimelineSnapTargetKind.TimelineStart => 3,
        _ => 4
    };
}
