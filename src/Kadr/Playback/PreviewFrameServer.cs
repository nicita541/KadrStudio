using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using NAudio.Wave;

namespace KadrStudio.Playback;

public sealed class PreviewFrameServer(
    string ffmpegPath,
    TimelineRenderCoordinator coordinator) : IPreviewEngine
{
    private readonly string _ffmpegPath = Path.GetFullPath(ffmpegPath);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RenderPlan? _plan;
    private PreviewRequest _request;
    private CancellationTokenSource? _videoCancellation;
    private CancellationTokenSource? _audioCancellation;
    private Process? _videoProcess;
    private Process? _audioProcess;
    private BufferedWaveProvider? _audioBuffer;
    private WasapiOut? _audioOutput;
    private Task? _videoTask;
    private Task? _presentationTask;
    private Task? _audioTask;
    private Channel<VideoFrame>? _videoFrames;
    private VideoFrame? _lastFrame;
    private readonly Stopwatch _fallbackClock = new();
    private TimelineTime _position;
    private TimelineTime _playbackStart;
    private bool _disposed;

    public PreviewState State { get; private set; } = PreviewState.Idle;
    public TimelineTime Position => State == PreviewState.Playing ? GetClockPosition() : _position;
    public VideoFrame? LastFrame => _lastFrame;
    public event EventHandler<PreviewState>? StateChanged;
    public event EventHandler<VideoFrame>? FramePresented;
    public event EventHandler<Exception>? Failed;

    public async Task PrepareAsync(RenderPlan plan, PreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopProcessesAsync().ConfigureAwait(false);
            _plan = plan;
            _request = request;
            _position = request.Position;
            SetState(PreviewState.Preparing);
            await StartProcessesAsync(Position, play: false, cancellationToken).ConfigureAwait(false);
            SetState(PreviewState.Paused);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailed(exception);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_plan is null) throw new InvalidOperationException("Preview is not prepared.");
            if (State == PreviewState.Playing) return;
            SetState(PreviewState.Buffering);
            await StopProcessesAsync().ConfigureAwait(false);
            await StartProcessesAsync(_position, play: true, cancellationToken).ConfigureAwait(false);
            SetState(PreviewState.Playing);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdatePlanAsync(
        RenderPlan plan,
        PreviewRequest request,
        bool restartVideo,
        bool restartAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!restartVideo && !restartAudio) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var wasPlaying = State == PreviewState.Playing;
            var current = Position.Clamp(plan.Range.Start, plan.Range.End);
            _plan = plan;
            _request = request;
            _position = current;
            if (wasPlaying) SetState(PreviewState.Buffering);

            if (restartVideo) await StopVideoAsync().ConfigureAwait(false);
            if (restartAudio)
            {
                await StopAudioAsync().ConfigureAwait(false);
                _playbackStart = current;
                _fallbackClock.Restart();
            }

            var remaining = plan.Range.End - current;
            if (remaining > TimelineTime.Zero)
            {
                var sliced = SlicePlan(plan, new TimeRange(current, remaining));
                if (restartVideo)
                    await StartVideoAsync(sliced, current, wasPlaying, cancellationToken).ConfigureAwait(false);
                if (restartAudio && wasPlaying)
                    await StartAudioAsync(sliced, cancellationToken).ConfigureAwait(false);
            }
            SetState(wasPlaying ? PreviewState.Playing : PreviewState.Paused);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailed(exception);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task SeekAsync(TimelineTime position, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_plan is null) throw new InvalidOperationException("Preview is not prepared.");
            var wasPlaying = State == PreviewState.Playing;
            _position = position.Clamp(_plan.Range.Start, _plan.Range.End);
            SetState(PreviewState.Buffering);
            await StopProcessesAsync().ConfigureAwait(false);
            await StartProcessesAsync(_position, wasPlaying, cancellationToken).ConfigureAwait(false);
            SetState(wasPlaying ? PreviewState.Playing : PreviewState.Paused);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailed(exception);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _position = Position;
            await StopProcessesAsync().ConfigureAwait(false);
            SetState(PreviewState.Paused);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopProcessesAsync().ConfigureAwait(false);
            _position = TimelineTime.Zero;
            SetState(PreviewState.Idle);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopProcessesAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StartProcessesAsync(TimelineTime position, bool play, CancellationToken cancellationToken)
    {
        if (_plan is null) return;
        var remaining = _plan.Range.End - position;
        if (remaining <= TimelineTime.Zero) return;
        var range = new TimeRange(position, remaining);
        var plan = SlicePlan(_plan, range);
        _playbackStart = position;
        _fallbackClock.Restart();
        await StartVideoAsync(plan, position, play, cancellationToken).ConfigureAwait(false);
        if (play) await StartAudioAsync(plan, cancellationToken).ConfigureAwait(false);
        await Task.Yield();
    }

    private Task StartVideoAsync(RenderPlan plan, TimelineTime position, bool play, CancellationToken cancellationToken)
    {
        if (plan.VisualLayers.Length == 0) return Task.CompletedTask;
        _videoCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _videoCancellation.Token;
        var command = coordinator.CreateCommand(plan, new RenderOutputOptions(
            RenderPurpose.FrameServer, "pipe:1", _request.Width, _request.Height,
            IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false));
        _videoProcess = Start(command, redirectOutput: true);
        if (play)
        {
            _videoFrames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(8)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });
            _videoTask = PumpVideoAsync(_videoProcess, position, continuous: true, _videoFrames.Writer, token);
            _presentationTask = PresentFramesAsync(_videoFrames.Reader, token);
        }
        else
        {
            _videoTask = PumpVideoAsync(_videoProcess, position, continuous: false, null, token);
        }
        return Task.CompletedTask;
    }

    private async Task StartAudioAsync(RenderPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AudioLayers.Length == 0) return;
        _audioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _audioCancellation.Token;
        var command = coordinator.CreateCommand(plan, new RenderOutputOptions(
            RenderPurpose.AudioServer, "pipe:1", 16, 16,
            IncludeVideo: false, IncludeAudio: true, IncludeOverlays: false));
        _audioProcess = Start(command, redirectOutput: true);
        _audioBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        _audioOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
        _audioOutput.Init(_audioBuffer);
        _audioTask = PumpAudioAsync(_audioProcess, token);
        await WaitForAudioPrerollAsync(_audioBuffer, token).ConfigureAwait(false);
        _fallbackClock.Restart();
        _audioOutput.Play();
    }

    private async Task PumpVideoAsync(
        Process process,
        TimelineTime start,
        bool continuous,
        ChannelWriter<VideoFrame>? writer,
        CancellationToken token)
    {
        var frameSize = checked(_request.Width * _request.Height * 4);
        var bytes = GC.AllocateUninitializedArray<byte>(frameSize);
        var frameIndex = 0L;
        try
        {
            while (await ReadExactlyAsync(process.StandardOutput.BaseStream, bytes, token).ConfigureAwait(false))
            {
                var timestamp = start + TimelineTime.FromFrames(frameIndex++, _request.FrameRate);
                var owned = GC.AllocateUninitializedArray<byte>(frameSize);
                Buffer.BlockCopy(bytes, 0, owned, 0, frameSize);
                var frame = new VideoFrame(timestamp, _request.Width, _request.Height, _request.Width * 4,
                    owned, _request.Generation.Video);
                if (!continuous)
                {
                    Present(frame);
                    break;
                }
                await writer!.WriteAsync(frame, token).ConfigureAwait(false);
            }
            writer?.TryComplete();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            writer?.TryComplete(exception);
            ReportPipelineFailure(exception, video: true);
        }
    }

    private async Task PresentFramesAsync(ChannelReader<VideoFrame> reader, CancellationToken token)
    {
        var frameDuration = TimelineTime.FromFrames(1, _request.FrameRate);
        try
        {
            await foreach (var frame in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var delta = frame.Position - GetClockPosition();
                if (delta < -frameDuration) continue;
                if (delta > TimelineTime.Zero)
                    await Task.Delay(TimeSpan.FromTicks(delta.Ticks), token).ConfigureAwait(false);
                Present(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportPipelineFailure(exception, video: true); }
    }

    private async Task PumpAudioAsync(Process process, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                var audioBuffer = _audioBuffer;
                if (audioBuffer is null) break;
                while (audioBuffer.BufferedDuration > TimeSpan.FromSeconds(1.5))
                    await Task.Delay(10, token).ConfigureAwait(false);
                audioBuffer.AddSamples(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportPipelineFailure(exception, video: false); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private Process Start(ExternalRenderCommand command, bool redirectOutput)
    {
        var info = new ProcessStartInfo
        {
            FileName = _ffmpegPath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput, RedirectStandardError = true
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg preview server did not start.");
        _ = DrainErrorsAsync(process);
        return process;
    }

    private async Task StopProcessesAsync()
    {
        _fallbackClock.Stop();
        await StopVideoAsync().ConfigureAwait(false);
        await StopAudioAsync().ConfigureAwait(false);
    }

    private async Task StopVideoAsync()
    {
        _videoCancellation?.Cancel();
        TryKill(_videoProcess);
        var tasks = new[] { _videoTask, _presentationTask }.Where(task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0) try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _videoProcess?.Dispose();
        _videoProcess = null; _videoTask = null; _presentationTask = null; _videoFrames = null;
        _videoCancellation?.Dispose(); _videoCancellation = null;
    }

    private async Task StopAudioAsync()
    {
        _audioCancellation?.Cancel();
        _audioOutput?.Stop();
        TryKill(_audioProcess);
        if (_audioTask is not null)
            try { await _audioTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _audioProcess?.Dispose(); _audioOutput?.Dispose();
        _audioProcess = null; _audioOutput = null; _audioBuffer = null; _audioTask = null;
        _audioCancellation?.Dispose(); _audioCancellation = null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try { await process.StandardError.ReadToEndAsync().ConfigureAwait(false); } catch { }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task WaitForAudioPrerollAsync(BufferedWaveProvider buffer, CancellationToken token)
    {
        var minimumBytes = buffer.WaveFormat.AverageBytesPerSecond / 8;
        var deadline = Stopwatch.StartNew();
        while (buffer.BufferedBytes < minimumBytes && deadline.Elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(10, token).ConfigureAwait(false);
    }

    private TimelineTime GetClockPosition()
    {
        var output = _audioOutput;
        if (output is not null && output.PlaybackState == PlaybackState.Playing)
        {
            var seconds = output.GetPosition() / (double)WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2).AverageBytesPerSecond;
            return _playbackStart + TimelineTime.FromSeconds(seconds);
        }
        return _playbackStart + TimelineTime.FromSeconds(_fallbackClock.Elapsed.TotalSeconds);
    }

    private void Present(VideoFrame frame)
    {
        _lastFrame = frame;
        _position = frame.Position;
        FramePresented?.Invoke(this, frame);
    }

    private void ReportPipelineFailure(Exception exception, bool video)
    {
        if (video && _audioProcess is null || !video && _videoProcess is null)
            SetState(PreviewState.Failed);
        Failed?.Invoke(this, exception);
    }

    private static RenderPlan SlicePlan(RenderPlan plan, TimeRange range)
    {
        var visual = plan.VisualLayers.Where(layer => layer.TimelineRange.Intersects(range)).ToImmutableArray();
        var audio = plan.AudioLayers.Where(layer => layer.TimelineRange.Intersects(range)).ToImmutableArray();
        var text = plan.TextLayers.Where(layer => layer.TimelineRange.Intersects(range)).ToImmutableArray();
        return plan with { Range = range, VisualLayers = visual, AudioLayers = audio, TextLayers = text };
    }

    private void SetState(PreviewState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void SetFailed(Exception exception)
    {
        SetState(PreviewState.Failed);
        Failed?.Invoke(this, exception);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class TimelineRangeExtensions
{
    public static bool Intersects(this TimeRange left, TimeRange right)
        => left.Start < right.End && left.End > right.Start;

    public static TimelineTime Clamp(this TimelineTime value, TimelineTime minimum, TimelineTime maximum)
        => value < minimum ? minimum : value > maximum ? maximum : value;
}
