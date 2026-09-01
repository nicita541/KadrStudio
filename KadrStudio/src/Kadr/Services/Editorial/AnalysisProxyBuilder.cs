using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Editorial;

public sealed record AnalysisProxyFile(
    string Path,
    string MediaType,
    string Kind,
    int ChunkOrder,
    int? StreamIndex = null);

public sealed class AnalysisProxyBundle(string directory, IReadOnlyList<AnalysisProxyFile> files) : IAsyncDisposable
{
    public string Directory { get; } = directory;
    public IReadOnlyList<AnalysisProxyFile> Files { get; } = files;

    public ValueTask DisposeAsync()
    {
        try
        {
            var root = Path.GetFullPath(KadrLocalDataPaths.TempRoot) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Directory);
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(target))
                System.IO.Directory.Delete(target, recursive: true);
        }
        catch
        {
            // Disposable analysis proxies are recoverable and can be cleaned on next startup.
        }
        return ValueTask.CompletedTask;
    }
}

public sealed class AnalysisProxyBuilder(
    FfmpegLocator ffmpeg,
    ProcessRunner processes,
    IEditorialTelemetrySink? telemetry = null)
{
    private const string ProxyFormatVersion = "analysis-proxy-v2-1280x720-x264-crf24-flac16k";
    private readonly IEditorialTelemetrySink _telemetry = telemetry ?? NullEditorialTelemetrySink.Instance;

    public async Task<AnalysisProxyBundle> BuildAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        var totalStarted = Stopwatch.GetTimestamp();
        if (source.OnlineState != MediaOnlineState.Online || !File.Exists(source.Path))
            throw new FileNotFoundException($"Media source '{source.Name}' is offline.", source.Path);
        ffmpeg.EnsureAvailable();
        var cacheDirectory = Path.Combine(
            KadrLocalDataPaths.CacheRoot, "analysis-v2", BuildCacheKey(source));
        var cached = TryOpenCache(source, cacheDirectory);
        if (cached is not null)
        {
            Record("proxy_cache_hit", 1);
            Record("proxy_preparation_duration_ms", Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds,
                ("cache", "hit"));
            return cached;
        }
        Record("proxy_cache_miss", 1);
        var directory = Path.Combine(
            KadrLocalDataPaths.TempRoot, "analysis-v2", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var files = new List<AnalysisProxyFile>();
        try
        {
            if (source.Kind is MediaKind.Video or MediaKind.Image)
            {
                var visualPath = Path.Combine(directory, source.Kind == MediaKind.Image ? "visual.jpg" : "visual.mp4");
                var arguments = source.Kind == MediaKind.Image
                    ? new[]
                    {
                        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                        "-i", source.Path,
                        "-vf", "scale=1280:720:force_original_aspect_ratio=decrease",
                        "-frames:v", "1", "-q:v", "3", visualPath
                    }
                    : new[]
                    {
                        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                        "-i", source.Path,
                        "-map", "0:v:0", "-an",
                        "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2:black,setsar=1",
                        "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "veryfast",
                        "-crf", "24", "-g", "12", "-keyint_min", "1", "-sc_threshold", "0",
                        "-pix_fmt", "yuv420p", "-movflags", "+faststart", visualPath
                    };
                await RunFfmpegAsync(arguments, "visual_proxy", cancellationToken).ConfigureAwait(false);
                Record("proxy_output_bytes", new FileInfo(visualPath).Length, ("kind", "visual_proxy"));
                files.Add(new AnalysisProxyFile(
                    visualPath,
                    source.Kind == MediaKind.Image ? "image/jpeg" : "video/mp4",
                    "visual-proxy",
                    0,
                    source.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Video)?.StreamIndex));
            }

            if (source.HasAudio || source.Kind == MediaKind.Audio)
            {
                var audioStreams = source.Streams.Where(item => item.Kind == MediaStreamKind.Audio).ToArray();
                if (audioStreams.Length == 0)
                    audioStreams = [new MediaStreamDescriptor(0, MediaStreamKind.Audio, "unknown")];
                foreach (var stream in audioStreams)
                {
                    const double chunkSeconds = 600d;
                    var chunkCount = Math.Max(1, (int)Math.Ceiling(source.Duration.TotalSeconds / chunkSeconds));
                    for (var order = 0; order < chunkCount; order++)
                    {
                        var startSeconds = order * chunkSeconds;
                        var durationSeconds = Math.Min(chunkSeconds, source.Duration.TotalSeconds - startSeconds);
                        if (durationSeconds <= 0) break;
                        var audioPath = Path.Combine(directory, $"audio-s{stream.StreamIndex:D3}-{order:D4}.flac");
                        await RunFfmpegAsync(
                        [
                            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                            "-ss", startSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                            "-i", source.Path,
                            "-t", durationSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                            "-map", source.Streams.IsDefaultOrEmpty ? "0:a:0" : $"0:{stream.StreamIndex}",
                            "-vn", "-ac", "1", "-ar", "16000", "-c:a", "flac", audioPath
                        ], "audio_proxy", cancellationToken).ConfigureAwait(false);
                        Record("proxy_output_bytes", new FileInfo(audioPath).Length, ("kind", "audio_proxy"));
                        files.Add(new AnalysisProxyFile(
                            audioPath, "audio/flac", "audio-chunk", order, stream.StreamIndex));
                    }
                }
            }
            if (files.Count == 0)
                throw new InvalidOperationException($"No safe analysis proxy could be created for '{source.Name}'.");
            await File.WriteAllTextAsync(
                Path.Combine(directory, ".complete"), ProxyFormatVersion, cancellationToken).ConfigureAwait(false);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
            try
            {
                System.IO.Directory.Move(directory, cacheDirectory);
            }
            catch (IOException) when (System.IO.Directory.Exists(cacheDirectory))
            {
                await new AnalysisProxyBundle(directory, files).DisposeAsync();
            }
            var result = TryOpenCache(source, cacheDirectory)
                ?? throw new InvalidDataException("Completed analysis proxy cache could not be reopened.");
            Record("proxy_preparation_duration_ms", Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds,
                ("cache", "miss"));
            return result;
        }
        catch
        {
            await new AnalysisProxyBundle(directory, files).DisposeAsync();
            throw;
        }
    }

    private static string BuildCacheKey(MediaSource source)
    {
        var file = new FileInfo(source.Path);
        var streams = string.Join(',', source.Streams
            .OrderBy(item => item.StreamIndex)
            .Select(item => $"{item.Kind}:{item.StreamIndex}"));
        var identity = string.Join('|',
            ProxyFormatVersion,
            Path.GetFullPath(source.Path).ToUpperInvariant(),
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            source.FastFingerprint,
            source.VerifiedFingerprint,
            source.Duration.Ticks,
            source.Kind,
            streams);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static AnalysisProxyBundle? TryOpenCache(MediaSource source, string directory)
    {
        var marker = Path.Combine(directory, ".complete");
        if (!File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker), ProxyFormatVersion, StringComparison.Ordinal)) return null;
        var files = new List<AnalysisProxyFile>();
        if (source.Kind is MediaKind.Video or MediaKind.Image)
        {
            var path = Path.Combine(directory, source.Kind == MediaKind.Image ? "visual.jpg" : "visual.mp4");
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            files.Add(new AnalysisProxyFile(
                path,
                source.Kind == MediaKind.Image ? "image/jpeg" : "video/mp4",
                "visual-proxy",
                0,
                source.Streams.FirstOrDefault(item => item.Kind == MediaStreamKind.Video)?.StreamIndex));
        }
        if (source.HasAudio || source.Kind == MediaKind.Audio)
        {
            var audioStreams = source.Streams.Where(item => item.Kind == MediaStreamKind.Audio).ToArray();
            if (audioStreams.Length == 0)
                audioStreams = [new MediaStreamDescriptor(0, MediaStreamKind.Audio, "unknown")];
            foreach (var stream in audioStreams)
            {
                var chunkCount = Math.Max(1, (int)Math.Ceiling(source.Duration.TotalSeconds / 600d));
                for (var order = 0; order < chunkCount; order++)
                {
                    var path = Path.Combine(directory, $"audio-s{stream.StreamIndex:D3}-{order:D4}.flac");
                    if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
                    files.Add(new AnalysisProxyFile(path, "audio/flac", "audio-chunk", order, stream.StreamIndex));
                }
            }
        }
        return files.Count == 0 ? null : new AnalysisProxyBundle(directory, files);
    }

    private async Task RunFfmpegAsync(
        IEnumerable<string> arguments,
        string stage,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await processes.RunAsync(
            ffmpeg.FfmpegPath, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        Record("proxy_ffmpeg_duration_ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ("stage", stage));
        if (result.ExitCode != 0)
            throw new InvalidDataException("FFmpeg could not create an analysis proxy: " + result.StandardError);
    }

    private void Record(string metric, double value, params (string Key, string Value)[] dimensions)
    {
        try
        {
            _telemetry.Record(new EditorialTelemetryEvent(
                DateTimeOffset.UtcNow,
                Guid.Empty,
                metric,
                value,
                dimensions.ToImmutableDictionary(item => item.Key, item => item.Value)));
        }
        catch
        {
            // Local diagnostics must never fail proxy preparation.
        }
    }
}
