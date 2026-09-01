using System.IO.Pipes;
using System.Threading.Channels;
using KadrStudio.Application.Preview;

namespace KadrStudio.MediaHost;

public sealed class MediaHostServer(string pipeName, string ffmpegPath) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HostPlaybackSession _playback = new(ffmpegPath);
    private readonly Channel<MediaHostPacket> _events = Channel.CreateBounded<MediaHostPacket>(
        new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private NamedPipeServerStream? _pipe;
    private Task? _eventWriter;
    private bool _disposed;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        _playback.StateChanged += Playback_StateChanged;
        _playback.FramePresented += Playback_FramePresented;
        _playback.AudioMeterUpdated += Playback_AudioMeterUpdated;
        _playback.Failed += Playback_Failed;
        await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        _eventWriter = WriteEventsAsync(_events.Reader, cancellationToken);

        while (!cancellationToken.IsCancellationRequested && _pipe.IsConnected)
        {
            var packet = await MediaHostPacketIO.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
            if (packet is null) break;
            if (!await HandleAsync(packet, cancellationToken).ConfigureAwait(false)) break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _playback.StateChanged -= Playback_StateChanged;
        _playback.FramePresented -= Playback_FramePresented;
        _playback.AudioMeterUpdated -= Playback_AudioMeterUpdated;
        _playback.Failed -= Playback_Failed;
        await _playback.DisposeAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        if (_eventWriter is not null)
            try { await _eventWriter.ConfigureAwait(false); } catch { }
        _pipe?.Dispose();
        _writeGate.Dispose();
    }

    private async Task<bool> HandleAsync(MediaHostPacket packet, CancellationToken cancellationToken)
    {
        try
        {
            switch (packet.Type)
            {
                case MediaHostPacketType.Hello:
                    var hello = packet.ReadHeader<MediaHostHello>();
                    if (hello.ProtocolVersion != MediaHostProtocol.Version)
                        throw new InvalidDataException(
                            $"MediaHost protocol {hello.ProtocolVersion} is incompatible with {MediaHostProtocol.Version}.");
                    await WriteAsync(MediaHostPacket.Create(
                        MediaHostPacketType.HelloAccepted,
                        new MediaHostHello(MediaHostProtocol.Version, Environment.ProcessId),
                        packet.CorrelationId), cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Prepare:
                    var prepare = packet.ReadHeader<MediaHostPrepare>();
                    await _playback.PrepareAsync(prepare.Plan, prepare.Request, cancellationToken).ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.UpdatePlan:
                    var update = packet.ReadHeader<MediaHostUpdatePlan>();
                    await _playback.UpdatePlanAsync(
                        update.Plan, update.Request, update.RestartVideo, update.RestartAudio, cancellationToken)
                        .ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Start:
                    await _playback.StartAsync(cancellationToken).ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Seek:
                    await _playback.SeekAsync(packet.ReadHeader<MediaHostSeek>().Position, cancellationToken)
                        .ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Pause:
                    await _playback.PauseAsync(cancellationToken).ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Stop:
                    await _playback.StopAsync(cancellationToken).ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Ping:
                    await WriteAsync(MediaHostPacket.Empty(MediaHostPacketType.Pong, packet.CorrelationId), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Diagnostics:
                    await WriteAsync(MediaHostPacket.Create(
                        MediaHostPacketType.DiagnosticsResult, _playback.Diagnostics, packet.CorrelationId),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case MediaHostPacketType.Shutdown:
                    await _playback.StopAsync(cancellationToken).ConfigureAwait(false);
                    await AcknowledgeAsync(packet, cancellationToken).ConfigureAwait(false);
                    return false;
                default:
                    throw new InvalidDataException($"Packet {packet.Type} is not a client command.");
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteAsync(MediaHostPacket.Create(
                MediaHostPacketType.Failure,
                new MediaHostFailure("host", exception.Message, Recoverable: true),
                packet.CorrelationId), cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private Task AcknowledgeAsync(MediaHostPacket packet, CancellationToken cancellationToken)
        => WriteAsync(MediaHostPacket.Create(
            MediaHostPacketType.Acknowledged,
            new MediaHostState(_playback.State, _playback.Position), packet.CorrelationId), cancellationToken);

    private void Playback_StateChanged(object? sender, PreviewState state)
        => QueueCritical(MediaHostPacket.Create(
            MediaHostPacketType.StateChanged, new MediaHostState(state, _playback.Position)));

    private void Playback_FramePresented(object? sender, VideoFrame frame)
        => Queue(MediaHostPacket.Create(
            MediaHostPacketType.VideoFrame,
            new MediaHostFrameHeader(frame.Position, frame.Width, frame.Height, frame.Stride, frame.Generation),
            payload: frame.Bgra));

    private void Playback_AudioMeterUpdated(object? sender, AudioMeterLevel level)
        => Queue(MediaHostPacket.Create(
            MediaHostPacketType.AudioMeter,
            new MediaHostAudioMeterHeader(level, _playback.Position, _playback.AudioGeneration)));

    private void Playback_Failed(object? sender, Exception exception)
        => QueueCritical(MediaHostPacket.Create(
            MediaHostPacketType.Failure,
            new MediaHostFailure("pipeline", exception.Message, Recoverable: true)));

    private void Queue(MediaHostPacket packet)
        => _events.Writer.TryWrite(packet);

    private void QueueCritical(MediaHostPacket packet)
        => _ = WriteCriticalAsync(packet);

    private async Task WriteCriticalAsync(MediaHostPacket packet)
    {
        try { await WriteAsync(packet, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private async Task WriteEventsAsync(
        ChannelReader<MediaHostPacket> events,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task WriteAsync(MediaHostPacket packet, CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected) return;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await MediaHostPacketIO.WriteAsync(pipe, packet, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
}
