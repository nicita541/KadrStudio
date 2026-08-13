using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Rendering;
using NAudio.Wave;

namespace KadrStudio.MediaHost;

/// <summary>
/// Persistent host-side playback session. Video and audio own independent
/// process generations and cancellation; replacing one pipeline never tears
/// down the IPC session or the other pipeline.
/// </summary>
public sealed class HostPlaybackSession(string ffmpegPath) : IPreviewEngine
{
    private readonly string _ffmpegPath = Path.GetFullPath(ffmpegPath);
    private readonly FfmpegRenderCommandBuilder _commands = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StereoPcmMeter _meter = new();
    private readonly Stopwatch _fallbackClock = new();
    private RenderPlan? _plan;
    private PreviewRequest _request;
    private CancellationTokenSource? _videoCancellation;
    private CancellationTokenSource? _audioCancellation;
    private Process? _videoProcess;
    private Process? _audioProcess;
    private Channel<VideoFrame>? _frames;
    private Task? _videoPump;
    private Task? _presentation;
    private Task? _audioPump;
    private BufferedWaveProvider? _audioBuffer;
    private WasapiOut? _audioOutput;
    private VideoFrame? _lastFrame;
    private TimelineTime _position;
    private TimelineTime _playbackStart;
    private int _audioSampleRate = 48_000;
    private bool _disposed;

    public PreviewState State { get; private set; } = PreviewState.Idle;
    public TimelineTime Position => State == PreviewState.Playing ? GetClockPosition() : _position;
    public VideoFrame? LastFrame => _lastFrame;
    public long AudioGeneration => _request.Generation.Audio;
    public event EventHandler<PreviewState>? StateChanged;
    public event EventHandler<VideoFrame>? FramePresented;
    public event EventHandler<AudioMeterLevel>? AudioMeterUpdated;
    public event EventHandler<Exception>? Failed;

