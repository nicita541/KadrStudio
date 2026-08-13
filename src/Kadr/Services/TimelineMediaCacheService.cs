using System.Buffers;
using System.Runtime.InteropServices;
using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class TimelineMediaCacheService(FfmpegLocator locator, ProcessRunner processRunner)
{
    private const double FrameIntervalSeconds = 15;
    private readonly string _frameRoot = ThumbnailService.CreateCacheDirectory("TimelineFrames");
    private readonly string _waveformRoot = ThumbnailService.CreateCacheDirectory("WaveformsV2");

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
            : Task.FromResult(WaveformPyramid.Empty);
        await Task.WhenAll(framesTask, waveformTask);
        asset.TimelineFramePaths = await framesTask;
        asset.Waveform = await waveformTask;
    }

    private async Task<IReadOnlyList<string>> PrepareFramesAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var frameCount = Math.Clamp((int)Math.Ceiling(asset.Duration / FrameIntervalSeconds), 12, 160);
        var directory = Path.Combine(_frameRoot, ThumbnailService.BuildCacheKey(asset.Path) + "-v2");
        Directory.CreateDirectory(directory);
        var paths = Enumerable.Range(0, frameCount).Select(index => Path.Combine(directory, $"{index:00}.jpg")).ToArray();
        if (paths.All(path => File.Exists(path) && new FileInfo(path).Length > 256)) return paths;
        await Parallel.ForEachAsync(Enumerable.Range(0, paths.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                if (File.Exists(paths[index]) && new FileInfo(paths[index]).Length > 256) return;
                var position = Math.Min(Math.Max(0, asset.Duration - 0.01), index * FrameIntervalSeconds + FrameIntervalSeconds / 2);
                await processRunner.RunAsync(locator.FfmpegPath,
                    ["-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "auto", "-ss", Invariant(position),
                     "-i", asset.Path, "-frames:v", "1", "-vf", "scale=192:108:force_original_aspect_ratio=increase,crop=192:108",
                     "-q:v", "5", paths[index]], cancellationToken: token);
            });
        return paths.Where(File.Exists).ToArray();
    }

    private async Task<WaveformPyramid> PrepareWaveformAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_waveformRoot, ThumbnailService.BuildCacheKey(asset.Path) + ".waveform");
        if (File.Exists(cachePath))
        {
            try { return WaveformPyramidCodec.Decode(await File.ReadAllBytesAsync(cachePath, cancellationToken)); }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException) { }
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = locator.FfmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-i", asset.Path, "-vn", "-map", "0:a:0",
            "-ac", "2", "-ar", "48000", "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"
        }) startInfo.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("FFmpeg waveform decoder did not start.");
        using var registration = cancellationToken.Register(() => TryKill(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var builder = new WaveformPyramidBuilder();
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        var carry = 0;
        try
        {
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(
                    buffer.AsMemory(carry, buffer.Length - carry), cancellationToken);
                if (read == 0) break;
                var bytes = carry + read;
                var complete = bytes - bytes % (sizeof(float) * 2);
                builder.AddInterleavedStereo(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, complete)));
                carry = bytes - complete;
                if (carry > 0) buffer.AsSpan(complete, carry).CopyTo(buffer);
            }
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg waveform decode failed: {error.Trim()}");
            var pyramid = builder.Build();
            if (pyramid.IsEmpty) return pyramid;
            Directory.CreateDirectory(_waveformRoot);
            var temporary = cachePath + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, WaveformPyramidCodec.Encode(pyramid), cancellationToken);
            File.Move(temporary, cachePath, overwrite: true);
            return pyramid;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryKill(process);
        }
    }

    private static string Invariant(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static void TryKill(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
