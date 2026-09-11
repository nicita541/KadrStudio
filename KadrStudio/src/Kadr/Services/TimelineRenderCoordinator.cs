using KadrStudio.Application.Jobs;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Rendering;

namespace KadrStudio.Services;

/// <summary>
/// Application composition root for rendering. Preview and export share the
/// same plan builder, FFmpeg command builder and resource scheduler, but do not
/// share mutable playback state.
/// </summary>
public sealed class TimelineRenderCoordinator : IAsyncDisposable
{
    private readonly RenderPlanBuilder _planBuilder = new();
    private readonly BackgroundJobScheduler _scheduler = new();
    private readonly FfmpegRenderEngine _engine;
    private readonly FfmpegRenderCommandBuilder _commandBuilder = new();

    public TimelineRenderCoordinator(FfmpegLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _engine = new FfmpegRenderEngine(locator.FfmpegPath, _commandBuilder, _scheduler);
    }

    public RenderPlan CreatePlan(ProjectState project, TimeRange? range = null)
        => _planBuilder.Build(project, range);

    public async Task<string> RenderAsync(
        RenderPlan plan,
        RenderOutputOptions options,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.IncludeVideo || !options.IncludeOverlays || plan.TextLayers.IsDefaultOrEmpty)
            return await _engine.RenderAsync(plan, options, progress, cancellationToken).ConfigureAwait(false);
        using var overlays = await TextOverlayRasterizer.PrepareAsync(plan, options.Width, options.Height, cancellationToken).ConfigureAwait(false);
        return await _engine.RenderAsync(overlays.Plan, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public ExternalRenderCommand CreateCommand(RenderPlan plan, RenderOutputOptions options)
        => _commandBuilder.Build(plan, options);

    public SchedulerSnapshot GetSchedulerSnapshot() => _scheduler.GetSnapshot();

    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();
}
