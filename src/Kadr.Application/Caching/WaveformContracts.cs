using System.Collections.Immutable;

namespace KadrStudio.Application.Caching;

public readonly record struct WaveformPeak(
    float MinimumLeft,
    float MaximumLeft,
    float RmsLeft,
    float MinimumRight,
    float MaximumRight,
    float RmsRight)
{
    public static WaveformPeak Silence => default;
}

public sealed record WaveformLevel(int FramesPerPeak, ImmutableArray<WaveformPeak> Peaks)
{
    public int Count => Peaks.Length;
}

public sealed record WaveformPyramid(
    int SampleRate,
    int Channels,
    long SourceFrameCount,
    ImmutableArray<WaveformLevel> Levels)
{
    public static WaveformPyramid Empty { get; } = new(48_000, 2, 0, []);
    public bool IsEmpty => Levels.IsDefaultOrEmpty || Levels[0].Peaks.IsDefaultOrEmpty;

    public WaveformLevel SelectLevel(double sourceStartRatio, double sourceEndRatio, int columnCount)
    {
        if (IsEmpty) throw new InvalidOperationException("The waveform pyramid is empty.");
        var visibleFrames = Math.Max(1,
            (long)Math.Ceiling(SourceFrameCount * Math.Max(0, sourceEndRatio - sourceStartRatio)));
        var targetFrames = Math.Max(1d, visibleFrames / (double)Math.Max(1, columnCount));
        return Levels
            .Where(level => level.FramesPerPeak <= targetFrames)
            .DefaultIfEmpty(Levels[0])
            .MaxBy(level => level.FramesPerPeak)!;
    }

    public ImmutableArray<WaveformPeak> ReadColumns(
        double sourceStartRatio,
        double sourceEndRatio,
        int columnCount)
    {
        if (IsEmpty || columnCount <= 0) return [];
        sourceStartRatio = Math.Clamp(sourceStartRatio, 0, 1);
        sourceEndRatio = Math.Clamp(sourceEndRatio, sourceStartRatio, 1);
        var level = SelectLevel(sourceStartRatio, sourceEndRatio, columnCount);
        var result = ImmutableArray.CreateBuilder<WaveformPeak>(columnCount);
        for (var column = 0; column < columnCount; column++)
        {
            var fromRatio = sourceStartRatio + (sourceEndRatio - sourceStartRatio) * column / columnCount;
            var toRatio = sourceStartRatio + (sourceEndRatio - sourceStartRatio) * (column + 1) / columnCount;
            var from = Math.Clamp((int)Math.Floor(fromRatio * level.Count), 0, level.Count - 1);
            var to = Math.Clamp((int)Math.Ceiling(toRatio * level.Count), from + 1, level.Count);
            result.Add(Aggregate(level.Peaks.AsSpan(from, to - from)));
        }
        return result.MoveToImmutable();
    }

    public static WaveformPeak Aggregate(ReadOnlySpan<WaveformPeak> peaks)
    {
        if (peaks.IsEmpty) return WaveformPeak.Silence;
        var minL = 0f; var maxL = 0f; var minR = 0f; var maxR = 0f;
        double rmsL = 0; double rmsR = 0;
        foreach (var peak in peaks)
        {
            minL = Math.Min(minL, peak.MinimumLeft); maxL = Math.Max(maxL, peak.MaximumLeft);
            minR = Math.Min(minR, peak.MinimumRight); maxR = Math.Max(maxR, peak.MaximumRight);
            rmsL += peak.RmsLeft * peak.RmsLeft;
            rmsR += peak.RmsRight * peak.RmsRight;
        }
        return new WaveformPeak(minL, maxL, (float)Math.Sqrt(rmsL / peaks.Length),
            minR, maxR, (float)Math.Sqrt(rmsR / peaks.Length));
    }
}

public interface IWaveformExtractor
{
    Task<WaveformPyramid> ExtractAsync(string path, CancellationToken cancellationToken = default);
}
