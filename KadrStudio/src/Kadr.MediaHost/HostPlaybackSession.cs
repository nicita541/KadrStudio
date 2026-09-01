using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StereoPcmMeter _meter = new();
    private readonly Stopwatch _fallbackClock = new();
    private RenderPlan? _plan;
    private PreviewRequest _request;
    private CancellationTokenSource? _videoCancellation;
    private CancellationTokenSource? _audioCancellation;
    private VideoWorkerSupervisor? _videoWorkers;
    private AudioWorkerSupervisor? _audioWorkers;
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
    public MediaHostDiagnostics Diagnostics => new(
        Environment.ProcessId,
        _videoWorkers?.ActiveWorkerCount ?? 0,
        _audioWorkers?.ActiveWorkerCount ?? 0,
        _videoWorkers?.PeakWorkerCount ?? 0,
        _audioWorkers?.PeakWorkerCount ?? 0,
        _videoWorkers?.StartedWorkerCount ?? 0,
        _audioWorkers?.StartedWorkerCount ?? 0);
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
        _videoWorkers = new VideoWorkerSupervisor(_ffmpegPath, ReportVideoWorkerFailure);
        if (play)
        {
            _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _videoPump = RunVideoWorkersAsync(
                _videoWorkers, plan, start, request, continuous: true, _frames.Writer, token);
            _presentation = PresentFramesAsync(_frames.Reader, request.FrameRate, token);
        }
        else
        {
            _videoPump = RunVideoWorkersAsync(
                _videoWorkers, plan, start, request, continuous: false, null, token);
        }
    }

    private async Task StartAudioAsync(RenderPlan plan, CancellationToken cancellationToken)
    {
        if (plan.AudioLayers.Length == 0) return;
        _audioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _audioCancellation.Token;
        _audioWorkers = new AudioWorkerSupervisor(_ffmpegPath, ReportAudioWorkerFailure);
        _audioSampleRate = plan.AudioSampleRate;
        _audioBuffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(_audioSampleRate, 2))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        _audioOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
        _audioOutput.Init(_audioBuffer);
        _audioPump = RunAudioWorkersAsync(_audioWorkers, plan, _request.Generation.Audio, token);
        await WaitForPrerollAsync(_audioBuffer, token).ConfigureAwait(false);
        _fallbackClock.Restart();
        _audioOutput.Play();
    }

    private async Task RunVideoWorkersAsync(
        VideoWorkerSupervisor workers,
        RenderPlan plan,
        TimelineTime start,
        PreviewRequest request,
        bool continuous,
        ChannelWriter<VideoFrame>? writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await workers.RunAsync(plan, request, start, continuous, async frame =>
            {
                if (continuous) await writer!.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                else Present(frame);
            }, cancellationToken).ConfigureAwait(false);
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

    private async Task RunAudioWorkersAsync(
        AudioWorkerSupervisor workers,
        RenderPlan plan,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await workers.RunAsync(plan, plan.Range.Start, generation, async block =>
            {
                var provider = _audioBuffer;
                if (provider is null || block.Generation != _request.Generation.Audio) return;
                while (provider.BufferedDuration > TimeSpan.FromSeconds(1.5))
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.InterleavedSamples.Span);
                provider.AddSamples(bytes.ToArray(), 0, bytes.Length);
                AudioMeterUpdated?.Invoke(this, _meter.Measure(block.InterleavedSamples.Span, block.Channels));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportPipelineFailure(exception, video: false); }
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
        if (_videoWorkers is not null) await _videoWorkers.DisposeAsync().ConfigureAwait(false);
        var tasks = new[] { _videoPump, _presentation }.Where(item => item is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _videoWorkers = null;
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
        if (_audioWorkers is not null) await _audioWorkers.DisposeAsync().ConfigureAwait(false);
        if (_audioPump is not null)
            try { await _audioPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _audioOutput?.Dispose();
        _audioWorkers = null;
        _audioOutput = null;
        _audioBuffer = null;
        _audioPump = null;
        _audioCancellation?.Dispose();
        _audioCancellation = null;
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
        if (video && _audioWorkers is null || !video && _videoWorkers is null) SetState(PreviewState.Failed);
        Failed?.Invoke(this, exception);
    }

    private void ReportVideoWorkerFailure(Exception exception)
        => Failed?.Invoke(this, exception);

    private void ReportAudioWorkerFailure(Exception exception)
        => Failed?.Invoke(this, exception);

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
