using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Caching;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

public sealed class RecurringSectionFingerprintService(
    FfmpegLocator locator,
    ProcessRunner processRunner,
    IArtifactStore artifacts)
{
    private const int FormatVersion = 1;

    public async Task<RecurringSectionFingerprint> CreateAsync(
        MediaSource source,
        TimeRange range,
        CancellationToken cancellationToken = default)
    {
        var fingerprint = string.Join('|',
            MontagePlanValidator.StableFingerprint(source),
            AiMontageAnalysisService.PipelineVersion,
            range.Start.Ticks,
            range.Duration.Ticks);
        var key = new MediaCacheKey(
            source.Id, fingerprint, MediaArtifactKind.SceneFingerprint, 0, range.Start.Ticks, FormatVersion);
        var cached = await artifacts.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is { } payload)
        {
            var restored = JsonSerializer.Deserialize<RecurringSectionFingerprint>(payload.Span);
            if (restored is not null) return restored;
        }

        locator.EnsureAvailable();
        var root = Path.Combine(KadrLocalDataPaths.TempRoot, "recurring-section-fingerprint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var videoPath = Path.Combine(root, "video.gray");
        var audioPath = Path.Combine(root, "audio.pcm");
        try
        {
            var video = await processRunner.RunAsync(
                locator.FfmpegPath,
                [
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                    "-ss", Invariant(range.Start.TotalSeconds), "-t", Invariant(range.Duration.TotalSeconds),
                    "-i", source.Path,
                    "-vf", "fps=1,scale=9:8:flags=area,format=gray",
                    "-an", "-f", "rawvideo", videoPath
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (video.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Не удалось построить визуальный отпечаток: {FfmpegOutput.LastMeaningfulLine(video.StandardError)}");

            if (source.HasAudio)
            {
                var audio = await processRunner.RunAsync(
                    locator.FfmpegPath,
                    [
                        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                        "-ss", Invariant(range.Start.TotalSeconds), "-t", Invariant(range.Duration.TotalSeconds),
                        "-i", source.Path,
                        "-vn", "-ac", "1", "-ar", "1000", "-f", "s16le", audioPath
                    ],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (audio.ExitCode != 0) TryDelete(audioPath);
            }

            var result = new RecurringSectionFingerprint(
                BuildVisualHashes(await File.ReadAllBytesAsync(videoPath, cancellationToken).ConfigureAwait(false)),
                File.Exists(audioPath)
                    ? BuildAudioEnvelope(await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false))
                    : []);
            await artifacts.PutAsync(key, JsonSerializer.SerializeToUtf8Bytes(result), cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            TryDelete(videoPath);
            TryDelete(audioPath);
            try { Directory.Delete(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static double Similarity(RecurringSectionFingerprint left, RecurringSectionFingerprint right)
    {
        var visual = SequenceSimilarity(left.VisualHashes, right.VisualHashes, static (a, b) =>
            1 - BitOperations.PopCount(a ^ b) / 64d, maximumOffset: 5);
        var audio = SequenceSimilarity(left.AudioEnvelope, right.AudioEnvelope, static (a, b) =>
            1 - Math.Abs(a - b) / 255d, maximumOffset: 10);
        if (left.AudioEnvelope.IsDefaultOrEmpty || right.AudioEnvelope.IsDefaultOrEmpty) return visual;
        if (left.VisualHashes.IsDefaultOrEmpty || right.VisualHashes.IsDefaultOrEmpty) return audio;
        return visual * 0.62 + audio * 0.38;
    }

    private static ImmutableArray<ulong> BuildVisualHashes(byte[] bytes)
    {
        const int frameSize = 9 * 8;
        var output = ImmutableArray.CreateBuilder<ulong>(bytes.Length / frameSize);
        for (var offset = 0; offset + frameSize <= bytes.Length; offset += frameSize)
        {
            ulong hash = 0;
            var bit = 0;
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (bytes[offset + y * 9 + x] < bytes[offset + y * 9 + x + 1]) hash |= 1UL << bit;
            output.Add(hash);
        }
        return output.ToImmutable();
    }

    private static ImmutableArray<byte> BuildAudioEnvelope(byte[] bytes)
    {
        const int samplesPerWindow = 500;
        var samples = bytes.Length / sizeof(short);
        var values = new List<double>(Math.Max(1, samples / samplesPerWindow));
        for (var start = 0; start < samples; start += samplesPerWindow)
        {
            var end = Math.Min(samples, start + samplesPerWindow);
            double total = 0;
            for (var index = start; index < end; index++)
            {
                var sample = BitConverter.ToInt16(bytes, index * sizeof(short));
                total += Math.Abs(sample / 32768d);
            }
            values.Add(total / Math.Max(1, end - start));
        }
        var maximum = values.DefaultIfEmpty(0).Max();
        return maximum <= 0
            ? []
            : values.Select(value => (byte)Math.Clamp(Math.Round(value / maximum * 255), 0, 255)).ToImmutableArray();
    }

    private static double SequenceSimilarity<T>(
        ImmutableArray<T> left,
        ImmutableArray<T> right,
        Func<T, T, double> score,
        int maximumOffset)
    {
        if (left.IsDefaultOrEmpty || right.IsDefaultOrEmpty) return 0;
        var best = 0d;
        for (var offset = -maximumOffset; offset <= maximumOffset; offset++)
        {
            var leftStart = Math.Max(0, -offset);
            var rightStart = Math.Max(0, offset);
            var count = Math.Min(left.Length - leftStart, right.Length - rightStart);
            if (count < Math.Min(8, Math.Min(left.Length, right.Length))) continue;
            double total = 0;
            for (var index = 0; index < count; index++)
                total += score(left[leftStart + index], right[rightStart + index]);
            best = Math.Max(best, total / count);
        }
        return best;
    }

    private static string Invariant(double value)
        => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed record RecurringSectionFingerprint(
    ImmutableArray<ulong> VisualHashes,
    ImmutableArray<byte> AudioEnvelope);
