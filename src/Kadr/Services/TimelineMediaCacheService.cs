using System.Globalization;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class TimelineMediaCacheService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private const double FrameIntervalSeconds = 15;
    private const int WaveformSampleCount = 12000;
    private readonly string _frameRoot = ThumbnailService.CreateCacheDirectory("TimelineFrames");
    private readonly string _waveformRoot = ThumbnailService.CreateCacheDirectory("Waveforms");

    public async Task PrepareAsync(MediaAsset asset, CancellationToken cancellationToken = default)
    {
        if (asset.Kind == MediaKind.Image)
        {
            asset.TimelineFramePaths = [asset.Path];
            return;
        }

        locator.EnsureAvailable();
        var framesTask = asset.Kind == MediaKind.Video
            ? PrepareFramesAsync(asset, cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        var waveformTask = asset.HasAudio
            ? PrepareWaveformAsync(asset, cancellationToken)
            : Task.FromResult<WaveformCache?>(null);

        await Task.WhenAll(framesTask, waveformTask);
        asset.TimelineFramePaths = await framesTask;
        var waveform = await waveformTask;
        asset.WaveformPath = waveform?.ImagePath;
        asset.WaveformPeaks = waveform?.Peaks ?? Array.Empty<float>();
    }

    private async Task<IReadOnlyList<string>> PrepareFramesAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var frameCount = Math.Clamp((int)Math.Ceiling(asset.Duration / FrameIntervalSeconds), 12, 160);
        var directory = Path.Combine(_frameRoot, ThumbnailService.BuildCacheKey(asset.Path) + "-v2");
        Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(0, frameCount)
            .Select(index => Path.Combine(directory, $"{index:00}.jpg"))
            .ToArray();
        if (paths.All(path => File.Exists(path) && new FileInfo(path).Length > 256))
        {
            return paths;
        }

        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                if (File.Exists(paths[index]) && new FileInfo(paths[index]).Length > 256)
                {
                    return;
                }
                var position = Math.Min(Math.Max(0, asset.Duration - 0.01), index * FrameIntervalSeconds + FrameIntervalSeconds / 2);
                await processRunner.RunAsync(locator.FfmpegPath,
                    [
                        "-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "auto",
                        "-ss", Format(position), "-i", asset.Path,
                        "-frames:v", "1", "-vf", "scale=192:108:force_original_aspect_ratio=increase,crop=192:108",
                        "-q:v", "5", paths[index]
                    ], cancellationToken: token);
            });
        return paths.Where(File.Exists).ToArray();
    }

    private async Task<WaveformCache?> PrepareWaveformAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var key = ThumbnailService.BuildCacheKey(asset.Path);
        var peakPath = Path.Combine(_waveformRoot, key + "-v4.peaks");
        if (File.Exists(peakPath))
        {
            var cachedPeaks = await ReadPeaksAsync(peakPath, cancellationToken);
            if (cachedPeaks.Count > 0) return new WaveformCache(null, cachedPeaks);
        }
        var sampleRate = Math.Clamp((int)Math.Ceiling(WaveformSampleCount * 64d / Math.Max(1, asset.Duration)), 500, 4000);
        var rawPath = peakPath + $"-{Guid.NewGuid():N}.raw";
        try
        {
            var peakResult = await processRunner.RunAsync(locator.FfmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y", "-i", asset.Path,
                    "-vn", "-map", "0:a:0", "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
                    "-c:a", "pcm_f32le", "-f", "f32le", rawPath
                ], cancellationToken: cancellationToken);
            if (peakResult.ExitCode != 0 || !File.Exists(rawPath)) return null;
            var samples = await File.ReadAllBytesAsync(rawPath, cancellationToken);
            var rawSamples = new float[samples.Length / sizeof(float)];
            Buffer.BlockCopy(samples, 0, rawSamples, 0, rawSamples.Length * sizeof(float));
            var peakCount = Math.Min(WaveformSampleCount, rawSamples.Length);
            var peaks = new float[peakCount];
            for (var index = 0; index < peakCount; index++)
            {
                var from = (int)((long)index * rawSamples.Length / peakCount);
                var to = Math.Max(from + 1, (int)((long)(index + 1) * rawSamples.Length / peakCount));
                double sumSquares = 0;
                for (var sample = from; sample < to; sample++) sumSquares += rawSamples[sample] * rawSamples[sample];
                peaks[index] = (float)Math.Sqrt(sumSquares / (to - from));
            }
            var normalization = peaks.OrderBy(value => value).ElementAt(Math.Clamp((int)(peaks.Length * 0.97), 0, peaks.Length - 1));
            normalization = Math.Max(0.02f, normalization);
            for (var index = 0; index < peaks.Length; index++)
                peaks[index] = Math.Clamp((float)Math.Sqrt(peaks[index] / normalization), 0.025f, 1);
            await WritePeaksAsync(peakPath, peaks, cancellationToken);
            return new WaveformCache(null, peaks);
        }
        finally
        {
            try { if (File.Exists(rawPath)) File.Delete(rawPath); } catch { }
        }
    }

    private static async Task WritePeaksAsync(string path, IReadOnlyList<float> peaks, CancellationToken cancellationToken)
    {
        var bytes = new byte[peaks.Count * sizeof(float)];
        for (var index = 0; index < peaks.Count; index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), peaks[index]);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private static async Task<IReadOnlyList<float>> ReadPeaksAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) return Array.Empty<float>();
        var result = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    public static IReadOnlyList<float> AggregateVisiblePeaks(
        IReadOnlyList<float> peaks,
        double sourceStartRatio,
        double sourceEndRatio,
        int columnCount)
    {
        if (peaks.Count == 0 || columnCount <= 0) return Array.Empty<float>();
        sourceStartRatio = Math.Clamp(sourceStartRatio, 0, 1);
        sourceEndRatio = Math.Clamp(sourceEndRatio, sourceStartRatio, 1);
        var result = new float[columnCount];
        for (var column = 0; column < columnCount; column++)
        {
            var fromRatio = sourceStartRatio + (sourceEndRatio - sourceStartRatio) * column / columnCount;
            var toRatio = sourceStartRatio + (sourceEndRatio - sourceStartRatio) * (column + 1) / columnCount;
            var from = Math.Clamp((int)Math.Floor(fromRatio * peaks.Count), 0, peaks.Count - 1);
            var to = Math.Clamp((int)Math.Ceiling(toRatio * peaks.Count), from + 1, peaks.Count);
            for (var index = from; index < to; index++) result[column] = Math.Max(result[column], peaks[index]);
        }
        return result;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record WaveformCache(string? ImagePath, IReadOnlyList<float> Peaks);
}