    public async Task PrepareAsync(RenderPlan plan, PreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopPipelinesAsync().ConfigureAwait(false);
            _plan = plan;
            _request = request;
            _position = request.Position;
            SetState(PreviewState.Preparing);
            await StartPipelinesAsync(_position, play: false, cancellationToken).ConfigureAwait(false);
            SetState(PreviewState.Paused);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailed(exception);
            throw;
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
            var current = Clamp(Position, plan.Range.Start, plan.Range.End);
            _plan = plan;
            _request = request;
            _position = current;
            if (wasPlaying) SetState(PreviewState.Buffering);
            if (restartVideo) await StopVideoAsync().ConfigureAwait(false);
            if (restartAudio) await StopAudioAsync().ConfigureAwait(false);
            var remaining = plan.Range.End - current;
            if (remaining > TimelineTime.Zero)
            {
                var slice = SlicePlan(plan, new TimeRange(current, remaining));
                if (restartVideo) StartVideo(slice, current, wasPlaying, cancellationToken);
                if (restartAudio && wasPlaying)
                    await TryStartAudioAsync(slice, cancellationToken).ConfigureAwait(false);
            }
            if (restartAudio)
            {
                _playbackStart = current;
                _fallbackClock.Restart();
            }
            SetState(wasPlaying ? PreviewState.Playing : PreviewState.Paused);
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
            await StopPipelinesAsync().ConfigureAwait(false);
            await StartPipelinesAsync(_position, play: true, cancellationToken).ConfigureAwait(false);
            SetState(PreviewState.Playing);
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
            _position = Clamp(position, _plan.Range.Start, _plan.Range.End);
            SetState(PreviewState.Buffering);
            await StopPipelinesAsync().ConfigureAwait(false);
            await StartPipelinesAsync(_position, wasPlaying, cancellationToken).ConfigureAwait(false);
            SetState(wasPlaying ? PreviewState.Playing : PreviewState.Paused);
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _position = Position;
            await StopPipelinesAsync().ConfigureAwait(false);
            SetState(PreviewState.Paused);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopPipelinesAsync().ConfigureAwait(false);
            _position = TimelineTime.Zero;
            SetState(PreviewState.Idle);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopPipelinesAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StartPipelinesAsync(TimelineTime position, bool play, CancellationToken cancellationToken)
    {
        if (_plan is null) return;
        var remaining = _plan.Range.End - position;
        if (remaining <= TimelineTime.Zero) return;
        var plan = SlicePlan(_plan, new TimeRange(position, remaining));
        _playbackStart = position;
        _fallbackClock.Restart();
        StartVideo(plan, position, play, cancellationToken);
        if (play) await TryStartAudioAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryStartAudioAsync(RenderPlan plan, CancellationToken cancellationToken)
    {
        try { await StartAudioAsync(plan, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await StopAudioAsync().ConfigureAwait(false);
            ReportPipelineFailure(exception, video: false);
        }
    }

    private void StartVideo(RenderPlan plan, TimelineTime start, bool play, CancellationToken cancellationToken)
    {
        if (plan.VisualLayers.Length == 0 && (_plan?.VisualLayers.Length ?? 0) == 0) return;
        var request = _request;
        _videoCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _videoCancellation.Token;
        var command = _commands.Build(plan, new RenderOutputOptions(
            RenderPurpose.FrameServer, "pipe:1", request.Width, request.Height,
            IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false));
        _videoProcess = StartProcess(command);
        if (play)
        {
            _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _videoPump = PumpVideoAsync(_videoProcess, start, request, true, _frames.Writer, token);
            _presentation = PresentFramesAsync(_frames.Reader, request.FrameRate, token);
        }
        else
        {
            _videoPump = PumpVideoAsync(_videoProcess, start, request, false, null, token);
        }
    }

    private async Task StartAudioAsync(RenderPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AudioLayers.Length == 0) return;
        _audioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _audioCancellation.Token;
        var command = _commands.Build(plan, new RenderOutputOptions(
            RenderPurpose.AudioServer, "pipe:1", 16, 16,
            IncludeVideo: false, IncludeAudio: true, IncludeOverlays: false));
        _audioProcess = StartProcess(command);
        _audioSampleRate = plan.AudioSampleRate;
        _audioBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(_audioSampleRate, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        _audioOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
        _audioOutput.Init(_audioBuffer);
        _audioPump = PumpAudioAsync(_audioProcess, token);
        await WaitForPrerollAsync(_audioBuffer, token).ConfigureAwait(false);
        _fallbackClock.Restart();
        _audioOutput.Play();
    }

    private async Task PumpVideoAsync(
        Process process,
        TimelineTime start,
        PreviewRequest request,
        bool continuous,
        ChannelWriter<VideoFrame>? writer,
        CancellationToken cancellationToken)
    {
        var frameSize = checked(request.Width * request.Height * 4);
        var buffer = GC.AllocateUninitializedArray<byte>(frameSize);
        var index = 0L;
        try
        {
            while (await ReadExactlyAsync(process.StandardOutput.BaseStream, buffer, cancellationToken).ConfigureAwait(false))
            {
                var bytes = GC.AllocateUninitializedArray<byte>(frameSize);
                Buffer.BlockCopy(buffer, 0, bytes, 0, frameSize);
                var frame = new VideoFrame(start + TimelineTime.FromFrames(index++, request.FrameRate),
                    request.Width, request.Height, request.Width * 4, bytes, request.Generation.Video);
                if (!continuous)
                {
                    Present(frame);
                    break;
                }
                await writer!.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
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

    private async Task PresentFramesAsync(
        ChannelReader<VideoFrame> reader,
        FrameRate frameRate,
        CancellationToken cancellationToken)
    {
        var frameDuration = TimelineTime.FromFrames(1, frameRate);
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var delta = frame.Position - GetClockPosition();
                if (delta < -frameDuration) continue;
                if (delta > TimelineTime.Zero)
                    await Task.Delay(TimeSpan.FromSeconds(delta.TotalSeconds), cancellationToken).ConfigureAwait(false);
                Present(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportPipelineFailure(exception, video: true); }
    }

    private async Task PumpAudioAsync(Process process, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var provider = _audioBuffer;
                if (provider is null) break;
                while (provider.BufferedDuration > TimeSpan.FromSeconds(1.5))
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                provider.AddSamples(buffer, 0, read);
                var complete = read - read % (sizeof(float) * 2);
                if (complete > 0)
                {
                    var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, complete));
                    AudioMeterUpdated?.Invoke(this, _meter.Measure(samples));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportPipelineFailure(exception, video: false); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private Process StartProcess(ExternalRenderCommand command)
    {
        var info = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg preview pipeline did not start.");
        _ = DrainErrorsAsync(process);
        return process;
    }

    private async Task StopPipelinesAsync()
    {
        _fallbackClock.Stop();
        await StopVideoAsync().ConfigureAwait(false);
        await StopAudioAsync().ConfigureAwait(false);
    }

    private async Task StopVideoAsync()
    {
        _videoCancellation?.Cancel();
        TryKill(_videoProcess);
        var tasks = new[] { _videoPump, _presentation }.Where(item => item is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _videoProcess?.Dispose();
        _videoProcess = null;
        _videoPump = null;
        _presentation = null;
        _frames = null;
        _videoCancellation?.Dispose();
        _videoCancellation = null;
    }

    private async Task StopAudioAsync()
    {
        _audioCancellation?.Cancel();
        _audioOutput?.Stop();
        TryKill(_audioProcess);
        if (_audioPump is not null)
            try { await _audioPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _audioProcess?.Dispose();
        _audioOutput?.Dispose();
        _audioProcess = null;
        _audioOutput = null;
        _audioBuffer = null;
        _audioPump = null;
        _audioCancellation?.Dispose();
        _audioCancellation = null;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try { await process.StandardError.ReadToEndAsync().ConfigureAwait(false); } catch { }
    }

    private static async Task WaitForPrerollAsync(BufferedWaveProvider provider, CancellationToken cancellationToken)
    {
        var minimum = provider.WaveFormat.AverageBytesPerSecond / 8;
        var deadline = Stopwatch.StartNew();
        while (provider.BufferedBytes < minimum && deadline.Elapsed < TimeSpan.FromSeconds(1))
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
    }

    private TimelineTime GetClockPosition()
    {
        if (_audioOutput is { PlaybackState: PlaybackState.Playing } output)
        {
            var bytesPerSecond = WaveFormat.CreateIeeeFloatWaveFormat(_audioSampleRate, 2).AverageBytesPerSecond;
            return _playbackStart + TimelineTime.FromSeconds(output.GetPosition() / (double)bytesPerSecond);
        }
        return _playbackStart + TimelineTime.FromSeconds(_fallbackClock.Elapsed.TotalSeconds);
    }

    private void Present(VideoFrame frame)
    {
        if (frame.Generation != _request.Generation.Video) return;
        _lastFrame = frame;
        _position = frame.Position;
        FramePresented?.Invoke(this, frame);
    }

    private void ReportPipelineFailure(Exception exception, bool video)
    {
        if (video && _audioProcess is null || !video && _videoProcess is null) SetState(PreviewState.Failed);
        Failed?.Invoke(this, exception);
    }

    private static RenderPlan SlicePlan(RenderPlan plan, TimeRange range)
    {
        var videoTransitions = plan.VideoTransitions.Where(item => item.TimelineRange.Overlaps(range)).ToImmutableArray();
        var audioTransitions = plan.AudioTransitions.Where(item => item.TimelineRange.Overlaps(range)).ToImmutableArray();
        var videoIds = videoTransitions.SelectMany(item => new[] { item.From.ClipId, item.To.ClipId }).ToHashSet();
        var audioIds = audioTransitions.SelectMany(item => new[] { item.From.ClipId, item.To.ClipId }).ToHashSet();
        return plan with
        {
            Range = range,
            VisualLayers = plan.VisualLayers
                .Where(item => item.TimelineRange.Overlaps(range) || videoIds.Contains(item.ClipId)).ToImmutableArray(),
            AudioLayers = plan.AudioLayers
                .Where(item => item.TimelineRange.Overlaps(range) || audioIds.Contains(item.ClipId)).ToImmutableArray(),
            TextLayers = plan.TextLayers.Where(item => item.TimelineRange.Overlaps(range)).ToImmutableArray(),
            VideoTransitions = videoTransitions,
            AudioTransitions = audioTransitions
        };
    }

    private static TimelineTime Clamp(TimelineTime value, TimelineTime minimum, TimelineTime maximum)
        => value < minimum ? minimum : value > maximum ? maximum : value;

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
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
