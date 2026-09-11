using System.Buffers;
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
    Action<Exception>? reportFailure = null,
    IRenderCommandBuilder? commands = null) : IAsyncDisposable
{
    private readonly string _ffmpegPath = Path.GetFullPath(ffmpegPath);
    private readonly IRenderCommandBuilder _commands = commands ?? new FfmpegRenderCommandBuilder();
    private readonly Dictionary<Guid, VideoLayerWorker> _workers = [];
    private bool _disposed;

    public int ActiveWorkerCount => _workers.Count;
    public int PeakWorkerCount { get; private set; }
    public long StartedWorkerCount { get; private set; }
    public long FrameSizeBytes { get; private set; }
    public long FramesProduced { get; private set; }
    public long FramesDecoded { get; private set; }
    private readonly long _allocationStart = GC.GetTotalAllocatedBytes(false);
    public long AllocatedBytes => Math.Max(0, GC.GetTotalAllocatedBytes(false) - _allocationStart);
    public long CopiedBytes { get; private set; }
    public TimeSpan DecoderReadTime { get; private set; }
    public string Decoder { get; private set; } = "ffmpeg-cuda";
    public string HardwareAcceleration { get; private set; } = "cuda-requested";
    public string Device { get; private set; } = "CUDA";
    public string Fallback { get; private set; } = "available-on-decoder-failure";

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
            var composed = await ComposeAsync(plan, request, position, cancellationToken).ConfigureAwait(false);
            FramesProduced++;
            FrameSizeBytes = composed.Length;
            var frame = new VideoFrame(
                position, request.Width, request.Height, request.Width * 4,
                composed.Pixels.AsMemory(0, composed.Length), request.Generation.Video, composed.Owner);
            try { await present(frame).ConfigureAwait(false); }
            catch
            {
                composed.Owner.Dispose();
                throw;
            }
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

    private async Task<ComposedFrame> ComposeAsync(
        RenderPlan plan,
        PreviewRequest request,
        TimelineTime position,
        CancellationToken cancellationToken)
    {
        var active = plan.VisualLayers
            .Where(layer => IsActive(plan, layer, position))
            .OrderBy(layer => layer.TrackIndex)
            .ThenBy(layer => layer.TimelineRange.Start)
            .ThenBy(layer => layer.ClipId)
            .ToArray();
        var activeIds = active.Select(item => item.ClipId).ToHashSet();
        await RetireInactiveAsync(plan, position, activeIds).ConfigureAwait(false);

        // A single isolated layer is already a complete FFmpeg-composited canvas.
        // Ask FFmpeg for an opaque background and forward that buffer instead of
        // allocating and alpha-blending a second full-resolution frame in managed code.
        if (active.Length == 1 && plan.VisualLayers.Length == 1 && plan.VideoTransitions.Length == 0)
        {
            try
            {
                var worker = await GetOrCreateAsync(
                    plan, request, active[0], position, cancellationToken, opaqueOutput: true).ConfigureAwait(false);
                var decoded = await worker.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                RecordDecodedFrame(worker, decoded);
                return new ComposedFrame(decoded.Pixels, decoded.Length, decoded.Owner);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reportFailure?.Invoke(new VideoWorkerException(active[0].ClipId, exception));
                if (_workers.Remove(active[0].ClipId, out var failed))
                    await failed.DisposeAsync().ConfigureAwait(false);
            }
        }

        var frameSize = checked(request.Width * request.Height * 4);
        var whiteBackground = plan.VideoTransitions.Any(item =>
            item.Kind == TransitionKind.DipToWhite && item.TimelineRange.Contains(position));
        var destination = ArrayPool<byte>.Shared.Rent(frameSize);
        var destinationOwner = new PooledBufferOwner(destination);
        FillBackground(destination.AsSpan(0, frameSize), whiteBackground ? (byte)255 : (byte)0);

        try
        {
            foreach (var layer in active)
            {
                DecodedFrame? decoded = null;
                try
                {
                    var worker = await GetOrCreateAsync(plan, request, layer, position, cancellationToken)
                        .ConfigureAwait(false);
                    decoded = await worker.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                    RecordDecodedFrame(worker, decoded);
                    AlphaComposite(destination.AsSpan(0, frameSize), decoded.Pixels.AsSpan(0, decoded.Length));
                    CopiedBytes += decoded.Length;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    reportFailure?.Invoke(new VideoWorkerException(layer.ClipId, exception));
                    if (_workers.Remove(layer.ClipId, out var failed))
                        await failed.DisposeAsync().ConfigureAwait(false);
                }
                finally { decoded?.Owner.Dispose(); }
            }
            return new ComposedFrame(destination, frameSize, destinationOwner);
        }
        catch
        {
            destinationOwner.Dispose();
            throw;
        }
    }

    private void RecordDecodedFrame(VideoLayerWorker worker, DecodedFrame decoded)
    {
        FramesDecoded++;
        CopiedBytes += decoded.Length;
        DecoderReadTime += decoded.ReadTime;
        if (!worker.FellBackToSoftware) return;
        Decoder = "ffmpeg-software";
        HardwareAcceleration = "none";
        Device = "CPU";
        Fallback = "cuda-failed-software-active";
    }

    private async Task<VideoLayerWorker> GetOrCreateAsync(
        RenderPlan plan,
        PreviewRequest request,
        RenderVisualLayer layer,
        TimelineTime position,
        CancellationToken cancellationToken,
        bool opaqueOutput = false)
    {
        if (_workers.TryGetValue(layer.ClipId, out var existing)) return existing;
        var range = ActiveRange(plan, layer);
        var end = range.End <= plan.Range.End ? range.End : plan.Range.End;
        if (end <= position) throw new InvalidOperationException("Visual layer has no remaining decode range.");
        var workerPlan = CreateWorkerPlan(plan, layer, new TimeRange(position, end - position));
        var worker = await VideoLayerWorker.StartAsync(
            _ffmpegPath, _commands, workerPlan, request, opaqueOutput, cancellationToken).ConfigureAwait(false);
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
        private Process _process;
        private readonly int _frameSize;
        private Task<string> _errorOutput;
        private readonly string _ffmpegPath;
        private readonly ExternalRenderCommand _softwareCommand;
        private bool _usingHardware = true;
        private bool _disposed;

        private VideoLayerWorker(
            string ffmpegPath,
            Process process,
            ExternalRenderCommand softwareCommand,
            int frameSize)
        {
            _ffmpegPath = ffmpegPath;
            _process = process;
            _softwareCommand = softwareCommand;
            _frameSize = frameSize;
            _errorOutput = process.StandardError.ReadToEndAsync();
        }

        public bool FellBackToSoftware { get; private set; }

        public static Task<VideoLayerWorker> StartAsync(
            string ffmpegPath,
            IRenderCommandBuilder commands,
            RenderPlan plan,
            PreviewRequest request,
            bool opaqueOutput,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new RenderOutputOptions(
                RenderPurpose.FrameServer, "pipe:1", request.Width, request.Height,
                IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false,
                TransparentBackground: !opaqueOutput, UseHardwareDecoding: true);
            var command = commands.Build(plan, options);
            var softwareCommand = commands.Build(plan, options with { UseHardwareDecoding = false });
            var process = StartProcess(ffmpegPath, command);
            return Task.FromResult(new VideoLayerWorker(
                ffmpegPath, process, softwareCommand, checked(request.Width * request.Height * 4)));
        }

        private static Process StartProcess(string ffmpegPath, ExternalRenderCommand command)
        {
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
            return process;
        }

        public async Task<DecodedFrame> ReadFrameAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { return await ReadFrameCoreAsync(cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException) when (_usingHardware)
            {
                await StopProcessAsync().ConfigureAwait(false);
                _process = StartProcess(_ffmpegPath, _softwareCommand);
                _errorOutput = _process.StandardError.ReadToEndAsync();
                _usingHardware = false;
                FellBackToSoftware = true;
                return await ReadFrameCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<DecodedFrame> ReadFrameCoreAsync(CancellationToken cancellationToken)
        {
            var bytes = ArrayPool<byte>.Shared.Rent(_frameSize);
            var owner = new PooledBufferOwner(bytes);
            var offset = 0;
            var started = Stopwatch.GetTimestamp();
            try
            {
                while (offset < _frameSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await _process.StandardOutput.BaseStream.ReadAsync(
                        bytes.AsMemory(offset, _frameSize - offset), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new EndOfStreamException("Visual source decoder ended before a complete BGRA frame.");
                    offset += read;
                }
                return new DecodedFrame(bytes, _frameSize, Stopwatch.GetElapsedTime(started), owner);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        private async Task StopProcessAsync()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            _process.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await StopProcessAsync().ConfigureAwait(false);
        }
    }

    private sealed record DecodedFrame(byte[] Pixels, int Length, TimeSpan ReadTime, PooledBufferOwner Owner);
    private sealed record ComposedFrame(byte[] Pixels, int Length, PooledBufferOwner Owner);

    private sealed class PooledBufferOwner(byte[] buffer) : IDisposable
    {
        private byte[]? _buffer = buffer;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _buffer, null);
            if (value is not null) ArrayPool<byte>.Shared.Return(value);
        }
    }
}

public sealed class VideoWorkerException(Guid clipId, Exception innerException)
    : Exception($"Visual worker {clipId:N} failed: {innerException.Message}", innerException)
{
    public Guid ClipId { get; } = clipId;
}
