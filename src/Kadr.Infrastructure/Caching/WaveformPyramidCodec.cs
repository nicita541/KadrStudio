using System.Collections.Immutable;
using KadrStudio.Application.Caching;

namespace KadrStudio.Infrastructure.Caching;

public static class WaveformPyramidCodec
{
    private const int Version = 2;

    public static byte[] Encode(WaveformPyramid pyramid)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(0x325641574452414BUL); // KADRWAV2
        writer.Write(Version); writer.Write(pyramid.SampleRate); writer.Write(pyramid.Channels);
        writer.Write(pyramid.SourceFrameCount); writer.Write(pyramid.Levels.Length);
        foreach (var level in pyramid.Levels)
        {
            writer.Write(level.FramesPerPeak); writer.Write(level.Peaks.Length);
            foreach (var peak in level.Peaks)
            {
                writer.Write(peak.MinimumLeft); writer.Write(peak.MaximumLeft); writer.Write(peak.RmsLeft);
                writer.Write(peak.MinimumRight); writer.Write(peak.MaximumRight); writer.Write(peak.RmsRight);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    public static WaveformPyramid Decode(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt64() != 0x325641574452414BUL || reader.ReadInt32() != Version)
            throw new InvalidDataException("Unsupported waveform cache format.");
        var sampleRate = reader.ReadInt32(); var channels = reader.ReadInt32();
        var sourceFrames = reader.ReadInt64(); var levelCount = reader.ReadInt32();
        if (sampleRate <= 0 || channels != 2 || sourceFrames < 0 || levelCount is < 1 or > 32)
            throw new InvalidDataException("Invalid waveform cache header.");
        var levels = ImmutableArray.CreateBuilder<WaveformLevel>(levelCount);
        for (var levelIndex = 0; levelIndex < levelCount; levelIndex++)
        {
            var frames = reader.ReadInt32(); var count = reader.ReadInt32();
            if (frames <= 0 || count is < 1 or > 20_000_000) throw new InvalidDataException("Invalid waveform level.");
            var peaks = ImmutableArray.CreateBuilder<WaveformPeak>(count);
            for (var index = 0; index < count; index++)
                peaks.Add(new WaveformPeak(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
            levels.Add(new WaveformLevel(frames, peaks.MoveToImmutable()));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Waveform cache has trailing data.");
        return new WaveformPyramid(sampleRate, channels, sourceFrames, levels.MoveToImmutable());
    }
}
