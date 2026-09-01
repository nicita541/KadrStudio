using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Application.Media;
using KadrStudio.Application.Upscaling;
using KadrStudio.Core.Domain;
using KadrStudio.Services.Editorial;

namespace KadrStudio.Services;

/// <summary>
/// Desktop adapter for the remote-capable AnimeSR pipeline. The editor only
/// prepares a bounded asset and applies the downloaded derivative; Python,
/// CUDA and model weights are owned by Kadr AI Server.
/// </summary>
public sealed class AnimeSrUpscaleService(
    FfmpegLocator ffmpeg,
    MediaProbeService probe,
    ProcessRunner processRunner,
    AiServerConnection aiServer) : IAiUpscaleService
{
    private readonly AiServerV2Client _client = new(aiServer);

    public async Task<UpscaleComparisonFrames> CreateComparisonFramesAsync(
        ProjectState project,
        Guid renditionGroupId,
        TimelineTime timelineTime,
        CancellationToken cancellationToken = default)
    {
        var group = project.RenditionGroups.FirstOrDefault(item => item.Id == renditionGroupId)
            ?? throw new InvalidOperationException("Связь оригинальной и апскейл-дорожки не найдена.");
        if (group.IsStale) throw new InvalidOperationException("Апскейл устарел после изменения оригинала. Запустите обработку повторно.");
        var original = project.MediaClips.FirstOrDefault(clip => clip.TrackId == group.OriginalTrackId &&
            clip.Start <= timelineTime && clip.End > timelineTime)
            ?? throw new InvalidOperationException("На текущей позиции нет оригинального видеоклипа.");
        var upscaled = project.MediaClips.FirstOrDefault(clip => clip.TrackId == group.UpscaledTrackId &&
            clip.Start <= timelineTime && clip.End > timelineTime)
            ?? throw new InvalidOperationException("На текущей позиции нет производного видеоклипа.");
        var originalSource = project.Sources[original.SourceId];
        var upscaledSource = project.Sources[upscaled.SourceId];
        var originalTime = original.SourceIn + (timelineTime - original.Start);
        var upscaledTime = upscaled.SourceIn + (timelineTime - upscaled.Start);
        ffmpeg.EnsureAvailable();
        var directory = Path.Combine(KadrLocalDataPaths.TempRoot, "UpscaleCompare", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var originalFrame = Path.Combine(directory, "original.png");
        var upscaledFrame = Path.Combine(directory, "upscaled.png");
        try
        {
            await Task.WhenAll(
                ExtractFrameAsync(originalSource.Path, originalTime, originalFrame, cancellationToken),
                ExtractFrameAsync(upscaledSource.Path, upscaledTime, upscaledFrame, cancellationToken)).ConfigureAwait(false);
            return new UpscaleComparisonFrames(originalFrame, upscaledFrame, directory);
        }
        catch
        {
            DeleteComparisonDirectory(directory);
            throw;
        }
    }

    private async Task ExtractFrameAsync(string input, TimelineTime time, string output, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(ffmpeg.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-i", input, "-ss", time.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
             "-frames:v", "1", "-update", "1", "-y", output], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("Не удалось извлечь кадр сравнения: " + result.StandardError.Trim());
    }

    public static void DeleteComparisonDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var root = Path.GetFullPath(Path.Combine(KadrLocalDataPaths.TempRoot, "UpscaleCompare"));
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return;
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    public async Task<UpscaleResult> UpscaleAsync(
        ProjectState project,
        Guid jobId,
        UpscaleRequest request,
        IProgress<UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID is required.", nameof(jobId));
        var sequence = project.FindSequence(request.SequenceId)
            ?? throw new InvalidOperationException("Последовательность для AnimeSR-X не найдена.");
        if (request.VisualTrackIds.IsDefaultOrEmpty || request.VisualTrackIds.Distinct().Count() != request.VisualTrackIds.Length)
            throw new InvalidOperationException("Выберите хотя бы одну уникальную видеодорожку.");
        var selectedTracks = request.VisualTrackIds.Select(id => sequence.Tracks.FirstOrDefault(item => item.Id == id)
                ?? throw new InvalidOperationException("Выбранная дорожка не найдена."))
            .ToArray();
        if (selectedTracks.Any(item => item.Kind != TrackKind.Visual))
            throw new InvalidOperationException("AnimeSR-X обрабатывает только видеодорожки.");

        ffmpeg.EnsureAvailable();
        var outputRoot = Path.Combine(KadrLocalDataPaths.ArtifactsRoot, "Upscale", jobId.ToString("N"));
        var uploadRoot = Path.Combine(KadrLocalDataPaths.TempRoot, "UpscaleUpload", jobId.ToString("N"));
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(uploadRoot);
        var sourceProject = project.ActiveSequenceId == sequence.Id ? project : project.ActivateSequence(sequence.Id);
        var allClips = selectedTracks.SelectMany(track => sourceProject.MediaClips
                .Where(clip => clip.TrackId == track.Id && clip.Video is not null))
            .ToArray();
        if (allClips.Length == 0)
            throw new InvalidOperationException("На выбранных дорожках нет видеоклипов.");
        var totalTicks = allClips.Sum(item => item.Duration.Ticks);
        var completedTicks = 0L;
        var results = ImmutableArray.CreateBuilder<UpscaleTrackResult>(selectedTracks.Length);
        var nextVisualIndex = sourceProject.Tracks.Where(item => item.Kind == TrackKind.Visual)
            .Select(item => item.Index).DefaultIfEmpty(-1).Max() + 1;

        try
        {
            foreach (var track in selectedTracks)
            {
                var originals = sourceProject.MediaClips
                    .Where(item => item.TrackId == track.Id && item.Video is not null)
                    .OrderBy(item => item.Start)
                    .ThenBy(item => item.Id)
                    .ToArray();
                var derivedTrack = new TimelineTrack(
                    Guid.NewGuid(), TrackKind.Visual, nextVisualIndex++, $"{track.Name} · AnimeSR-X", IsVisible: false);
                var derivedSources = ImmutableArray.CreateBuilder<MediaSource>(originals.Length);
                var derivedClips = ImmutableArray.CreateBuilder<MediaClip>(originals.Length);
                string? modelId = null;
                string? modelSha256 = null;
                foreach (var original in originals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sourceProject.Sources[original.SourceId];
                    if (source.Kind != MediaKind.Video)
                        throw new InvalidOperationException("AnimeSR-X не обрабатывает изображения как видеоклипы.");
                    var proxyPath = Path.Combine(uploadRoot, $"{track.Id:N}-{original.Id:N}.mp4");
                    var outputPath = Path.Combine(outputRoot, $"{track.Id:N}-{original.Id:N}.mp4");
                    var clipBaseTicks = completedTicks;
                    progress?.Report(new UpscaleProgress(
                        jobId, clipBaseTicks / (double)totalTicks,
                        $"Подготовка защищённого клипа для сервера: {track.Name}"));
                    await CreateBoundedProxyAsync(source.Path, original, proxyPath, cancellationToken).ConfigureAwait(false);
                    var assetId = await _client.UploadAssetAsync(
                        proxyPath, "video/mp4",
                        new Progress<double>(value => ReportStage(progress, jobId, track.Name, "Загрузка", clipBaseTicks,
                            original.Duration.Ticks, totalTicks, value * 0.15)),
                        cancellationToken).ConfigureAwait(false);
                    var serverJobId = await _client.StartJobAsync(
                        "anime-upscale", "1", [assetId],
                        new
                        {
                            durationTicks = original.Duration.Ticks,
                            scaleMode = request.ScaleMode switch
                            {
                                UpscaleScaleMode.X2 => "x2",
                                UpscaleScaleMode.X4 => "x4",
                                _ => "auto"
                            },
                            crf = 14,
                            assets = new[] { new { id = assetId, kind = "video", order = 0, startTicks = 0 } }
                        }, cancellationToken).ConfigureAwait(false);
                    AiServerJob completed;
                    try
                    {
                        completed = await _client.WaitForJobAsync(
                            serverJobId,
                            new Progress<double>(value => ReportStage(progress, jobId, track.Name, "Обработка на сервере",
                                clipBaseTicks, original.Duration.Ticks, totalTicks, 0.15 + value * 0.75)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { await _client.CancelJobAsync(serverJobId).ConfigureAwait(false); } catch { }
                        throw;
                    }
                    if (completed.ArtifactIds.Length < 2)
                        throw new InvalidDataException("Kadr AI Server не вернул видео и metadata AnimeSR-X.");
                    using var metadata = await _client.GetArtifactAsync(
                        completed.ArtifactIds[^1], cancellationToken).ConfigureAwait(false);
                    var metadataRoot = metadata.RootElement;
                    var outputArtifactId = metadataRoot.GetProperty("outputArtifactId").GetString()
                        ?? throw new InvalidDataException("Kadr AI Server не вернул ID видео AnimeSR-X.");
                    modelId = metadataRoot.GetProperty("modelId").GetString();
                    modelSha256 = metadataRoot.GetProperty("modelSha256").GetString();
                    await _client.DownloadArtifactAsync(
                        outputArtifactId, outputPath,
                        new Progress<double>(value => ReportStage(progress, jobId, track.Name, "Скачивание",
                            clipBaseTicks, original.Duration.Ticks, totalTicks, 0.90 + value * 0.10)),
                        cancellationToken).ConfigureAwait(false);
                    var media = await probe.ProbeAsync(outputPath, verifyContent: true, cancellationToken).ConfigureAwait(false);
                    var tolerance = (source.FrameRate ?? sequence.Settings.FrameRate).FrameDuration;
                    if (media.Kind != MediaKind.Video || media.Streams.Any(item => item.Kind == MediaStreamKind.Audio) ||
                        Distance(media.Duration, original.Duration) > tolerance)
                        throw new InvalidDataException("Производный файл AnimeSR-X не прошёл проверку длительности или video-only режима.");
                    var video = media.Streams.First(item => item.Kind == MediaStreamKind.Video);
                    var derivedSource = new MediaSource(
                        Guid.NewGuid(), media.Path, Path.GetFileName(media.Path), MediaKind.Video,
                        original.Duration, false, media.Width, media.Height, media.FrameRate,
                        video.Codec, string.Empty, media.Fingerprint.Length, media.Fingerprint.LastWriteUtcTicks,
                        media.Fingerprint.FastHash, FastFingerprint: media.Fingerprint.FastHash,
                        VerifiedFingerprint: media.Fingerprint.VerifiedHash ?? string.Empty,
                        Streams: media.Streams, IsVariableFrameRate: media.IsVariableFrameRate);
                    derivedSources.Add(derivedSource);
                    derivedClips.Add(new MediaClip(
                        Guid.NewGuid(), derivedSource.Id, derivedTrack.Id,
                        original.Start, TimelineTime.Zero, original.Duration,
                        Video: original.Video, StreamIndex: video.StreamIndex));
                    completedTicks += original.Duration.Ticks;
                    TryDeleteFile(proxyPath);
                }
                if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(modelSha256))
                    throw new InvalidDataException("Kadr AI Server не вернул идентичность модели AnimeSR-X.");
                var fingerprint = InputFingerprint(sourceProject, track.Id);
                var now = DateTimeOffset.UtcNow;
                results.Add(new UpscaleTrackResult(
                    derivedTrack,
                    derivedSources.ToImmutable(),
                    derivedClips.ToImmutable(),
                    new TrackRenditionGroup(
                        Guid.NewGuid(), sequence.Id, track.Id, derivedTrack.Id,
                        TrackRenditionKind.Original, sequence.Revision, fingerprint,
                        modelId, modelSha256, request.ScaleMode, false, now, now)));
            }
        }
        catch
        {
            TryDeleteGeneratedDirectory(outputRoot);
            throw;
        }
        finally
        {
            TryDeleteUploadDirectory(uploadRoot);
        }
        progress?.Report(new UpscaleProgress(jobId, 1, "AnimeSR-X завершён на Kadr AI Server."));
        return new UpscaleResult(jobId, results.ToImmutable());
    }

    private static void ReportStage(
        IProgress<UpscaleProgress>? progress, Guid jobId, string trackName, string stage,
        long clipBaseTicks, long clipTicks, long totalTicks, double clipProgress)
        => progress?.Report(new UpscaleProgress(
            jobId,
            Math.Clamp((clipBaseTicks + (long)(clipTicks * clipProgress)) / (double)totalTicks, 0, 0.999),
            $"{stage}: {trackName}"));

    private async Task CreateBoundedProxyAsync(
        string input, MediaClip clip, string output, CancellationToken cancellationToken)
    {
        var stream = clip.StreamIndex ?? 0;
        var result = await processRunner.RunAsync(ffmpeg.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-i", input,
                "-ss", clip.SourceIn.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                "-t", clip.Duration.TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
                "-map", $"0:{stream}", "-an", "-sn", "-dn",
                "-c:v", "libx264", "-preset", "fast", "-crf", "12", "-pix_fmt", "yuv420p", output
            ], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("Не удалось подготовить ограниченный video-asset: " + result.StandardError.Trim());
    }

    public static long EstimateOutputBytes(ProjectState project, IEnumerable<Guid> trackIds, UpscaleScaleMode scale)
    {
        var ids = trackIds.ToHashSet();
        var seconds = project.MediaClips.Where(item => ids.Contains(item.TrackId)).Sum(item => item.Duration.TotalSeconds);
        var multiplier = scale == UpscaleScaleMode.X4 ? 2.2 : 1.4;
        return checked((long)Math.Ceiling(seconds * 1_500_000 * multiplier));
    }

    private static string InputFingerprint(ProjectState project, Guid trackId)
    {
        var canonical = string.Join('|', project.MediaClips
            .Where(item => item.TrackId == trackId)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .Select(item => $"{item.Id:N}:{MediaSourceFingerprint.Stable(project.Sources[item.SourceId])}:" +
                            $"{item.Start.Ticks}:{item.SourceIn.Ticks}:{item.Duration.Ticks}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static TimelineTime Distance(TimelineTime left, TimelineTime right)
        => left >= right ? left - right : right - left;

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteUploadDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Path.Combine(KadrLocalDataPaths.TempRoot, "UpscaleUpload"));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch { }
    }

    private static void TryDeleteGeneratedDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(Path.Combine(KadrLocalDataPaths.ArtifactsRoot, "Upscale"));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch { }
    }
}
