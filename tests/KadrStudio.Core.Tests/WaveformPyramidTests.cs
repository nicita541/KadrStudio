using KadrStudio.Infrastructure.Caching;

namespace KadrStudio.Core.Tests;

public sealed class WaveformPyramidTests
{
    [Fact]
    public void Preserves_silence_impulses_and_stereo_channels()
    {
        var samples = new float[4096 * 2];
        samples[512 * 2] = 1;
        samples[2048 * 2 + 1] = -0.75f;
        var builder = new WaveformPyramidBuilder(48_000, 64);
        builder.AddInterleavedStereo(samples);
        var pyramid = builder.Build();

        Assert.Equal(0, pyramid.Levels[0].Peaks[0].MaximumLeft);
        Assert.Contains(pyramid.Levels[0].Peaks, peak => peak.MaximumLeft == 1);
        Assert.Contains(pyramid.Levels[0].Peaks, peak => peak.MinimumRight == -0.75f);
        Assert.Contains(pyramid.Levels[0].Peaks, peak => peak == default);
    }

    [Fact]
    public void Codec_round_trips_all_levels_and_rejects_corruption()
    {
        var builder = new WaveformPyramidBuilder(48_000, 8);
        builder.AddInterleavedStereo(Enumerable.Range(0, 1024)
            .SelectMany(index => new[] { MathF.Sin(index / 10f), MathF.Cos(index / 7f) }).ToArray());
        var source = builder.Build();
        var encoded = WaveformPyramidCodec.Encode(source);
        var decoded = WaveformPyramidCodec.Decode(encoded);

        Assert.Equal(source.SampleRate, decoded.SampleRate);
        Assert.Equal(source.Channels, decoded.Channels);
        Assert.Equal(source.SourceFrameCount, decoded.SourceFrameCount);
        Assert.Equal(source.Levels.Length, decoded.Levels.Length);
        for (var index = 0; index < source.Levels.Length; index++)
        {
            Assert.Equal(source.Levels[index].FramesPerPeak, decoded.Levels[index].FramesPerPeak);
            Assert.True(source.Levels[index].Peaks.SequenceEqual(decoded.Levels[index].Peaks));
        }
        encoded[0] ^= 0x7f;
        Assert.Throws<InvalidDataException>(() => WaveformPyramidCodec.Decode(encoded));
        var truncated = encoded[..^17];
        Assert.Throws<InvalidDataException>(() => WaveformPyramidCodec.Decode(truncated));
    }

    [Fact]
    public void Zoom_selects_finer_data_but_keeps_requested_pixel_density()
    {
        var builder = new WaveformPyramidBuilder(48_000, 4);
        builder.AddInterleavedStereo(Enumerable.Range(0, 16_384)
            .SelectMany(index => new[] { index % 127 / 127f, index % 89 / 89f }).ToArray());
        var pyramid = builder.Build();

        var fullLevel = pyramid.SelectLevel(0, 1, 800);
        var closeLevel = pyramid.SelectLevel(0.4, 0.5, 800);
        Assert.True(closeLevel.FramesPerPeak < fullLevel.FramesPerPeak);
        Assert.Equal(800, pyramid.ReadColumns(0, 1, 800).Length);
        Assert.Equal(800, pyramid.ReadColumns(0.4, 0.5, 800).Length);
    }
}
