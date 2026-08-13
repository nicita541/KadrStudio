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
    event EventHandler<Exception>? Failed;

    Task PrepareAsync(Rendering.RenderPlan plan, PreviewRequest request, CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(TimelineTime position, CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
