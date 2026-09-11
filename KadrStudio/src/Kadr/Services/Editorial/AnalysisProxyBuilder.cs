using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    // Published cache entries outlive consumers. Only the builder owns its staging files.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class AnalysisProxyBuilder(
    FfmpegLocator ffmpeg,
    ProcessRunner processes,
    IEditorialTelemetrySink? telemetry = null)
{
    private const string ProxyFormatVersion = "analysis-proxy-v3-1280x720-x264-crf24-flac16k-sha256";
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
        var cached = await FindCacheAsync(source, cacheDirectory, cancellationToken).ConfigureAwait(false);
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
        EnsureNoReparsePoints(directory);
        System.IO.Directory.CreateDirectory(directory);
        var files = new List<AnalysisProxyFile>();
        var stagingFiles = new List<string>();
        try
        {
            if (source.Kind is MediaKind.Video or MediaKind.Image)
            {
                var visualPath = Path.Combine(directory, source.Kind == MediaKind.Image ? "visual.jpg" : "visual.mp4");
                stagingFiles.Add(visualPath);
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
                        stagingFiles.Add(audioPath);
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
            var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                await using var input = File.OpenRead(file.Path);
                checksums.Add(Path.GetFileName(file.Path), Convert.ToHexString(
                    await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)));
            }
            stagingFiles.Add(Path.Combine(directory, "checksums.json"));
            stagingFiles.Add(Path.Combine(directory, ".complete"));
            await File.WriteAllTextAsync(Path.Combine(directory, "checksums.json"),
                JsonSerializer.Serialize(checksums), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(directory, ".complete"), ProxyFormatVersion, cancellationToken).ConfigureAwait(false);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
            // Immutable generations avoid replacing an entry held by another analysis and
            // never delete unknown contents of an invalid/legacy cache directory.
            cacheDirectory += "-" + Guid.NewGuid().ToString("N");
            EnsureNoReparsePoints(directory);
            EnsureNoReparsePoints(Path.GetDirectoryName(cacheDirectory)!);
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.Directory.Move(directory, cacheDirectory);
            var result = await TryOpenCacheAsync(source, cacheDirectory, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Completed analysis proxy cache could not be reopened.");
            Record("proxy_preparation_duration_ms", Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds,
                ("cache", "miss"));
            return result;
        }
        catch
        {
            CleanupStaging(directory, stagingFiles);
            throw;
        }
    }

    private static void CleanupStaging(string directory, IEnumerable<string> files)
    {
        try
        {
            EnsureNoReparsePoints(directory);
            foreach (var file in files)
            {
                EnsureNoReparsePoints(file);
                File.Delete(file);
            }
            // Unknown contents prevent removal; never recursively delete a staging tree.
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Analysis staging cleanup incomplete: {0}", error.GetType().Name);
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

    private static async Task<AnalysisProxyBundle?> FindCacheAsync(
        MediaSource source, string keyPath, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(keyPath)!;
        if (!System.IO.Directory.Exists(parent)) return null;
        EnsureNoReparsePoints(parent);
        foreach (var candidate in System.IO.Directory.EnumerateDirectories(parent, Path.GetFileName(keyPath) + "-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = Path.GetFileName(candidate)[(Path.GetFileName(keyPath).Length + 1)..];
            if (!Guid.TryParseExact(suffix, "N", out _)) continue;
            var result = await TryOpenCacheAsync(source, candidate, cancellationToken).ConfigureAwait(false);
            if (result is not null) return result;
        }
        return null;
    }

    private static async Task<AnalysisProxyBundle?> TryOpenCacheAsync(
        MediaSource source, string directory, CancellationToken cancellationToken)
    {
        try
        {
            EnsureNoReparsePoints(directory);
            return await ReadCacheAsync(source, directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<AnalysisProxyBundle?> ReadCacheAsync(
        MediaSource source, string directory, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(directory, ".complete");
        EnsureNoReparsePoints(marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 256 ||
            !string.Equals(File.ReadAllText(marker), ProxyFormatVersion, StringComparison.Ordinal)) return null;
        var manifestPath = Path.Combine(directory, "checksums.json");
        EnsureNoReparsePoints(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1024 * 1024) return null;
        var checksums = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        if (checksums is null) return null;
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
        if (files.Count == 0 || checksums.Count != files.Count) return null;
        foreach (var file in files)
        {
            EnsureNoReparsePoints(file.Path);
            if (!checksums.TryGetValue(Path.GetFileName(file.Path), out var expected)) return null;
            await using var input = File.OpenRead(file.Path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, expected, StringComparison.Ordinal)) return null;
        }
        return new AnalysisProxyBundle(directory, files);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Analysis cache cannot traverse a reparse point.");
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
