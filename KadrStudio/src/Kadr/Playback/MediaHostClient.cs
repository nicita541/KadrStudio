using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;
using KadrStudio.Application.Preview;
using KadrStudio.Infrastructure.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;

namespace KadrStudio.Playback;

/// <summary>
/// Versioned named-pipe client for the out-of-process media runtime. It owns no
/// decoder, audio device or FFmpeg process other than the MediaHost watchdog.
/// </summary>
public sealed class MediaHostClient(string mediaHostPath, string ffmpegPath) : IPreviewEngine
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private readonly string _mediaHostPath = Path.GetFullPath(mediaHostPath);
    private readonly string _ffmpegPath = Path.GetFullPath(ffmpegPath);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MediaHostPacket>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeClientStream? _pipe;
    private Process? _host;
    private Task? _readerTask;
    private Task? _heartbeatTask;
    private Task? _hostErrorTask;
    private string _lastHostErrorTail = string.Empty;
    private Task? _frameReaderTask;
    private RenderPlan? _lastPlan;
    private sealed class RequestSnapshot(PreviewRequest request)
    {
        public PreviewRequest Request { get; } = request;
        public long PositionTicks = request.Position.Ticks;
    }
    private RequestSnapshot _requestSnapshot = new(default);
    private PreviewRequest _lastRequest
    {
        get
        {
            var snapshot = Volatile.Read(ref _requestSnapshot);
            return snapshot.Request with { Position = new TimelineTime(Interlocked.Read(ref snapshot.PositionTicks)) };
        }
        set => Volatile.Write(ref _requestSnapshot, new RequestSnapshot(value));
    }
    private bool _desiredPlaying;
    private bool _stopping;
    private bool _disposed;
    private int _recoveryScheduled;
    private TimelineTime _position;
    private long _pipeReadTicks;
    private long _framesReceived;
    private long _pipePayloadBytes;
    private long _frameBufferAllocatedBytes;
    private SharedFrameRingReader? _frameRing;
    private byte[][] _frameBuffers = [];
    private readonly Channel<byte> _frameSignals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });

    public PreviewState State { get; private set; } = PreviewState.Idle;
    public TimelineTime Position => _position;
    public int HostProcessId => _host is { HasExited: false } ? _host.Id : 0;
    public string LastHostErrorTail => Volatile.Read(ref _lastHostErrorTail);
    public event EventHandler<PreviewState>? StateChanged;
    public event EventHandler<VideoFrame>? FramePresented;
    public event EventHandler<AudioMeterLevel>? AudioMeterUpdated;
    public event EventHandler<Exception>? Failed;

    public async Task PrepareAsync(RenderPlan plan, PreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _lastPlan = plan;
        _lastRequest = request;
        _position = request.Position;
        _desiredPlaying = false;
        await ExecuteCommandAsync(MediaHostPacketType.Prepare, new MediaHostPrepare(plan, request), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpdatePlanAsync(
        RenderPlan plan,
        PreviewRequest request,
        bool restartVideo,
        bool restartAudio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _lastPlan = plan;
        _lastRequest = request;
        await ExecuteCommandAsync(MediaHostPacketType.UpdatePlan,
            new MediaHostUpdatePlan(plan, request, restartVideo, restartAudio), cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _desiredPlaying = true;
        await ExecuteCommandAsync(MediaHostPacketType.Start, new { }, cancellationToken).ConfigureAwait(false);
    }

    public Task SeekAsync(TimelineTime position, CancellationToken cancellationToken = default)
        => SeekAsync(position, _lastRequest.Generation, cancellationToken);

    public async Task SeekAsync(TimelineTime position, PreviewGeneration generation, CancellationToken cancellationToken = default)
    {
        _position = position;
        _lastRequest = _lastRequest with { Position = position, Generation = generation };
        await ExecuteCommandAsync(MediaHostPacketType.Seek, new MediaHostSeek(position, generation), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        _desiredPlaying = false;
        await ExecuteCommandAsync(MediaHostPacketType.Pause, new { }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _desiredPlaying = false;
        _stopping = true;
        try
        {
            if (_pipe is { IsConnected: true })
                await ExecuteCommandCoreAsync(MediaHostPacketType.Stop, new { }, cancellationToken).ConfigureAwait(false);
            SetState(PreviewState.Idle);
            _position = TimelineTime.Zero;
        }
        finally { _stopping = false; }
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await SendAndWaitAsync(MediaHostPacket.Empty(MediaHostPacketType.Ping, Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<MediaHostDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendAndWaitAsync(
            MediaHostPacket.Empty(MediaHostPacketType.Diagnostics, Guid.NewGuid()), cancellationToken)
            .ConfigureAwait(false);
        if (response.Type != MediaHostPacketType.DiagnosticsResult)
            throw new InvalidDataException($"Unexpected diagnostics response {response.Type}.");
        var remote = response.ReadHeader<MediaHostDiagnostics>();
        return remote with
        {
            FramesPresented = Interlocked.Read(ref _framesReceived),
            PipeReadTimeMs = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _pipeReadTicks)).TotalMilliseconds,
            CopiedBytes = remote.CopiedBytes + Interlocked.Read(ref _pipePayloadBytes),
            AllocatedBytes = remote.AllocatedBytes + Interlocked.Read(ref _frameBufferAllocatedBytes)
        };
    }

    public void TerminateHostForTest()
    {
        var process = _host;
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping = true;
        try
        {
            if (_pipe is { IsConnected: true })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await SendAndWaitAsync(
                        MediaHostPacket.Empty(MediaHostPacketType.Shutdown, Guid.NewGuid()), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException) { }
            }
            _lifetime.Cancel();
            CloseConnection();
            if (_readerTask is not null)
                try { await _readerTask.ConfigureAwait(false); } catch { }
            if (_heartbeatTask is not null)
                try { await _heartbeatTask.ConfigureAwait(false); } catch { }
            _frameSignals.Writer.TryComplete();
            if (_frameReaderTask is not null)
                try { await _frameReaderTask.ConfigureAwait(false); } catch { }
            await StopHostProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            FailPending(new ObjectDisposedException(nameof(MediaHostClient)));
            _lifetime.Dispose();
            _connectionGate.Dispose();
            _writeGate.Dispose();
        }
    }

    private async Task ExecuteCommandAsync<T>(
        MediaHostPacketType type,
        T header,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteCommandCoreAsync(type, header, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception) && !_disposed)
        {
            await RecoverAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteCommandCoreAsync(type, header, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteCommandCoreAsync<T>(
        MediaHostPacketType type,
        T header,
        CancellationToken cancellationToken)
    {
        var packet = MediaHostPacket.Create(type, header, Guid.NewGuid());
        var response = await SendAndWaitAsync(packet, cancellationToken).ConfigureAwait(false);
        if (response.Type == MediaHostPacketType.Failure)
        {
            var failure = response.ReadHeader<MediaHostFailure>();
            throw new MediaHostException(failure.Message, failure.Recoverable);
        }
        if (response.Type != MediaHostPacketType.Acknowledged)
            throw new InvalidDataException($"Unexpected response {response.Type} for {type}.");
        var state = response.ReadHeader<MediaHostState>();
        _position = state.Position;
        SetState(state.State);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true } && _host is { HasExited: false }) return;
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is { IsConnected: true } && _host is { HasExited: false }) return;
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _connectionGate.Release(); }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(_mediaHostPath)) throw new FileNotFoundException("Kadr.MediaHost was not found.", _mediaHostPath);
        if (!File.Exists(_ffmpegPath)) throw new FileNotFoundException("FFmpeg was not found.", _ffmpegPath);
        CloseConnection();
        await StopHostProcessAsync().ConfigureAwait(false);
        var pipeName = $"kadr-media-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var info = new ProcessStartInfo
        {
            FileName = _mediaHostPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("--ffmpeg");
        info.ArgumentList.Add(_ffmpegPath);
        info.ArgumentList.Add("--protocol");
        info.ArgumentList.Add(MediaHostProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _host = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!_host.Start()) throw new InvalidOperationException("Kadr.MediaHost did not start.");
        _hostErrorTask = DrainHostErrorsAsync(_host);
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        _readerTask = ReadLoopAsync(_pipe, _lifetime.Token);
        var hello = MediaHostPacket.Create(MediaHostPacketType.Hello,
            new MediaHostHello(MediaHostProtocol.Version, Environment.ProcessId), Guid.NewGuid());
        var response = await SendAndWaitAsync(hello, cancellationToken).ConfigureAwait(false);
        var accepted = response.Type == MediaHostPacketType.HelloAccepted
            ? response.ReadHeader<MediaHostHello>()
            : null;
        if (accepted?.ProtocolVersion != MediaHostProtocol.Version ||
            string.IsNullOrWhiteSpace(accepted.FrameBufferName) ||
            accepted.FrameSlotCapacity <= 0 || accepted.FrameSlotCount != 3)
            throw new InvalidDataException("Kadr.MediaHost handshake failed.");
        _frameRing?.Dispose();
        _frameRing = SharedFrameRingReader.Open(
            accepted.FrameBufferName, accepted.FrameSlotCapacity, accepted.FrameSlotCount);
        _frameBuffers = new byte[accepted.FrameSlotCount][];
        _frameReaderTask ??= ReadFramesAsync(_lifetime.Token);
        _heartbeatTask ??= HeartbeatLoopAsync(_lifetime.Token);
    }

    private async Task<MediaHostPacket> SendAndWaitAsync(
        MediaHostPacket packet,
        CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) throw new IOException("Kadr.MediaHost is not connected.");
        var completion = new TaskCompletionSource<MediaHostPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(packet.CorrelationId, completion))
            throw new InvalidOperationException("Duplicate MediaHost correlation ID.");
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await MediaHostPacketIO.WriteAsync(pipe, packet, cancellationToken).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommandTimeout);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Kadr.MediaHost did not acknowledge {packet.Type}.");
        }
        finally { _pending.TryRemove(packet.CorrelationId, out _); }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var readStarted = Stopwatch.GetTimestamp();
                var packet = await MediaHostPacketIO.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _pipeReadTicks, Stopwatch.GetTimestamp() - readStarted);
                if (packet is null) break;
                Dispatch(packet);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            var reason = failure ?? new IOException("Kadr.MediaHost disconnected.");
            FailPending(reason);
            if (!_disposed && !_stopping && Interlocked.Exchange(ref _recoveryScheduled, 1) == 0)
                _ = RecoverBackgroundAsync(reason);
        }
    }

    private void Dispatch(MediaHostPacket packet)
    {
        if (packet.CorrelationId != Guid.Empty && _pending.TryRemove(packet.CorrelationId, out var pending))
        {
            pending.TrySetResult(packet);
            return;
        }
        switch (packet.Type)
        {
            case MediaHostPacketType.StateChanged:
                var state = packet.ReadHeader<MediaHostState>();
                _position = state.Position;
                SetState(state.State);
                break;
            case MediaHostPacketType.VideoFrame:
                _frameSignals.Writer.TryWrite(0);
                break;
            case MediaHostPacketType.AudioMeter:
                var meter = packet.ReadHeader<MediaHostAudioMeterHeader>();
                if (meter.Generation == _lastRequest.Generation.Audio)
                {
                    _position = meter.Position;
                    AudioMeterUpdated?.Invoke(this, meter.Level);
                }
                break;
            case MediaHostPacketType.Failure:
                var failure = packet.ReadHeader<MediaHostFailure>();
                Failed?.Invoke(this, new MediaHostException(failure.Message, failure.Recoverable));
                break;
        }
    }

    private async Task RecoverBackgroundAsync(Exception reason)
    {
        try
        {
            SetState(PreviewState.Buffering);
            await RecoverAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!_disposed)
        {
            SetState(PreviewState.Failed);
            Failed?.Invoke(this, new AggregateException("Kadr.MediaHost recovery failed.", reason, exception));
        }
        finally { Interlocked.Exchange(ref _recoveryScheduled, 0); }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is not { IsConnected: true } || _host is not { HasExited: false })
                await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_lastPlan is null) return;
            await ExecuteCommandCoreAsync(
                MediaHostPacketType.Prepare, new MediaHostPrepare(_lastPlan, _lastRequest), cancellationToken)
                .ConfigureAwait(false);
            if (_desiredPlaying)
                await ExecuteCommandCoreAsync(MediaHostPacketType.Start, new { }, cancellationToken).ConfigureAwait(false);
        }
        finally { _connectionGate.Release(); }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_disposed || _stopping || _pipe is not { IsConnected: true }) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { await PingAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception exception) when (!_disposed &&
                    exception is IOException or TimeoutException or OperationCanceledException)
                {
                    if (Interlocked.Exchange(ref _recoveryScheduled, 1) == 0)
                        _ = RecoverBackgroundAsync(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void CloseConnection()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        try { _frameRing?.Dispose(); } catch { }
        _frameRing = null;
        _frameBuffers = [];
    }

    private async Task ReadFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _frameSignals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var ring = _frameRing;
                    var request = Volatile.Read(ref _requestSnapshot);
                    using var lease = ring?.TryAcquireLatest();
                    if (lease is null || lease.Descriptor.Generation != request.Request.Generation.Video) continue;
                    var descriptor = lease.Descriptor;
                    var buffers = _frameBuffers;
                    if ((uint)descriptor.SlotIndex >= (uint)buffers.Length) continue;
                    var pixels = buffers[descriptor.SlotIndex];
                    if (pixels is null || pixels.Length < descriptor.ValidLength)
                    {
                        pixels = new byte[descriptor.ValidLength];
                        buffers[descriptor.SlotIndex] = pixels;
                        Interlocked.Add(ref _frameBufferAllocatedBytes, pixels.Length);
                    }
                    lease.CopyTo(pixels);
                    if (_disposed || !ReferenceEquals(ring, _frameRing)) continue;
                    var position = new TimelineTime(descriptor.TimestampTicks);
                    Interlocked.Exchange(ref request.PositionTicks, position.Ticks);
                    if (!ReferenceEquals(Volatile.Read(ref _requestSnapshot), request)) continue;
                    Interlocked.Increment(ref _framesReceived);
                    Interlocked.Add(ref _pipePayloadBytes, descriptor.ValidLength);
                    _position = position;
                    FramePresented?.Invoke(this, new VideoFrame(
                        position, descriptor.Width, descriptor.Height, descriptor.Stride,
                        pixels.AsMemory(0, descriptor.ValidLength), descriptor.Generation));
                }
                catch (ObjectDisposedException) when (!_disposed) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(exception);
    }

    private void SetState(PreviewState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or EndOfStreamException or TimeoutException or MediaHostException { Recoverable: true };

    private async Task DrainHostErrorsAsync(Process process)
    {
        var tail = new System.Text.StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, count);
                if (tail.Length > Services.ProcessRunner.MaximumErrorCharacters)
                    tail.Remove(0, tail.Length - Services.ProcessRunner.MaximumErrorCharacters);
                Volatile.Write(ref _lastHostErrorTail, tail.ToString());
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        { Trace.TraceWarning("MediaHost stderr drain ended: {0}", exception.Message); }
    }

    private async Task StopHostProcessAsync()
    {
        var process = _host;
        var errors = _hostErrorTask;
        _host = null;
        _hostErrorTask = null;
        if (process is null) return;
        try
        {
            TryKill(process);
            await Task.WhenAll(process.WaitForExitAsync(), errors ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) { Trace.TraceWarning("MediaHost cleanup failed: {0}", exception); }
        finally { process.Dispose(); }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
    }
}

public sealed class MediaHostException(string message, bool recoverable) : Exception(message)
{
    public bool Recoverable { get; } = recoverable;
}
