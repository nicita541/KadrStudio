using System.Windows.Controls;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Playback;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class PreviewPresenterLifetimeTests
{
    [Theory]
    [InlineData("project")]
    [InlineData("quality")]
    [InlineData("sequence")]
    [InlineData("invalidate")]
    [InlineData("close")]
    [InlineData("unchanged")]
    public void Project_switch_or_close_during_prepare_does_not_commit_old_state(string change)
        => RunSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "kadr-presenter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "source.mp4");
                File.WriteAllText(path, "fixture: fake engine does not decode");
                var project = ProjectState.CreateNew();
                var source = new MediaSource(Guid.NewGuid(), path, "fixture", MediaKind.Video,
                    TimelineTime.FromSeconds(2), false, 320, 180, Streams: []);
                project = project with
                {
                    Sources = project.Sources.Add(source.Id, source),
                    MediaClips = [new MediaClip(Guid.NewGuid(), source.Id,
                        project.Tracks.First(track => track.Kind == TrackKind.Visual).Id,
                        TimelineTime.Zero, TimelineTime.Zero, TimelineTime.FromSeconds(2), Video: new VideoParameters())]
                };
                var locator = new FfmpegLocator();
                using var artifacts = new DiskMediaArtifactCache(Path.Combine(root, "cache"));
                var coordinator = new TimelineRenderCoordinator(locator);
                var engine = new DelayedEngine();
                var presenter = new PreviewPresenter(new Image(), new Border(), locator, coordinator, artifacts, engine);
                try
                {
                    presenter.SetProject(project, false);
                    var pending = presenter.UpdateAsync(0, false, true);
                    Assert.Equal(1, engine.Prepares);
                    var queued = presenter.UpdateAsync(0, false, true);
                    if (change == "close")
                    {
                        presenter.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                        Assert.False(engine.DisposedWhilePreparing);
                        Assert.True(pending.IsCompleted);
                        Assert.ThrowsAny<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
                        Assert.ThrowsAny<OperationCanceledException>(() => queued.GetAwaiter().GetResult());
                        Assert.Equal(0, engine.Starts);
                        return;
                    }
                    Task invalidation = Task.CompletedTask;
                    switch (change)
                    {
                        case "project": presenter.SetProject(project with { Id = Guid.NewGuid() }, false); break;
                        case "quality": presenter.SetProject(project, true); break;
                        case "sequence": presenter.SetProject(project with { CanvasWidth = 1280 }, false); break;
                        case "invalidate": invalidation = presenter.InvalidateAsync(true, true, true); break;
                        case "unchanged": presenter.SetProject(project, false); break;
                    }
                    engine.Release.TrySetResult();
                    pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    queued.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    invalidation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    Assert.Equal(change == "unchanged" ? 2 : 0, engine.Starts);
                    presenter.UpdateAsync(0, false, false).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    Assert.Equal(change == "unchanged" ? 1 : 2, engine.Prepares);
                    if (change != "unchanged") Assert.True(engine.Generations[1] > engine.Generations[0]);
                }
                finally
                {
                    engine.Release.TrySetResult();
                    presenter.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            finally { Directory.Delete(root, true); }
        });

    private static void RunSta(Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception error) { completed.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    private sealed class DelayedEngine : IPreviewEngine
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Prepares;
        public int Starts;
        public List<long> Generations { get; } = [];
        public bool Preparing;
        public bool DisposedWhilePreparing;
        public PreviewState State => PreviewState.Paused;
        public TimelineTime Position => TimelineTime.Zero;
        public event EventHandler<PreviewState>? StateChanged { add { } remove { } }
        public event EventHandler<VideoFrame>? FramePresented { add { } remove { } }
        public event EventHandler<AudioMeterLevel>? AudioMeterUpdated { add { } remove { } }
        public event EventHandler<Exception>? Failed { add { } remove { } }
        public async Task PrepareAsync(RenderPlan plan, PreviewRequest request, CancellationToken cancellationToken = default)
        {
            Prepares++;
            Generations.Add(request.Generation.Video);
            Preparing = true;
            try { await Release.Task.WaitAsync(cancellationToken); }
            finally { Preparing = false; }
        }
        public Task UpdatePlanAsync(RenderPlan plan, PreviewRequest request, bool restartVideo, bool restartAudio, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }
        public Task SeekAsync(TimelineTime position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimelineTime position, PreviewGeneration generation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposedWhilePreparing |= Preparing; return ValueTask.CompletedTask; }
    }
}
