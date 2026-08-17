using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using KadrStudio.Application.Preview;
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
    private RenderPlan? _lastPlan;
    private PreviewRequest _lastRequest;
    private bool _desiredPlaying;
    private bool _stopping;
    private bool _disposed;
    private int _recoveryScheduled;
    private TimelineTime _position;

    public PreviewState State { get; private set; } = PreviewState.Idle;
    public TimelineTime Position => _position;
    public int HostProcessId => _host is { HasExited: false } ? _host.Id : 0;
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

    public async Task SeekAsync(TimelineTime position, CancellationToken cancellationToken = default)
    {
        _position = position;
        _lastRequest = _lastRequest with { Position = position };
        await ExecuteCommandAsync(MediaHostPacketType.Seek, new MediaHostSeek(position), cancellationToken)
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
        return response.ReadHeader<MediaHostDiagnostics>();
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
            TryKill(_host);
            _host?.Dispose();
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
        TryKill(_host);
        _host?.Dispose();
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
        _ = DrainHostErrorsAsync(_host);
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        _readerTask = ReadLoopAsync(_pipe, _lifetime.Token);
        var hello = MediaHostPacket.Create(MediaHostPacketType.Hello,
            new MediaHostHello(MediaHostProtocol.Version, Environment.ProcessId), Guid.NewGuid());
        var response = await SendAndWaitAsync(hello, cancellationToken).ConfigureAwait(false);
        if (response.Type != MediaHostPacketType.HelloAccepted ||
            response.ReadHeader<MediaHostHello>().ProtocolVersion != MediaHostProtocol.Version)
            throw new InvalidDataException("Kadr.MediaHost handshake failed.");
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
                var packet = await MediaHostPacketIO.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
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
                var frame = packet.ReadHeader<MediaHostFrameHeader>();
                if (frame.Generation == _lastRequest.Generation.Video)
                {
                    _position = frame.Position;
                    _lastRequest = _lastRequest with { Position = frame.Position };
                    FramePresented?.Invoke(this, new VideoFrame(
                        frame.Position, frame.Width, frame.Height, frame.Stride, packet.Payload, frame.Generation));
                }
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

    private static async Task DrainHostErrorsAsync(Process process)
    {
        try { await process.StandardError.ReadToEndAsync().ConfigureAwait(false); } catch { }
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
