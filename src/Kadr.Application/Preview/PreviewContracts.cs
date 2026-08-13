using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Preview;

public enum PreviewState
{
    Idle,
    Preparing,
    Buffering,
    Playing,
    Paused,
    Failed
}

public readonly record struct PreviewGeneration(long Video, long Audio, long Overlay);

public readonly record struct PreviewRequest(
    TimelineTime Position,
    FrameRate FrameRate,
    int Width,
    int Height,
    bool UseProxy,
    PreviewGeneration Generation);

public sealed record VideoFrame(
    TimelineTime Position,
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> Bgra,
    long Generation);

public sealed record AudioBlock(
    TimelineTime Position,
    int SampleRate,
    int Channels,
    ReadOnlyMemory<float> InterleavedSamples,
    long Generation);

public readonly record struct AudioMeterLevel(
    float LeftPeak,
    float RightPeak,
    float LeftRms,
    float RightRms,
    float LeftPeakDb,
    float RightPeakDb);

/// <summary>Calculates channel meters from the exact interleaved PCM sent to the audio device.</summary>
public sealed class StereoPcmMeter
{
    public AudioMeterLevel Measure(ReadOnlySpan<float> interleaved, int channels = 2)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (interleaved.IsEmpty) return default;
        double leftSquares = 0;
        double rightSquares = 0;
        var leftPeak = 0f;
        var rightPeak = 0f;
        var frames = interleaved.Length / channels;
        if (frames == 0) return default;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * channels;
            var left = Math.Clamp(interleaved[offset], -1f, 1f);
            var right = channels == 1 ? left : Math.Clamp(interleaved[offset + 1], -1f, 1f);
            leftPeak = Math.Max(leftPeak, Math.Abs(left));
            rightPeak = Math.Max(rightPeak, Math.Abs(right));
            leftSquares += left * left;
            rightSquares += right * right;
        }
        return new AudioMeterLevel(
            leftPeak, rightPeak,
            (float)Math.Sqrt(leftSquares / frames),
            (float)Math.Sqrt(rightSquares / frames),
            ToDb(leftPeak), ToDb(rightPeak));
    }

    private static float ToDb(float amplitude)
        => amplitude <= 0 ? float.NegativeInfinity : 20f * MathF.Log10(amplitude);
}

public readonly record struct PreviewArtifactKey(
    Guid ProjectId,
    string ContentSignature,
    TimelineTime RangeStart,
    TimelineTime RangeDuration,
    int Width,
    int Height,
    FrameRate FrameRate,
    bool IsProxy)
{
    public string StableHash
    {
        get
        {
            var value = string.Join('|',
                ProjectId.ToString("N"), ContentSignature, RangeStart.Ticks, RangeDuration.Ticks,
                Width, Height, FrameRate.Numerator, FrameRate.Denominator, IsProxy);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)));
        }
    }
}

public interface IPreviewEngine : IAsyncDisposable
{
    PreviewState State { get; }
    TimelineTime Position { get; }
    event EventHandler<PreviewState>? StateChanged;
    event EventHandler<VideoFrame>? FramePresented;
    event EventHandler<AudioMeterLevel>? AudioMeterUpdated;
    event EventHandler<Exception>? Failed;

    Task PrepareAsync(Rendering.RenderPlan plan, PreviewRequest request, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimelineTime position, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
