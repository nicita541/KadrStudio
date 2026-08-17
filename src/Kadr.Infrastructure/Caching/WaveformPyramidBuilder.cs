using System.Collections.Immutable;
using KadrStudio.Application.Caching;

namespace KadrStudio.Infrastructure.Caching;

public sealed class WaveformPyramidBuilder(int sampleRate = 48_000, int framesPerPeak = 256)
{
    private readonly List<WaveformPeak> _basePeaks = [];
    private int _frameCount;
    private float _minL;
    private float _maxL;
    private float _minR;
    private float _maxR;
    private double _squaresL;
    private double _squaresR;
    private long _sourceFrames;

    public void AddInterleavedStereo(ReadOnlySpan<float> samples)
    {
        if (samples.Length % 2 != 0) throw new ArgumentException("Stereo samples must be interleaved pairs.", nameof(samples));
        for (var index = 0; index < samples.Length; index += 2)
        {
            var left = Math.Clamp(samples[index], -1, 1);
            var right = Math.Clamp(samples[index + 1], -1, 1);
            _minL = Math.Min(_minL, left); _maxL = Math.Max(_maxL, left);
            _minR = Math.Min(_minR, right); _maxR = Math.Max(_maxR, right);
            _squaresL += left * left; _squaresR += right * right;
            _frameCount++; _sourceFrames++;
            if (_frameCount == framesPerPeak) FlushPeak();
        }
    }

    public WaveformPyramid Build()
    {
        if (_frameCount > 0) FlushPeak();
        if (_basePeaks.Count == 0) return WaveformPyramid.Empty;
        var levels = ImmutableArray.CreateBuilder<WaveformLevel>();
        var current = _basePeaks.ToImmutableArray();
        var scale = framesPerPeak;
        levels.Add(new WaveformLevel(scale, current));
        while (current.Length > 1)
        {
            // FramesPerPeak is intentionally an int in the cache contract. For
            // extremely long recordings, keep the last safe level instead of
            // overflowing while creating a single all-source peak.
            if (scale > int.MaxValue / 4) break;
            var next = ImmutableArray.CreateBuilder<WaveformPeak>((current.Length + 3) / 4);
            for (var index = 0; index < current.Length; index += 4)
                next.Add(WaveformPyramid.Aggregate(current.AsSpan().Slice(index, Math.Min(4, current.Length - index))));
            current = next.MoveToImmutable();
            scale = checked(scale * 4);
            levels.Add(new WaveformLevel(scale, current));
        }
        return new WaveformPyramid(sampleRate, 2, _sourceFrames, levels.ToImmutable());
    }

    private void FlushPeak()
    {
        _basePeaks.Add(new WaveformPeak(_minL, _maxL, (float)Math.Sqrt(_squaresL / _frameCount),
            _minR, _maxR, (float)Math.Sqrt(_squaresR / _frameCount)));
        _frameCount = 0; _minL = 0; _maxL = 0; _minR = 0; _maxR = 0; _squaresL = 0; _squaresR = 0;
    }
}
