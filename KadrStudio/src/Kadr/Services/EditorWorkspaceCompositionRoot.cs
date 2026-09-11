using KadrStudio.Application.Caching;
using KadrStudio.Application.Media;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Media;
using KadrStudio.Application.Upscaling;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Services.Agent;
using KadrStudio.Services.Editorial;

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
    IAiUpscaleService AiUpscaleService,
    AiServerConnection AiServer,
    BackgroundJobScheduler AutomationScheduler,
    WorkspaceSettingsService SettingsService,
    EditorialPipeline EditorialPipeline,
    IAgentDebugLog AgentDebugLog);

public static class EditorWorkspaceCompositionRoot
{
    public static EditorWorkspaceServices Create()
    {
        var ffmpeg = new FfmpegLocator();
        var processes = new ProcessRunner();
        var settingsService = new WorkspaceSettingsService();
        var settings = settingsService.Load();
        var artifacts = ArtifactStoreFactory.Create(new ArtifactStoreOptions(
            settings.ArtifactRoot, settings.ArtifactDiskBudgetBytes,
            OwnershipId: settings.ArtifactCacheOwnerId));
        if (settings.ArtifactCacheOwnerId != artifacts.Options.OwnershipId ||
            !settings.ArtifactRoot.Equals(artifacts.Options.Root, StringComparison.OrdinalIgnoreCase))
        {
            settingsService.Save(settings with
            {
                ArtifactRoot = artifacts.Options.Root,
                ArtifactCacheOwnerId = artifacts.Options.OwnershipId
            });
        }
        var probe = new MediaProbeService(ffmpeg, processes);
        var registry = new MediaRegistry(probe);
        var thumbnails = new ThumbnailService(ffmpeg, processes, artifacts);
        var renderCoordinator = new TimelineRenderCoordinator(ffmpeg);
        var timelineCache = new TimelineMediaCacheService(
            ffmpeg, processes, artifacts: artifacts);
        var export = new ExportService(ffmpeg, processes, renderCoordinator);
        var subtitles = new AutoSubtitleService(ffmpeg, processes);
        var aiServer = new AiServerConnection();
        var editorialTelemetry = new JsonlEditorialTelemetrySink();
        var aiServerV2 = new AiServerV2Client(aiServer, editorialTelemetry);
        var editorialIndexer = new AiServerMediaUnderstandingIndexer(
            aiServerV2, new AnalysisProxyBuilder(ffmpeg, processes, editorialTelemetry));
        var editorialReasoner = new AiServerEditorialReasoner(aiServerV2);
        var editorialPipeline = new EditorialPipeline(
            editorialIndexer, editorialReasoner, editorialReasoner, editorialIndexer,
            retriever: new HierarchicalMediaRetriever(semanticRanker: new AiServerSemanticNodeRanker(aiServerV2)),
            telemetry: editorialTelemetry);
        var upscale = new AnimeSrUpscaleService(ffmpeg, probe, processes, aiServer);
        var document = new KadrStudio.Application.Storage.ProjectDocumentCoordinator(KadrLocalDataPaths.HistoryRoot);
        return new EditorWorkspaceServices(
            ffmpeg,
            processes,
            new ProjectService(KadrLocalDataPaths.RecoveryRoot, document),
            artifacts,
            registry,
            probe,
            thumbnails,
            renderCoordinator,
            timelineCache,
            export,
            new ProjectHistoryService(coordinator: document),
            subtitles,
            upscale,
            aiServer,
            new BackgroundJobScheduler(),
            settingsService,
            editorialPipeline,
            new FileAgentDebugLog());
    }
}
