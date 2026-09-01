using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Media;
using KadrStudio.Application.Upscaling;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

public sealed class AnimeSrUpscaleService(
    FfmpegLocator ffmpeg,
    MediaProbeService probe,
    ProcessRunner processRunner,
    AiServerConnection? aiServer = null) : IAiUpscaleService
{
    public const string ModelId = "AnimeSR-X_first_short_run_best-experimental";
    public const string ModelSha256 = "5302034b463cded6497eb2c53e7e6be22d3e4f16bed190d7cfff4415ff81c548";
    private static readonly SemaphoreSlim AcceleratorLease = new(1, 1);

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

        var runtime = ResolveRuntime();
        ffmpeg.EnsureAvailable();
        var outputRoot = Path.Combine(KadrLocalDataPaths.ArtifactsRoot, "Upscale", jobId.ToString("N"));
        Directory.CreateDirectory(outputRoot);
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

        await AcceleratorLease.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new UpscaleProgress(jobId, 0, "Освобождаю VRAM от Vision/Director…"));
            if (aiServer is not null)
                await aiServer.TryReleaseLocalAcceleratorAsync(cancellationToken).ConfigureAwait(false);
            foreach (var track in selectedTracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originals = sourceProject.MediaClips
                    .Where(item => item.TrackId == track.Id && item.Video is not null)
                    .OrderBy(item => item.Start)
                    .ThenBy(item => item.Id)
                    .ToArray();
                var derivedTrack = new TimelineTrack(
                    Guid.NewGuid(), TrackKind.Visual, nextVisualIndex++, $"{track.Name} · AnimeSR-X", IsVisible: false);
                var derivedSources = ImmutableArray.CreateBuilder<MediaSource>(originals.Length);
                var derivedClips = ImmutableArray.CreateBuilder<MediaClip>(originals.Length);
                foreach (var original in originals)
                {
                    var source = sourceProject.Sources[original.SourceId];
                    if (source.Kind != MediaKind.Video)
                        throw new InvalidOperationException("AnimeSR-X не обрабатывает изображения как видеоклипы.");
                    var outputPath = Path.Combine(outputRoot, $"{track.Id:N}-{original.Id:N}.mp4");
                    var clipBaseTicks = completedTicks;
                    await RunPythonAsync(
                        runtime,
                        source.Path,
                        outputPath,
                        original.SourceIn,
                        original.Duration,
                        request.ScaleMode,
                        jobId,
                        (value, frames, totalFrames, eta) => progress?.Report(new UpscaleProgress(
                            jobId,
                            Math.Clamp((clipBaseTicks + (long)(original.Duration.Ticks * value)) / (double)totalTicks, 0, 0.999),
                            $"AnimeSR-X: {track.Name}, кадр {frames}/{totalFrames}",
                            frames,
                            totalFrames,
                            eta)),
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
                }
                var fingerprint = InputFingerprint(sourceProject, track.Id);
                var now = DateTimeOffset.UtcNow;
                results.Add(new UpscaleTrackResult(
                    derivedTrack,
                    derivedSources.ToImmutable(),
                    derivedClips.ToImmutable(),
                    new TrackRenditionGroup(
                        Guid.NewGuid(), sequence.Id, track.Id, derivedTrack.Id,
                        TrackRenditionKind.Original, sequence.Revision, fingerprint,
                        ModelId, ModelSha256, request.ScaleMode, false, now, now)));
            }
        }
        catch
        {
            TryDeleteGeneratedDirectory(outputRoot);
            throw;
        }
        finally
        {
            AcceleratorLease.Release();
        }
        progress?.Report(new UpscaleProgress(jobId, 1, "AnimeSR-X завершён."));
        return new UpscaleResult(jobId, results.ToImmutable());
    }

    public static long EstimateOutputBytes(ProjectState project, IEnumerable<Guid> trackIds, UpscaleScaleMode scale)
    {
        var ids = trackIds.ToHashSet();
        var seconds = project.MediaClips.Where(item => ids.Contains(item.TrackId)).Sum(item => item.Duration.TotalSeconds);
        var multiplier = scale == UpscaleScaleMode.X4 ? 2.2 : 1.4;
        return checked((long)Math.Ceiling(seconds * 1_500_000 * multiplier));
    }

    private async Task RunPythonAsync(
        RuntimePaths runtime,
        string input,
        string output,
        TimelineTime sourceIn,
        TimelineTime duration,
        UpscaleScaleMode scale,
        Guid jobId,
        Action<double, int, int, TimeSpan?> report,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(runtime.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(runtime.Script)!
        };
        foreach (var argument in new[]
        {
            runtime.Script, "--input", input, "--output", output,
            "--model", runtime.Model, "--ffmpeg", ffmpeg.FfmpegPath, "--ffprobe", ffmpeg.FfprobePath,
            "--start-ticks", sourceIn.Ticks.ToString(CultureInfo.InvariantCulture),
            "--duration-ticks", duration.Ticks.ToString(CultureInfo.InvariantCulture),
            "--outscale", scale switch { UpscaleScaleMode.X2 => "2", UpscaleScaleMode.X4 => "4", _ => "auto" },
            "--crf", "14"
        }) start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить AnimeSR-X.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });
        var errors = new StringBuilder();
        var diagnostics = new StringBuilder();
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                errors.AppendLine(line);
        }, cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("KADR_PROGRESS ", StringComparison.Ordinal))
            {
                if (line.StartsWith("KADR_ERROR ", StringComparison.Ordinal)) diagnostics.AppendLine(line);
                continue;
            }
            try
            {
                using var json = JsonDocument.Parse(line[14..]);
                var root = json.RootElement;
                var value = root.GetProperty("progress").GetDouble();
                var frames = root.GetProperty("frames").GetInt32();
                var totalFrames = root.GetProperty("totalFrames").GetInt32();
                var eta = root.GetProperty("etaSeconds").GetDouble();
                report(value, frames, totalFrames, eta > 0 ? TimeSpan.FromSeconds(eta) : null);
            }
            catch (JsonException)
            {
                // A malformed diagnostic line cannot publish a result and is ignored here.
            }
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException(
                $"AnimeSR-X завершился с кодом {process.ExitCode}. " +
                $"{diagnostics}{errors}".Trim());
    }

    private static RuntimePaths ResolveRuntime()
    {
        var workspace = FindWorkspaceRoot(Directory.GetCurrentDirectory()) ?? FindWorkspaceRoot(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Не найден корень Kadr Studio.");
        var aiRoot = Path.Combine(workspace, ".kadr-ai");
        var python = Path.Combine(aiRoot, "workers", "runtime", "Scripts", "python.exe");
        var model = Path.Combine(aiRoot, "models", "animesr-x-experimental", "AnimeSR-X_first_short_run_best.pth");
        var deployedScript = Path.Combine(aiRoot, "workers", "kadr_worker", "animesr_upscale.py");
        var sourceScript = Path.Combine(workspace, "workers", "python", "kadr_worker", "animesr_upscale.py");
        var script = File.Exists(deployedScript) ? deployedScript : sourceScript;
        if (!File.Exists(python) || !File.Exists(model) || !File.Exists(script))
            throw new FileNotFoundException("AnimeSR-X не установлен. Откройте ИИ и установите подтверждённый пакет моделей.");
        return new RuntimePaths(python, script, model);
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

    private static string? FindWorkspaceRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KadrStudio.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
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
        catch
        {
            // Generated partials may still be locked by a terminating encoder; startup cleanup can retry later.
        }
    }

    private sealed record RuntimePaths(string Python, string Script, string Model);
}
