using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KadrStudio.Models;
using KadrStudio.Services;

Console.OutputEncoding = Encoding.UTF8;
var ffmpegLocator = new FfmpegLocator();
var processRunner = new ProcessRunner();
await using var renderCoordinator = new TimelineRenderCoordinator(ffmpegLocator);
using var ollamaService = new OllamaVideoAnalysisService(ffmpegLocator, processRunner);
if (args is ["--models"])
{
    var availableModels = await ollamaService.GetModelsAsync();
    foreach (var availableModel in availableModels)
    {
        Console.WriteLine(availableModel.DisplayName);
    }
    return availableModels.Count > 0 ? 0 : 1;
}

if (args is ["--make-empty-project", var emptyProjectOutput])
{
    var project = EditorProject.CreateNew();
    project.Name = "Runtime smoke";
    await new ProjectService().SaveAsync(project, Path.GetFullPath(emptyProjectOutput));
    Console.WriteLine($"EMPTY_PROJECT_OK {Path.GetFullPath(emptyProjectOutput)}");
    return 0;
}

if (args is ["--timeline-cache-smoke", var cacheInput] && File.Exists(cacheInput))
{
    var probe = new MediaProbeService(ffmpegLocator, processRunner);
    var cacheAsset = await probe.ProbeAsync(Path.GetFullPath(cacheInput));
    var service = new TimelineMediaCacheService(ffmpegLocator, processRunner);
    var started = System.Diagnostics.Stopwatch.StartNew();
    await service.PrepareAsync(cacheAsset);
    if (cacheAsset.TimelineFramePaths.Count < 8 || cacheAsset.TimelineFramePaths.Any(path => !File.Exists(path)) ||
        cacheAsset.Waveform.IsEmpty || cacheAsset.Waveform.Levels[0].Count < 1000 ||
        cacheAsset.Waveform.Levels[0].Peaks.All(value => value.MaximumLeft <= 0 && value.MaximumRight <= 0))
    {
        throw new InvalidOperationException("Кадры или реальная форма волны таймлайна не созданы.");
    }
    Console.WriteLine($"TIMELINE_CACHE_SMOKE_OK frames={cacheAsset.TimelineFramePaths.Count} waveformPeaks={cacheAsset.Waveform.Levels[0].Count} in {started.Elapsed.TotalSeconds:0.0}s");
    return 0;
}

if (args is ["--waveform-zoom-smoke"])
{
    var builder = new KadrStudio.Infrastructure.Caching.WaveformPyramidBuilder(48_000, 2);
    var samples = Enumerable.Range(0, 2000)
        .SelectMany(index => new[] { index % 100 / 100f, -(index % 73) / 73f }).ToArray();
    builder.AddInterleavedStereo(samples);
    var pyramid = builder.Build();
    var full = pyramid.ReadColumns(0, 1, 100);
    var tenPercent = pyramid.ReadColumns(0.4, 0.5, 100);
    if (full.Length != 100 || tenPercent.Length != 100 || full.SequenceEqual(tenPercent))
        throw new InvalidOperationException("Waveform не меняет детализацию вместе с масштабом.");
    Console.WriteLine("WAVEFORM_ZOOM_SMOKE_OK full=100 visible10percent=100");
    return 0;
}

if (args is ["--subtitle-smoke", var subtitleInput] && File.Exists(subtitleInput))
{
    var probe = new MediaProbeService(ffmpegLocator, processRunner);
    var subtitleAsset = await probe.ProbeAsync(Path.GetFullPath(subtitleInput));
    var service = new AutoSubtitleService(ffmpegLocator, processRunner);
    var result = await service.TranscribeLocalAsync(subtitleAsset, 0, subtitleAsset.Duration);
    if (result.Cues.Count == 0 || result.Cues.Any(cue => cue.End <= cue.Start))
    {
        throw new InvalidOperationException("Локальные субтитры не извлечены.");
    }
    Console.WriteLine($"SUBTITLE_SMOKE_OK cues={result.Cues.Count} engine={result.Engine}");
    return 0;
}

