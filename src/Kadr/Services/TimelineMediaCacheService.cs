using System.Globalization;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class TimelineMediaCacheService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private const double FrameIntervalSeconds = 15;
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
            : Task.FromResult<string?>(null);

        await Task.WhenAll(framesTask, waveformTask);
        asset.TimelineFramePaths = await framesTask;
        asset.WaveformPath = await waveformTask;
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

    private async Task<string?> PrepareWaveformAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var output = Path.Combine(_waveformRoot, ThumbnailService.BuildCacheKey(asset.Path) + "-v2.png");
        if (File.Exists(output) && new FileInfo(output).Length > 512)
        {
            return output;
        }

        var filter =
            "[0:a:0]aformat=channel_layouts=mono," +
            "showwavespic=s=2400x192:colors=0x9AF3C7:draw=full," +
            "crop=2400:96:0:0,format=rgba,colorkey=0x000000:0.05:0.0[wave]";
        var result = await processRunner.RunAsync(locator.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-i", asset.Path,
                "-filter_complex", filter, "-map", "[wave]", "-frames:v", "1", output
            ], cancellationToken: cancellationToken);
        return result.ExitCode == 0 && File.Exists(output) ? output : null;
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
