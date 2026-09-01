using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Preview;

public static class MediaHostProtocol
{
    public const int Version = 1;
    public const int MaximumHeaderBytes = 64 * 1024 * 1024;
    public const int MaximumPayloadBytes = 256 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public enum MediaHostPacketType : byte
{
    Hello = 1,
    HelloAccepted = 2,
    Prepare = 3,
    UpdatePlan = 4,
    Start = 5,
    Seek = 6,
    Pause = 7,
    Stop = 8,
    Shutdown = 9,
    Acknowledged = 10,
    StateChanged = 11,
    VideoFrame = 12,
    AudioMeter = 13,
    Position = 14,
    Failure = 15,
    Ping = 16,
    Pong = 17,
    Diagnostics = 18,
    DiagnosticsResult = 19
}

public sealed record MediaHostPacket(
    MediaHostPacketType Type,
    Guid CorrelationId,
    ReadOnlyMemory<byte> Header,
    ReadOnlyMemory<byte> Payload)
{
    public T ReadHeader<T>()
        => JsonSerializer.Deserialize<T>(Header.Span, MediaHostProtocol.JsonOptions)
           ?? throw new InvalidDataException($"MediaHost packet {Type} has an empty header.");

    public static MediaHostPacket Create<T>(
        MediaHostPacketType type,
        T header,
        Guid correlationId = default,
        ReadOnlyMemory<byte> payload = default)
        => new(type, correlationId,
            JsonSerializer.SerializeToUtf8Bytes(header, MediaHostProtocol.JsonOptions), payload);

    public static MediaHostPacket Empty(MediaHostPacketType type, Guid correlationId = default)
        => new(type, correlationId, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty);
}

public sealed record MediaHostHello(int ProtocolVersion, int ProcessId);
public sealed record MediaHostPrepare(RenderPlan Plan, PreviewRequest Request);
public sealed record MediaHostUpdatePlan(
    RenderPlan Plan,
    PreviewRequest Request,
    bool RestartVideo,
    bool RestartAudio);
public sealed record MediaHostSeek(TimelineTime Position);
public sealed record MediaHostState(PreviewState State, TimelineTime Position);
public sealed record MediaHostFrameHeader(
    TimelineTime Position,
    int Width,
    int Height,
    int Stride,
    long Generation);
public sealed record MediaHostAudioMeterHeader(
    AudioMeterLevel Level,
    TimelineTime Position,
    long Generation);
public sealed record MediaHostFailure(string Pipeline, string Message, bool Recoverable);
public sealed record MediaHostDiagnostics(
    int ProcessId,
    int ActiveVideoWorkers,
    int ActiveAudioWorkers,
    int PeakVideoWorkers,
    int PeakAudioWorkers,
    long StartedVideoWorkers,
    long StartedAudioWorkers);

public static class MediaHostPacketIO
{
    private const int PrefixLength = 1 + 16 + 4 + 4;

    public static async ValueTask WriteAsync(
        Stream stream,
        MediaHostPacket packet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (packet.Header.Length > MediaHostProtocol.MaximumHeaderBytes)
            throw new InvalidDataException("MediaHost header exceeds the protocol limit.");
        if (packet.Payload.Length > MediaHostProtocol.MaximumPayloadBytes)
            throw new InvalidDataException("MediaHost payload exceeds the protocol limit.");
        var prefix = new byte[PrefixLength];
        prefix[0] = (byte)packet.Type;
        packet.CorrelationId.TryWriteBytes(prefix.AsSpan(1, 16));
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(17, 4), packet.Header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(21, 4), packet.Payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (!packet.Header.IsEmpty) await stream.WriteAsync(packet.Header, cancellationToken).ConfigureAwait(false);
        if (!packet.Payload.IsEmpty) await stream.WriteAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<MediaHostPacket?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[PrefixLength];
        if (!await ReadExactlyAsync(stream, prefix, allowEndOfStream: true, cancellationToken).ConfigureAwait(false))
            return null;
        var type = (MediaHostPacketType)prefix[0];
        if (!Enum.IsDefined(type)) throw new InvalidDataException($"Unknown MediaHost packet type {(byte)type}.");
        var correlation = new Guid(prefix.AsSpan(1, 16));
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(17, 4));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(21, 4));
        if (headerLength is < 0 or > MediaHostProtocol.MaximumHeaderBytes)
            throw new InvalidDataException("Invalid MediaHost header length.");
        if (payloadLength is < 0 or > MediaHostProtocol.MaximumPayloadBytes)
            throw new InvalidDataException("Invalid MediaHost payload length.");
        var header = new byte[headerLength];
        var payload = new byte[payloadLength];
        if (headerLength > 0)
            await ReadExactlyAsync(stream, header, allowEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (payloadLength > 0)
            await ReadExactlyAsync(stream, payload, allowEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return new MediaHostPacket(type, correlation, header, payload);
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowEndOfStream,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowEndOfStream && offset == 0) return false;
                throw new EndOfStreamException("MediaHost packet ended before its declared length.");
            }
            offset += read;
        }
        return true;
    }
}
