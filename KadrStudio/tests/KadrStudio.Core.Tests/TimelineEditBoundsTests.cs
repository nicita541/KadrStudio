using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class TimelineEditBoundsTests
{
    [Theory]
    [InlineData(24000)]
    [InlineData(30000)]
    [InlineData(60000)]
    public void Two_frame_clip_can_split_at_one_frame_but_not_inside_a_frame(int numerator)
    {
        var fps = new FrameRate(numerator, 1001);
        var frame = TimelineTime.FromFrames(1, fps).TotalSeconds;
        Assert.True(TimelineEditBounds.CanSplit(0, frame * 2, frame, fps));
        Assert.False(TimelineEditBounds.CanSplit(0, frame * 2, frame / 2, fps));
        Assert.False(TimelineEditBounds.CanSplit(0, frame * 2, double.NaN, fps));
    }
}
