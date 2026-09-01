using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class TimelineTimeTests
{
    public static IEnumerable<object[]> StandardFrameRates()
    {
        yield return [FrameRate.Fps23976];
        yield return [FrameRate.Fps24];
        yield return [FrameRate.Fps25];
        yield return [FrameRate.Fps2997];
        yield return [FrameRate.Fps30];
        yield return [FrameRate.Fps50];
        yield return [FrameRate.Fps5994];
        yield return [FrameRate.Fps60];
    }

    [Theory]
    [MemberData(nameof(StandardFrameRates))]
    public void Frame_conversion_has_no_accumulating_drift(FrameRate frameRate)
    {
        const long fourHoursAt60Fps = 4 * 60 * 60 * 60;
        var time = TimelineTime.FromFrames(fourHoursAt60Fps, frameRate);

        Assert.Equal(fourHoursAt60Fps, time.ToNearestFrame(frameRate));
        Assert.Equal(time, time.SnapToFrame(frameRate));
    }

    [Fact]
    public void Time_range_uses_half_open_boundaries()
    {
        var range = new TimeRange(TimelineTime.FromSeconds(10), TimelineTime.FromSeconds(5));

        Assert.True(range.Contains(TimelineTime.FromSeconds(10)));
        Assert.True(range.Contains(TimelineTime.FromSeconds(14.999)));
        Assert.False(range.Contains(TimelineTime.FromSeconds(15)));
    }
}
