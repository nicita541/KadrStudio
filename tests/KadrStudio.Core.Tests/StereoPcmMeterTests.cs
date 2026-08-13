using KadrStudio.Application.Preview;

namespace KadrStudio.Core.Tests;

public sealed class StereoPcmMeterTests
{
    [Fact]
    public void Silence_is_exactly_zero_and_negative_infinity_db()
    {
        var level = new StereoPcmMeter().Measure(new float[32]);
        Assert.Equal(0, level.LeftPeak);
        Assert.Equal(0, level.RightPeak);
        Assert.True(float.IsNegativeInfinity(level.LeftPeakDb));
        Assert.True(float.IsNegativeInfinity(level.RightPeakDb));
    }

    [Fact]
    public void Stereo_channels_are_measured_independently()
    {
        float[] pcm = [1f, 0.25f, -0.5f, -0.75f];
        var level = new StereoPcmMeter().Measure(pcm);
        Assert.Equal(1f, level.LeftPeak);
        Assert.Equal(0.75f, level.RightPeak);
        Assert.Equal(MathF.Sqrt((1f + 0.25f) / 2), level.LeftRms, 5);
        Assert.Equal(MathF.Sqrt((0.0625f + 0.5625f) / 2), level.RightRms, 5);
    }

    [Fact]
    public void Mono_is_mirrored_to_both_channels()
    {
        var level = new StereoPcmMeter().Measure([0.2f, -0.8f], channels: 1);
        Assert.Equal(0.8f, level.LeftPeak);
        Assert.Equal(level.LeftPeak, level.RightPeak);
    }
}
