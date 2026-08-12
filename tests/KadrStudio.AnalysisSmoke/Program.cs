using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KadrStudio.Models;
using KadrStudio.Services;

Console.OutputEncoding = Encoding.UTF8;
var ffmpegLocator = new FfmpegLocator();
var processRunner = new ProcessRunner();
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

if (args is ["--preview-smoke", var previewInput] && File.Exists(previewInput))
{
    var probe = new MediaProbeService(ffmpegLocator, processRunner);
    var previewAsset = await probe.ProbeAsync(Path.GetFullPath(previewInput));
    var service = new PreviewProxyService(ffmpegLocator, processRunner);
    var started = System.Diagnostics.Stopwatch.StartNew();
    var segment = await service.EnsureSegmentAsync(previewAsset, Math.Min(39, previewAsset.Duration / 2));
    var segmentAsset = await probe.ProbeAsync(segment.Path);
    if (!File.Exists(segment.Path) || segmentAsset.Kind != MediaKind.Video || segmentAsset.Duration > 21 || !segmentAsset.HasAudio)
    {
        throw new InvalidOperationException("Быстрый фрагмент предпросмотра создан неверно.");
    }
    Console.WriteLine($"PREVIEW_SEGMENT_SMOKE_OK {segmentAsset.Duration:0.0}s in {started.Elapsed.TotalSeconds:0.0}s");
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
        string.IsNullOrWhiteSpace(cacheAsset.WaveformPath) || !File.Exists(cacheAsset.WaveformPath))
    {
        throw new InvalidOperationException("Кадры или реальная форма волны таймлайна не созданы.");
    }
    Console.WriteLine($"TIMELINE_CACHE_SMOKE_OK frames={cacheAsset.TimelineFramePaths.Count} waveform=yes in {started.Elapsed.TotalSeconds:0.0}s");
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
    await File.WriteAllTextAsync(Path.GetFullPath(uiOutput), ProjectJson.Serialize(uiProject), Encoding.UTF8);
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
        var checkpoint = history.CreateCheckpoint(project, "Первая версия");
        project.Name = "После изменения";
        var entries = history.GetCheckpoints(project);
        if (entries.Count != 1 || entries[0].Id != checkpoint.Id)
        {
            throw new InvalidOperationException("Контрольная точка не появилась в истории.");
        }

        var restored = history.RestoreCheckpoint(entries[0], null);
        if (restored.Name != "До изменения" || restored.Id != project.Id)
        {
            throw new InvalidOperationException("Восстановленный снимок не совпадает с исходным проектом.");
        }

        history.DeleteCheckpoint(entries[0]);
        if (history.GetCheckpoints(project).Count != 0)
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

        var exporter = new ExportService(ffmpegLocator, processRunner);
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
    using var viewModel = new KadrStudio.ViewModels.MainViewModel();
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
    using var viewModel = new KadrStudio.ViewModels.MainViewModel();
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
