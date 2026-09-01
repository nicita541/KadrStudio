using KadrStudio.Core.Domain;

namespace KadrStudio.Controls;

public static class TimelineFrameNavigator
{
    public static TimelineTime Step(
        TimelineTime current,
        int direction,
        FrameRate frameRate,
        TimelineTime maximum)
    {
        if (direction == 0) return Clamp(current, maximum);
        var framePosition = current.Ticks * (decimal)frameRate.Numerator /
                            (TimelineTime.TicksPerSecond * (decimal)frameRate.Denominator);
        var targetFrame = direction > 0
            ? (long)decimal.Floor(framePosition) + 1
            : (long)decimal.Ceiling(framePosition) - 1;
        var stepped = TimelineTime.FromFrames(Math.Max(0, targetFrame), frameRate);
        return Clamp(stepped, maximum);
    }

    private static TimelineTime Clamp(TimelineTime value, TimelineTime maximum)
        => value < TimelineTime.Zero
            ? TimelineTime.Zero
            : value > maximum
                ? maximum
                : value;
}
