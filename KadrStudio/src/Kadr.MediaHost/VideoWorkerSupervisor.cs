using System.Collections.Immutable;
using System.Diagnostics;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Rendering;

namespace KadrStudio.MediaHost;

/// <summary>
/// Owns independent sequential decoders for the currently active visual clips.
/// A failed worker removes only its layer; the compositor and other layers keep
/// producing frames on the shared timeline clock.
/// </summary>
public sealed class VideoWorkerSupervisor(
    string ffmpegPath,
    Action<Exception>? reportFailure = null) : IAsyncDisposable
{
    private readonly string _ffmpegPath = Path.GetFullPath(ffmpegPath);
    private readonly FfmpegRenderCommandBuilder _commands = new();
    private readonly Dictionary<Guid, VideoLayerWorker> _workers = [];
    private bool _disposed;

    public int ActiveWorkerCount => _workers.Count;
    public int PeakWorkerCount { get; private set; }
    public long StartedWorkerCount { get; private set; }

    public async Task RunAsync(
        RenderPlan plan,
        PreviewRequest request,
        TimelineTime start,
        bool continuous,
        Func<VideoFrame, ValueTask> present,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(present);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frameIndex = 0L;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = start + TimelineTime.FromFrames(frameIndex++, request.FrameRate);
            if (position >= plan.Range.End) break;
            var pixels = await ComposeAsync(plan, request, position, cancellationToken).ConfigureAwait(false);
            await present(new VideoFrame(
                position, request.Width, request.Height, request.Width * 4,
                pixels, request.Generation.Video)).ConfigureAwait(false);
            if (!continuous) break;
        } while (true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var workers = _workers.Values.ToArray();
        _workers.Clear();
        foreach (var worker in workers) await worker.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<byte[]> ComposeAsync(
        RenderPlan plan,
        PreviewRequest request,
        TimelineTime position,
        CancellationToken cancellationToken)
    {
        var frameSize = checked(request.Width * request.Height * 4);
        var whiteBackground = plan.VideoTransitions.Any(item =>
            item.Kind == TransitionKind.DipToWhite && item.TimelineRange.Contains(position));
        var destination = GC.AllocateUninitializedArray<byte>(frameSize);
        FillBackground(destination, whiteBackground ? (byte)255 : (byte)0);
        var active = plan.VisualLayers
            .Where(layer => IsActive(plan, layer, position))
            .OrderBy(layer => layer.TrackIndex)
            .ThenBy(layer => layer.TimelineRange.Start)
            .ThenBy(layer => layer.ClipId)
            .ToArray();
        var activeIds = active.Select(item => item.ClipId).ToHashSet();
        await RetireInactiveAsync(plan, position, activeIds).ConfigureAwait(false);

        foreach (var layer in active)
        {
            byte[]? source = null;
            try
            {
                var worker = await GetOrCreateAsync(plan, request, layer, position, cancellationToken)
                    .ConfigureAwait(false);
                source = await worker.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reportFailure?.Invoke(new VideoWorkerException(layer.ClipId, exception));
                if (_workers.Remove(layer.ClipId, out var failed))
                    await failed.DisposeAsync().ConfigureAwait(false);
            }
            if (source is not null) AlphaComposite(destination, source);
        }
        return destination;
    }

    private async Task<VideoLayerWorker> GetOrCreateAsync(
        RenderPlan plan,
        PreviewRequest request,
        RenderVisualLayer layer,
        TimelineTime position,
        CancellationToken cancellationToken)
    {
        if (_workers.TryGetValue(layer.ClipId, out var existing)) return existing;
        var range = ActiveRange(plan, layer);
        var end = range.End <= plan.Range.End ? range.End : plan.Range.End;
        if (end <= position) throw new InvalidOperationException("Visual layer has no remaining decode range.");
        var workerPlan = CreateWorkerPlan(plan, layer, new TimeRange(position, end - position));
        var worker = await VideoLayerWorker.StartAsync(
            _ffmpegPath, _commands, workerPlan, request, cancellationToken).ConfigureAwait(false);
        _workers.Add(layer.ClipId, worker);
        StartedWorkerCount++;
        PeakWorkerCount = Math.Max(PeakWorkerCount, _workers.Count);
        return worker;
    }

    private async Task RetireInactiveAsync(
        RenderPlan plan,
        TimelineTime position,
        IReadOnlySet<Guid> activeIds)
    {
        foreach (var pair in _workers.ToArray())
        {
            var layer = plan.VisualLayers.FirstOrDefault(item => item.ClipId == pair.Key);
            if (layer is not null && (activeIds.Contains(pair.Key) || position < ActiveRange(plan, layer).End)) continue;
            _workers.Remove(pair.Key);
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static RenderPlan CreateWorkerPlan(
        RenderPlan plan,
        RenderVisualLayer layer,
        TimeRange range)
    {
        var transitions = plan.VideoTransitions
            .Where(item => (item.From.ClipId == layer.ClipId || item.To.ClipId == layer.ClipId) &&
                           item.TimelineRange.Overlaps(range))
            .ToImmutableArray();
        return plan with
        {
            Range = range,
            VisualLayers = [layer],
            AudioLayers = [],
            TextLayers = [],
            VideoTransitions = transitions,
            AudioTransitions = []
        };
    }

    private static bool IsActive(RenderPlan plan, RenderVisualLayer layer, TimelineTime position)
        => layer.TimelineRange.Contains(position) || plan.VideoTransitions.Any(item =>
            (item.From.ClipId == layer.ClipId || item.To.ClipId == layer.ClipId) &&
            item.TimelineRange.Contains(position));

    private static TimeRange ActiveRange(RenderPlan plan, RenderVisualLayer layer)
    {
        var start = layer.TimelineRange.Start;
        var end = layer.TimelineRange.End;
        foreach (var transition in plan.VideoTransitions.Where(item =>
                     item.From.ClipId == layer.ClipId || item.To.ClipId == layer.ClipId))
        {
            if (transition.TimelineRange.Start < start) start = transition.TimelineRange.Start;
            if (transition.TimelineRange.End > end) end = transition.TimelineRange.End;
        }
        return new TimeRange(start, end - start);
    }

    private static void FillBackground(Span<byte> pixels, byte value)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
    }

    private static void AlphaComposite(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (destination.Length != source.Length) throw new ArgumentException("Frame dimensions do not match.");
        for (var offset = 0; offset < destination.Length; offset += 4)
        {
            var alpha = source[offset + 3];
            if (alpha == 0) continue;
            if (alpha == 255)
            {
                destination[offset] = source[offset];
                destination[offset + 1] = source[offset + 1];
                destination[offset + 2] = source[offset + 2];
                continue;
            }
            var inverse = 255 - alpha;
            destination[offset] = (byte)((source[offset] * alpha + destination[offset] * inverse + 127) / 255);
            destination[offset + 1] = (byte)((source[offset + 1] * alpha + destination[offset + 1] * inverse + 127) / 255);
            destination[offset + 2] = (byte)((source[offset + 2] * alpha + destination[offset + 2] * inverse + 127) / 255);
        }
    }

    private sealed class VideoLayerWorker : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly int _frameSize;
        private readonly Task<string> _errorOutput;
        private bool _disposed;

        private VideoLayerWorker(Process process, int frameSize)
        {
            _process = process;
            _frameSize = frameSize;
            _errorOutput = process.StandardError.ReadToEndAsync();
        }

        public static Task<VideoLayerWorker> StartAsync(
            string ffmpegPath,
            FfmpegRenderCommandBuilder commands,
            RenderPlan plan,
            PreviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = commands.Build(plan, new RenderOutputOptions(
                RenderPurpose.FrameServer, "pipe:1", request.Width, request.Height,
                IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false,
                TransparentBackground: true));
            var info = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            var process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("Visual source decoder did not start.");
            return Task.FromResult(new VideoLayerWorker(process, checked(request.Width * request.Height * 4)));
        }

        public async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bytes = GC.AllocateUninitializedArray<byte>(_frameSize);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await _process.StandardOutput.BaseStream
                    .ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    var errors = await _errorOutput.ConfigureAwait(false);
                    throw new EndOfStreamException(
                        string.IsNullOrWhiteSpace(errors) ? "Visual source decoder ended early." : errors);
                }
                offset += read;
            }
            return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _process.Dispose();
        }
    }
}

public sealed class VideoWorkerException(Guid clipId, Exception innerException)
    : Exception($"Visual worker {clipId:N} failed: {innerException.Message}", innerException)
{
    public Guid ClipId { get; } = clipId;
}
