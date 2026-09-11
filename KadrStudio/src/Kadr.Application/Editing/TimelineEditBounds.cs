using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

public static class TimelineEditBounds
{
    public static bool CanSplit(double start, double end, double position, FrameRate frameRate)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(position)) return false;
        var minimum = TimelineTime.FromFrames(1, frameRate);
        var split = TimelineTime.FromSeconds(position);
        return split - TimelineTime.FromSeconds(start) >= minimum &&
               TimelineTime.FromSeconds(end) - split >= minimum;
    }
}
