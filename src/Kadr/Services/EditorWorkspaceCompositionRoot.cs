using KadrStudio.Application.Caching;
using KadrStudio.Application.Media;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Media;

namespace KadrStudio.Services;

/// <summary>
/// Owns construction of process, cache and media services. Views and view-models
/// receive an already composed workspace and never construct FFmpeg/process adapters.
/// </summary>
public sealed record EditorWorkspaceServices(
    FfmpegLocator FfmpegLocator,
    ProcessRunner ProcessRunner,
    ProjectService ProjectService,
    IArtifactStore ArtifactStore,
    IMediaRegistry MediaRegistry,
    MediaProbeService MediaProbeService,
    ThumbnailService ThumbnailService,
    TimelineRenderCoordinator RenderCoordinator,
    TimelineMediaCacheService TimelineMediaCacheService,
    ExportService ExportService,
    ProjectHistoryService ProjectHistoryService,
    AutoSubtitleService AutoSubtitleService,
    VideoAnalysisService VideoAnalysisService,
    OllamaVideoAnalysisService OllamaVideoAnalysisService,
    BackgroundJobScheduler AutomationScheduler,
    WorkspaceSettingsService SettingsService);

public static class EditorWorkspaceCompositionRoot
{
    public static EditorWorkspaceServices Create()
    {
        var ffmpeg = new FfmpegLocator();
        var processes = new ProcessRunner();
        var settingsService = new WorkspaceSettingsService();
        var settings = settingsService.Load();
        var artifacts = new DiskMediaArtifactCache(new ArtifactStoreOptions(
            settings.ArtifactRoot, settings.ArtifactDiskBudgetBytes));
        var probe = new MediaProbeService(ffmpeg, processes);
        var registry = new MediaRegistry(probe);
        var thumbnails = new ThumbnailService(ffmpeg, processes, artifacts);
        var renderCoordinator = new TimelineRenderCoordinator(ffmpeg);
        var timelineCache = new TimelineMediaCacheService(
            ffmpeg, processes, artifacts: artifacts);
        var export = new ExportService(ffmpeg, processes, renderCoordinator);
        var subtitles = new AutoSubtitleService(ffmpeg, processes);
        var analysis = new VideoAnalysisService(ffmpeg, processes);
        var ollama = new OllamaVideoAnalysisService(ffmpeg, processes);
        return new EditorWorkspaceServices(
            ffmpeg,
            processes,
            new ProjectService(),
            artifacts,
            registry,
            probe,
            thumbnails,
            renderCoordinator,
            timelineCache,
            export,
            new ProjectHistoryService(),
            subtitles,
            analysis,
            ollama,
            new BackgroundJobScheduler(),
            settingsService);
    }
}
