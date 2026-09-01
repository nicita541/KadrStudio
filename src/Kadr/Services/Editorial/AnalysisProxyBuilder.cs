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
    ProcessRunner processes)
{
    public async Task<AnalysisProxyBundle> BuildAsync(
        MediaSource source,
        CancellationToken cancellationToken)
    {
        if (source.OnlineState != MediaOnlineState.Online || !File.Exists(source.Path))
            throw new FileNotFoundException($"Media source '{source.Name}' is offline.", source.Path);
        ffmpeg.EnsureAvailable();
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
                await RunFfmpegAsync(arguments, cancellationToken).ConfigureAwait(false);
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
                        ], cancellationToken).ConfigureAwait(false);
                        files.Add(new AnalysisProxyFile(
                            audioPath, "audio/flac", "audio-chunk", order, stream.StreamIndex));
                    }
                }
            }
            if (files.Count == 0)
                throw new InvalidOperationException($"No safe analysis proxy could be created for '{source.Name}'.");
            return new AnalysisProxyBundle(directory, files);
        }
        catch
        {
            await new AnalysisProxyBundle(directory, files).DisposeAsync();
            throw;
        }
    }

    private async Task RunFfmpegAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(
            ffmpeg.FfmpegPath, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidDataException("FFmpeg could not create an analysis proxy: " + result.StandardError);
    }
}
