using KadrStudio.Adapters;
using KadrStudio.Application.Jobs;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Jobs;
using KadrStudio.Infrastructure.Rendering;
using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>
/// Application composition root for rendering. Preview and export share the
/// same plan builder, FFmpeg command builder and resource scheduler, but do not
/// share mutable playback state.
/// </summary>
public sealed class TimelineRenderCoordinator : IAsyncDisposable
{
    private readonly EditorProjectMapper _mapper = new();
    private readonly RenderPlanBuilder _planBuilder = new();
    private readonly BackgroundJobScheduler _scheduler = new();
    private readonly FfmpegRenderEngine _engine;
    private readonly FfmpegRenderCommandBuilder _commandBuilder = new();
    private long _revision;

    public TimelineRenderCoordinator(FfmpegLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        _engine = new FfmpegRenderEngine(locator.FfmpegPath, _commandBuilder, _scheduler);
    }

    public RenderPlan CreatePlan(EditorProject project, TimeRange? range = null)
        => _planBuilder.Build(_mapper.ToCore(project, Interlocked.Increment(ref _revision)), range);

    public Task<string> RenderAsync(
        RenderPlan plan,
        RenderOutputOptions options,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _engine.RenderAsync(plan, options, progress, cancellationToken);

    public ExternalRenderCommand CreateCommand(RenderPlan plan, RenderOutputOptions options)
        => _commandBuilder.Build(plan, options);

    public SchedulerSnapshot GetSchedulerSnapshot() => _scheduler.GetSnapshot();

    public ValueTask DisposeAsync() => _scheduler.DisposeAsync();
}