if (args is ["--boundary-smoke"])
{
    var directory = Path.Combine(Path.GetTempPath(), "KadrStudio", "boundary-smoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var input = Path.Combine(directory, "cuts.mp4");
    try
    {
        var make = await processRunner.RunAsync(ffmpegLocator.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=red:s=640x360:r=25:d=2",
             "-f", "lavfi", "-i", "color=blue:s=640x360:r=25:d=2", "-f", "lavfi", "-i", "color=green:s=640x360:r=25:d=2",
             "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", input]);
        if (make.ExitCode != 0) throw new InvalidOperationException(make.StandardError);
        var probe = new MediaProbeService(ffmpegLocator, processRunner);
        var boundaryAsset = await probe.ProbeAsync(input);
        var analysis = new VideoAnalysisService(ffmpegLocator, processRunner);
        var boundaryBaseline = new VideoAnalysisResult("known cuts", 0, 6,
            [new DetectedVideoRange(MarkerKind.Opening, 1.65, 2.7, "Опенинг", "грубая зона", 0.6)]);
        var refined = await analysis.RefineSemanticBoundariesAsync(boundaryAsset, boundaryBaseline);
        var range = refined.Ranges.Single();
        if (Math.Abs(range.SourceStart - 2) > 1d / 25 + 0.001 || Math.Abs(range.SourceStart + range.Duration - 4) > 1d / 25 + 0.001)
        {
            throw new InvalidOperationException($"Покадровая граница неверна: {range.SourceStart:0.###}–{range.SourceStart + range.Duration:0.###}");
        }
        Console.WriteLine($"BOUNDARY_SMOKE_OK {range.SourceStart:0.###}-{range.SourceStart + range.Duration:0.###} {range.Description}");
        return 0;
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

if (args is ["--final-structure-smoke", var finalInput] && File.Exists(finalInput))
{
    var probe = new MediaProbeService(ffmpegLocator, processRunner);
    var finalAsset = await probe.ProbeAsync(Path.GetFullPath(finalInput));
    var fallbackEnding = new DetectedVideoRange(
        MarkerKind.Ending, Math.Max(0, finalAsset.Duration - 110), 106,
        "Эндинг", "Грубая граница vision-модели.", 0.72);
    var finalBaseline = new VideoAnalysisResult(
        "final structure smoke", 0, finalAsset.Duration, [fallbackEnding]);
    var finalRanges = await ollamaService.InferFinalRangesFromEmbeddedTextAsync(
        finalAsset, finalBaseline, fallbackEnding);
    var boundaryService = new VideoAnalysisService(ffmpegLocator, processRunner);
    var refinedFinal = await boundaryService.RefineSemanticBoundariesAsync(
        finalAsset, finalBaseline with { Ranges = finalRanges });
    var ordered = refinedFinal.Ranges.OrderBy(range => range.SourceStart).ToList();
    if (ordered.Count < 3 ||
        ordered[0].Kind != MarkerKind.Ending ||
        ordered[1].Kind != MarkerKind.PostCredits ||
        ordered[2].Kind != MarkerKind.Preview ||
        ordered.Zip(ordered.Skip(1), (left, right) => left.SourceStart + left.Duration <= right.SourceStart + 0.001).Any(valid => !valid))
    {
        throw new InvalidOperationException("Финальные блоки не разделены в правильном порядке.");
    }
    Console.WriteLine("FINAL_STRUCTURE_SMOKE_OK " + string.Join(" | ", ordered.Select(range =>
        $"{range.Kind}={TimeSpan.FromSeconds(range.SourceStart):mm\\:ss\\.fff}–{TimeSpan.FromSeconds(range.SourceStart + range.Duration):mm\\:ss\\.fff}")));
    return 0;
}

if (args is ["--make-ui-project", var uiInput, var uiOutput] && File.Exists(uiInput))
{
    var probe = new MediaProbeService(ffmpegLocator, processRunner);
    var uiAsset = await probe.ProbeAsync(Path.GetFullPath(uiInput));
    var uiProject = EditorProject.CreateNew();
    uiProject.Name = "Проверка нового таймлайна";
    uiProject.Media.Add(uiAsset);
    var link = Guid.NewGuid();
    uiProject.Clips.Add(new TimelineClip
    {
        AssetId = uiAsset.Id, Track = TrackKind.Visual, TrackIndex = 0, LinkGroupId = link,
        Start = 0, SourceStart = 0, Duration = uiAsset.Duration
    });
    uiProject.Clips.Add(new TimelineClip
    {
        AssetId = uiAsset.Id, Track = TrackKind.Audio, TrackIndex = 0, LinkGroupId = link,
        Start = 0, SourceStart = 0, Duration = uiAsset.Duration
    });
    uiProject.TextOverlays.Add(new TextOverlay
    {
        Start = 15, Duration = 12, Text = "Текст можно двигать и растягивать", X = 0.5, Y = 0.78
    });
    await new ProjectService().SaveAsync(uiProject, Path.GetFullPath(uiOutput));
    Console.WriteLine($"UI_PROJECT_OK {Path.GetFullPath(uiOutput)}");
    return 0;
}

if (args is ["--history-smoke"])
{
    var testRoot = Path.Combine(Path.GetTempPath(), "KadrStudio", "history-smoke", Guid.NewGuid().ToString("N"));
    try
    {
        var history = new ProjectHistoryService(testRoot);
        var project = EditorProject.CreateNew();
        project.Name = "До изменения";
        var checkpoint = await history.CreateCheckpointAsync(project, "Первая версия");
        project.Name = "После изменения";
        var entries = await history.GetCheckpointsAsync(project);
        if (entries.Count != 1 || entries[0].Id != checkpoint.Id)
        {
            throw new InvalidOperationException("Контрольная точка не появилась в истории.");
        }

        var restored = await history.RestoreCheckpointAsync(entries[0], null);
        if (restored.Name != "До изменения" || restored.Id != project.Id)
        {
            throw new InvalidOperationException("Восстановленный снимок не совпадает с исходным проектом.");
        }

        await history.DeleteCheckpointAsync(entries[0]);
        if ((await history.GetCheckpointsAsync(project)).Count != 0)
        {
            throw new InvalidOperationException("Контрольная точка не удалилась.");
        }

        Console.WriteLine("HISTORY_SMOKE_OK");
        return 0;
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}

if (args is ["--preview-composition-smoke"])
{
    var testRoot = Path.Combine(Path.GetTempPath(), "KadrStudio", "preview-composition-smoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        var basePath = Path.Combine(testRoot, "base.mp4");
        var topPath = Path.Combine(testRoot, "top.mp4");
        var baseResult = await processRunner.RunAsync(ffmpegLocator.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=0x25334f:s=320x180:r=15:d=36",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=36",
             "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", basePath]);
        var topResult = await processRunner.RunAsync(ffmpegLocator.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-y",
             "-f", "lavfi", "-i", "color=0x9955dd:s=160x90:r=15:d=8",
             "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", topPath]);
        if (baseResult.ExitCode != 0 || topResult.ExitCode != 0)
            throw new InvalidOperationException("Не удалось создать исходники проверки предпросмотра.");

        var probe = new MediaProbeService(ffmpegLocator, processRunner);
        var baseAsset = await probe.ProbeAsync(basePath);
        var topAsset = await probe.ProbeAsync(topPath);
        var project = EditorProject.CreateNew();
        project.Media.Add(baseAsset);
        project.Media.Add(topAsset);
        project.Clips.Add(new TimelineClip
        {
            AssetId = baseAsset.Id, Track = TrackKind.Visual, TrackIndex = 0,
            Start = 0, SourceStart = 0, Duration = 36
        });
        project.Clips.Add(new TimelineClip
        {
            AssetId = topAsset.Id, Track = TrackKind.Visual, TrackIndex = 1,
            Start = 4, SourceStart = 0, Duration = 8
        });
        var audioClip = new TimelineClip
        {
            AssetId = baseAsset.Id, Track = TrackKind.Audio, TrackIndex = 0,
            Start = 0, SourceStart = 0, Duration = 36, Volume = 0.8, Pan = -0.15
        };
        project.Clips.Add(audioClip);

        var composition = new PreviewCompositionService(
            ffmpegLocator, processRunner, renderCoordinator, Path.Combine(testRoot, "preview-cache"));
        using var previewSession = new TimelinePreviewSession(composition);
        var initialVideoSignature = composition.GetVideoSignature(project, halfQuality: true);
        var initialAudioSignature = composition.GetAudioSignature(project);
        audioClip.Volume = 0.45;
        if (composition.GetVideoSignature(project, halfQuality: true) != initialVideoSignature ||
            composition.GetAudioSignature(project) == initialAudioSignature)
            throw new InvalidOperationException("Громкость аудио ошибочно инвалидирует видеокэш.");
        var changedAudioSignature = composition.GetAudioSignature(project);
        project.GetVisualClips()[0].Brightness = 0.08;
        if (composition.GetVideoSignature(project, halfQuality: true) == initialVideoSignature ||
            composition.GetAudioSignature(project) != changedAudioSignature)
            throw new InvalidOperationException("Цветокоррекция видео ошибочно инвалидирует аудиокэш.");
        var audioBeforeCanvasChange = composition.GetAudioSignature(project);
        project.CanvasWidth = 1280;
        project.CanvasHeight = 720;
        if (composition.GetAudioSignature(project) != audioBeforeCanvasChange)
            throw new InvalidOperationException("Размер видеокадра ошибочно инвалидирует аудиокэш.");

        var firstTasks = new[]
        {
            composition.EnsureVideoSegmentAsync(project, 14.8, halfQuality: true),
            composition.EnsureVideoSegmentAsync(project, 15.2, halfQuality: true)
        };
        var audioTasks = new[]
        {
            composition.EnsureAudioSegmentAsync(project, 14.8),
            composition.EnsureAudioSegmentAsync(project, 15.2)
        };
        await Task.WhenAll(firstTasks.Concat<Task>(audioTasks));
        var videoSegments = await Task.WhenAll(firstTasks);
        var audioSegments = await Task.WhenAll(audioTasks);
        if (videoSegments.Select(segment => segment.Path).Distinct().Count() != 2 ||
            audioSegments.Select(segment => segment.Path).Distinct().Count() != 2 ||
            !videoSegments[0].Contains(14.8) || !videoSegments[1].Contains(15.2) ||
            !audioSegments[0].Contains(14.8) || !audioSegments[1].Contains(15.2))
            throw new InvalidOperationException("Сегменты на границе 15 секунд сформированы неверно.");

        foreach (var segment in videoSegments)
        {
            var media = await probe.ProbeAsync(segment.Path);
            if (media.Kind != MediaKind.Video || media.HasAudio || media.Width != 640 || media.Height != 360)
                throw new InvalidOperationException("V-дорожки должны давать только видео 640x360 без аудио.");
        }
        foreach (var segment in audioSegments)
        {
            var media = await probe.ProbeAsync(segment.Path);
            if (media.Kind != MediaKind.Audio || !media.HasAudio)
                throw new InvalidOperationException("A-дорожки должны давать только аудио без видеопотока.");
        }

        var still = await composition.EnsureStillFrameAsync(project, 6.5, halfQuality: true);
        if (!File.Exists(still.Path) || new FileInfo(still.Path).Length < 512)
            throw new InvalidOperationException("Композитный точный кадр не создан.");
        var sessionVideo = await previewSession.EnsureVideoAsync(project, 14.8, halfQuality: true);
        var cachedSessionVideo = previewSession.TryGetVideo(project, 14.8, halfQuality: true);
        if (cachedSessionVideo?.Path != sessionVideo.Path)
            throw new InvalidOperationException("Сессия не вернула готовый видеосегмент из своего поколения.");
        project.InvalidatePreview(TrackKind.Audio);
        if (previewSession.TryGetVideo(project, 14.8, halfQuality: true)?.Path != sessionVideo.Path)
            throw new InvalidOperationException("Инвалидация аудио ошибочно сбросила поколение видео.");

        var staleVideoJob = previewSession.EnsureVideoAsync(project, 30.2, halfQuality: true);
        var independentAudioJob = previewSession.EnsureAudioAsync(project, 30.2);
        project.GetVisualClips()[0].Contrast = 1.15;
        project.InvalidatePreview(TrackKind.Visual);
        var currentVideoJob = previewSession.EnsureVideoAsync(project, 30.2, halfQuality: true);
        try
        {
            await staleVideoJob;
            throw new InvalidOperationException("Устаревшее поколение видео смогло попасть в текущую сессию.");
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(currentVideoJob, independentAudioJob);
        if (!previewSession.IsCurrentVideo(project, halfQuality: true, currentVideoJob.Result.Signature))
            throw new InvalidOperationException("Сессия не приняла актуальное поколение видеокэша.");

        await using (var revisionViewModel = new KadrStudio.ViewModels.MainViewModel())
        {
            var revisionAsset = new MediaAsset
            {
                Path = basePath, Name = "revision.mp4", Kind = MediaKind.Video,
                Duration = 36, HasAudio = true, FileSizeBytes = new FileInfo(basePath).Length
            };
            revisionViewModel.Project.Media.Add(revisionAsset);
            var revisionVideo = new TimelineClip
            {
                AssetId = revisionAsset.Id, Track = TrackKind.Visual, TrackIndex = 0,
                Start = 0, SourceStart = 0, Duration = 10
            };
            var revisionAudio = new TimelineClip
            {
                AssetId = revisionAsset.Id, Track = TrackKind.Audio, TrackIndex = 0,
                Start = 0, SourceStart = 0, Duration = 10
            };
            revisionViewModel.Project.Clips.Add(revisionVideo);
            revisionViewModel.Project.Clips.Add(revisionAudio);
            var videoRevision = revisionViewModel.Project.VideoRevision;
            var audioRevision = revisionViewModel.Project.AudioRevision;
            revisionAudio.Volume = 0.3;
            if (revisionViewModel.Project.VideoRevision != videoRevision ||
                revisionViewModel.Project.AudioRevision <= audioRevision)
                throw new InvalidOperationException("Изменение A-дорожки затронуло ревизию V-дорожек.");
            videoRevision = revisionViewModel.Project.VideoRevision;
            audioRevision = revisionViewModel.Project.AudioRevision;
            revisionVideo.Saturation = 0.7;
            if (revisionViewModel.Project.VideoRevision <= videoRevision ||
                revisionViewModel.Project.AudioRevision != audioRevision)
                throw new InvalidOperationException("Изменение V-дорожки затронуло ревизию A-дорожек.");
        }
        Console.WriteLine($"PREVIEW_COMPOSITION_SMOKE_OK video={videoSegments.Length} audio={audioSegments.Length} independent-signatures=true");
        return 0;
    }
    finally
    {
        try { Directory.Delete(testRoot, recursive: true); } catch { }
    }
}

if (args is ["--export-smoke"])
{
    var testRoot = Path.Combine(Path.GetTempPath(), "KadrStudio", "export-smoke", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        var redPath = Path.Combine(testRoot, "red.mp4");
        var bluePath = Path.Combine(testRoot, "blue.mp4");
        var outputPath = Path.Combine(testRoot, "multitrack.mp4");
        var redResult = await processRunner.RunAsync(
            ffmpegLocator.FfmpegPath,
            ["-hide_banner", "-y", "-f", "lavfi", "-i", "color=red:s=640x360:r=30:d=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", redPath]);
        var blueResult = await processRunner.RunAsync(
            ffmpegLocator.FfmpegPath,
            ["-hide_banner", "-y", "-f", "lavfi", "-i", "color=blue:s=320x240:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", bluePath]);
        if (redResult.ExitCode != 0 || blueResult.ExitCode != 0)
        {
            throw new InvalidOperationException("Не удалось создать тестовые исходники FFmpeg.");
        }

        var probe = new MediaProbeService(ffmpegLocator, processRunner);
        var red = await probe.ProbeAsync(redPath);
        var blue = await probe.ProbeAsync(bluePath);
        var project = EditorProject.CreateNew();
        project.Media.Add(red);
        project.Media.Add(blue);
        var linkedGroup = Guid.NewGuid();
        project.Clips.Add(new TimelineClip
        {
            AssetId = red.Id,
            Track = TrackKind.Visual,
            TrackIndex = 0,
            LinkGroupId = linkedGroup,
            Start = 0,
            SourceStart = 0,
            Duration = 2,
            Brightness = 0.04,
            Contrast = 1.08,
            Saturation = 1.12
        });
        project.Clips.Add(new TimelineClip
        {
            AssetId = red.Id,
            Track = TrackKind.Audio,
            TrackIndex = 0,
            LinkGroupId = linkedGroup,
            Start = 0,
            SourceStart = 0,
            Duration = 2,
            Pan = -0.2,
            FadeIn = 0.1,
            FadeOut = 0.1,
            Bass = 1
        });
        project.Clips.Add(new TimelineClip
        {
            AssetId = blue.Id,
            Track = TrackKind.Visual,
            TrackIndex = 1,
            Start = 0.5,
            SourceStart = 0,
            Duration = 1
        });
        project.TextOverlays.Add(new TextOverlay
        {
            Start = 0.2,
            Duration = 1.4,
            Text = "Kadr Studio export\nSecond line",
            IsSubtitle = true,
            FontFamily = "Segoe UI",
            FontSize = 38,
            X = 0.5,
            Y = 0.82,
            BoxWidth = 0.72,
            BoxHeight = 0.2
        });

        var exporter = new ExportService(ffmpegLocator, processRunner, renderCoordinator);
        await exporter.ExportAsync(project, outputPath, new ExportSettings
        {
            Resolution = ExportResolution.P480,
            UseHardwareEncoding = false,
            Quality = 25
        });
        var exported = await probe.ProbeAsync(outputPath);
        if (exported.Width != 854 || exported.Height != 480 || exported.Duration is < 1.8 or > 2.2 || !exported.HasAudio)
        {
            throw new InvalidOperationException(
                $"Неверный экспорт: {exported.Width}x{exported.Height}, {exported.Duration:0.00} сек., audio={exported.HasAudio}.");
        }

        Console.WriteLine($"EXPORT_SMOKE_OK {exported.Width}x{exported.Height} {exported.Duration:0.00}s audio={exported.HasAudio}");
        return 0;
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}

if (args is ["--linked-smoke"])
{
    await using var viewModel = new KadrStudio.ViewModels.MainViewModel();
    var video = new MediaAsset
    {
        Name = "linked.mp4",
        Path = "linked.mp4",
        Kind = MediaKind.Video,
        Duration = 12,
        HasAudio = true
    };
    viewModel.Project.Media.Add(video);
    viewModel.AddAssetToTimeline(video.Id, requestedStart: 54);
    var clips = viewModel.Project.Clips.OrderBy(clip => clip.Track).ToList();
    if (clips.Count != 2 || clips.Any(clip => clip.Start != 0) ||
        clips[0].LinkGroupId is null || clips[0].LinkGroupId != clips[1].LinkGroupId ||
        clips.Count(clip => clip.Track == TrackKind.Visual) != 1 || clips.Count(clip => clip.Track == TrackKind.Audio) != 1)
    {
        throw new InvalidOperationException("Первое видео не создало связанную пару V/A с позиции 00:00.");
    }

    viewModel.SelectedClip = clips[0];
    if (!viewModel.UnlinkSelectedClip() || viewModel.Project.Clips.Any(clip => clip.LinkGroupId.HasValue))
    {
        throw new InvalidOperationException("Разрыв связи V/A не сработал.");
    }

    Console.WriteLine("LINKED_CLIPS_SMOKE_OK");
    return 0;
}

if (args is ["--edit-smoke"])
{
    await using var viewModel = new KadrStudio.ViewModels.MainViewModel();
    var testAsset = new MediaAsset
    {
        Name = "test.mp4",
        Path = "test.mp4",
        Kind = MediaKind.Video,
        Duration = 4,
        HasAudio = true
    };
    viewModel.Project.Media.Add(testAsset);
    viewModel.Project.Clips.Add(new TimelineClip
    {
        AssetId = testAsset.Id,
        Track = TrackKind.Visual,
        TrackIndex = 0,
        Start = 0,
        Duration = 4
    });
    viewModel.Project.Markers.Add(new TimelineMarker
    {
        AssetId = testAsset.Id,
        Kind = MarkerKind.Opening,
        Start = 1,
        Duration = 1.5,
        Title = "Опенинг"
    });

    if (!EditingCommandPlanner.TryCreateDeterministic(viewModel.Project, "удали опенинг", null, out var plan))
    {
        throw new InvalidOperationException("Планировщик не распознал удаление опенинга.");
    }
    var count = viewModel.BeginEditPlanReview(plan);
    if (count != 1 || !viewModel.HasPendingEditReview || Math.Abs(viewModel.Project.Duration - 2.5) > 0.01)
    {
        throw new InvalidOperationException("Черновик монтажа применился неверно.");
    }

    viewModel.RejectEditPlanReview();
    if (viewModel.HasPendingEditReview || viewModel.Project.Clips.Count != 1 || Math.Abs(viewModel.Project.Duration - 4) > 0.01)
    {
        throw new InvalidOperationException("Откат черновика не восстановил исходное состояние.");
    }

    Console.WriteLine("EDIT_REVIEW_SMOKE_OK");
    return 0;
}

if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Передайте путь к видеофайлу или --models.");
    return 2;
}

var inputPath = Path.GetFullPath(args[0]);
var probeService = new MediaProbeService(ffmpegLocator, processRunner);
var analysisService = new VideoAnalysisService(ffmpegLocator, processRunner);
var progress = new Progress<VideoAnalysisProgress>(value =>
    Console.Error.WriteLine($"[{value.Percent:0}%] {value.Stage}"));

Console.Error.WriteLine($"Файл: {inputPath}");
var asset = await probeService.ProbeAsync(inputPath);
Console.Error.WriteLine($"Длительность: {asset.Duration:0.0} сек., {asset.Width}x{asset.Height}, {asset.VideoCodec}");
var query = "Сделай полный анализ аниме: выдели сцены, рекап, опенинг, эндинг, сцену после титров и превью следующей серии";
var baseline = await analysisService.AnalyzeAsync(
    new VideoAnalysisRequest(asset, 0, asset.Duration, query),
    progress);
var models = await ollamaService.GetModelsAsync();
var model = models.FirstOrDefault(item => item.Name.Equals("qwen3-vl:4b-instruct", StringComparison.OrdinalIgnoreCase))
    ?? models.FirstOrDefault(item => item.SupportsVision)
    ?? throw new InvalidOperationException("В проекте нет vision-модели Ollama.");
var enhancement = await ollamaService.EnhanceAsync(asset, baseline, query, model.Name, progress);

var refinedKinds = enhancement.Ranges.Select(range => range.Kind).ToHashSet();
var ranges = baseline.Ranges
    .Where(range => !refinedKinds.Contains(range.Kind))
    .Concat(enhancement.Ranges)
    .OrderBy(range => range.SourceStart)
    .ThenBy(range => range.Kind)
    .ToList();
var frameRefined = await analysisService.RefineSemanticBoundariesAsync(
    asset,
    baseline with { Ranges = ranges },
    progress);
ranges = frameRefined.Ranges.ToList();
if (args.Contains("--semantic-only", StringComparer.OrdinalIgnoreCase))
{
    ranges = ranges
        .Where(range => range.Kind is MarkerKind.Opening or MarkerKind.Ending or MarkerKind.PostCredits or MarkerKind.Preview or MarkerKind.Recap or MarkerKind.Note)
        .ToList();
}
var report = new AnalysisSmokeReport(
    inputPath,
    asset.Duration,
    model.Name,
    enhancement.UsedVision,
    baseline.Summary,
    enhancement.Summary,
    ranges);
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
}));
return 0;

internal sealed record AnalysisSmokeReport(
    string File,
    double Duration,
    string Model,
    bool UsedVision,
    string TechnicalSummary,
    string AiSummary,
    IReadOnlyList<DetectedVideoRange> Ranges);
