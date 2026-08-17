using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Application.Preview;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using KadrStudio.Application.Caching;

namespace KadrStudio.Playback;

public sealed class PreviewPresenter : IAsyncDisposable
{
    private readonly Image _image;
    private readonly FrameworkElement _emptyState;
    private readonly TimelineRenderCoordinator _coordinator;
    private readonly IPreviewEngine _engine;
    private readonly PreviewProxyStore _proxies;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private WriteableBitmap? _bitmap;
    private ProjectState? _project;
    private long _videoGeneration;
    private long _audioGeneration;
    private long _overlayGeneration;
    private bool _halfQuality = true;
    private bool _prepared;
    private string? _videoSignature;
    private string? _audioSignature;
    private string? _overlaySignature;
    private bool _disposed;

    public PreviewPresenter(Image image, FrameworkElement emptyState, FfmpegLocator locator,
        TimelineRenderCoordinator coordinator,
        IArtifactStore? artifacts = null)
    {
        _image = image;
        _emptyState = emptyState;
        _coordinator = coordinator;
        var hostPath = Path.Combine(AppContext.BaseDirectory, "mediahost", "Kadr.MediaHost.exe");
        _engine = new MediaHostClient(hostPath, locator.FfmpegPath);
        _proxies = new PreviewProxyStore(locator, artifacts);
        _engine.FramePresented += Engine_FramePresented;
        _engine.AudioMeterUpdated += Engine_AudioMeterUpdated;
        _engine.Failed += Engine_Failed;
        _proxies.ProxyReady += Proxies_ProxyReady;
    }

    public event EventHandler<Exception>? Failed;
    public event EventHandler<AudioMeterLevel>? AudioMeterUpdated;
    public PreviewState State => _engine.State;
    public TimelineTime Position => _engine.Position;

    public void SetProject(ProjectState project, bool halfQuality)
    {
        var identityChanged = _project is null || _project.Id != project.Id;
        var qualityChanged = _halfQuality != halfQuality;
        _project = project;
        _halfQuality = halfQuality;
        if (identityChanged || qualityChanged)
        {
            _prepared = false;
            _videoSignature = null;
            _audioSignature = null;
            _overlaySignature = null;
            _videoGeneration++;
            _audioGeneration++;
            _overlayGeneration++;
        }
        _proxies.Configure(project);
    }

    public async Task UpdateAsync(double timelineSeconds, bool forceSeek, bool playing,
        CancellationToken cancellationToken = default)
    {
        if (_project is null) return;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hasVideo = _project.MediaClips.Any(clip =>
                _project.FindTrack(clip.TrackId)?.Kind == TrackKind.Visual &&
                _project.Sources.TryGetValue(clip.SourceId, out var source) &&
                source.OnlineState == MediaOnlineState.Online && File.Exists(source.Path));
            var hasAudio = _project.MediaClips.Any(clip =>
                _project.FindTrack(clip.TrackId)?.Kind == TrackKind.Audio &&
                _project.Sources.TryGetValue(clip.SourceId, out var source) && source.HasAudio &&
                source.OnlineState == MediaOnlineState.Online && File.Exists(source.Path));
            if (!hasVideo && !hasAudio)
            {
                await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
                Dispatch(() => { _image.Visibility = Visibility.Collapsed; _emptyState.Visibility = Visibility.Visible; });
                return;
            }
            Dispatch(() => _emptyState.Visibility = hasVideo ? Visibility.Collapsed : Visibility.Visible);
            var plan = _coordinator.CreatePlan(_project);
            if (_halfQuality)
            {
                // Interactive playback must stay responsive. Explicit proxy
                // preparation may populate this store, but merely dropping or
                // playing a clip must never start a full-length transcode.
                plan = _proxies.UseAvailable(plan);
            }
            var position = TimelineTime.FromSeconds(Math.Clamp(
                timelineSeconds, 0, Math.Max(0, _project.Duration.TotalSeconds)));
            var size = PreviewSizing.Resolve(_project, _halfQuality);
            if (!_prepared)
            {
                var request = new PreviewRequest(position, _project.FrameRate, size.Width, size.Height,
                    _halfQuality, new PreviewGeneration(_videoGeneration, _audioGeneration, _overlayGeneration));
                await _engine.PrepareAsync(plan, request, cancellationToken).ConfigureAwait(false);
                RememberSignatures(plan);
                _prepared = true;
            }
            else
            {
                var videoChanged = !string.Equals(_videoSignature, plan.VideoContentSignature, StringComparison.Ordinal);
                var audioChanged = !string.Equals(_audioSignature, plan.AudioContentSignature, StringComparison.Ordinal);
                var overlayChanged = !string.Equals(_overlaySignature, plan.OverlaySignature, StringComparison.Ordinal);
                if (videoChanged) _videoGeneration++;
                if (audioChanged) _audioGeneration++;
                if (overlayChanged) _overlayGeneration++;
                var request = new PreviewRequest(position, _project.FrameRate, size.Width, size.Height,
                    _halfQuality, new PreviewGeneration(_videoGeneration, _audioGeneration, _overlayGeneration));
                if (videoChanged || audioChanged)
                {
                    await _engine.UpdatePlanAsync(plan, request, videoChanged, audioChanged, cancellationToken)
                        .ConfigureAwait(false);
                    RememberSignatures(plan);
                }
                else if (overlayChanged)
                {
                    _overlaySignature = plan.OverlaySignature;
                }

                var frameDuration = 1d / Math.Max(1, _project.FrameRate.FramesPerSecond);
                if (forceSeek && Math.Abs(position.TotalSeconds - _engine.Position.TotalSeconds) > frameDuration / 2)
                    await _engine.SeekAsync(position, cancellationToken).ConfigureAwait(false);
            }
            if (playing) await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
            else await _engine.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    public async Task InvalidateAsync(bool video, bool audio, bool overlay)
    {
        if (video) _videoGeneration++;
        if (audio) _audioGeneration++;
        if (overlay) _overlayGeneration++;
        _prepared = false;
        _videoSignature = null;
        _audioSignature = null;
        _overlaySignature = null;
        await _engine.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.FramePresented -= Engine_FramePresented;
        _engine.AudioMeterUpdated -= Engine_AudioMeterUpdated;
        _engine.Failed -= Engine_Failed;
        _proxies.ProxyReady -= Proxies_ProxyReady;
        await _engine.DisposeAsync().ConfigureAwait(false);
        await _proxies.DisposeAsync().ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private void Engine_FramePresented(object? sender, VideoFrame frame)
    {
        if (frame.Generation != _videoGeneration) return;
        Dispatch(() =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra.ToArray(), frame.Stride, 0);
            _image.Source = _bitmap;
            _image.Visibility = Visibility.Visible;
            _emptyState.Visibility = Visibility.Collapsed;
        });
    }

    private void Engine_Failed(object? sender, Exception exception) => Failed?.Invoke(this, exception);

    private void Engine_AudioMeterUpdated(object? sender, AudioMeterLevel level)
        => AudioMeterUpdated?.Invoke(this, level);

    private void Proxies_ProxyReady(object? sender, Guid sourceId)
    {
        if (_disposed || _project?.Sources.ContainsKey(sourceId) != true) return;
        _videoSignature = null;
        _ = ActivateReadyProxyAsync();
    }

    private async Task ActivateReadyProxyAsync()
    {
        try
        {
            await UpdateAsync(Position.TotalSeconds, forceSeek: true, playing: State == PreviewState.Playing)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
    }

    private void RememberSignatures(KadrStudio.Application.Rendering.RenderPlan plan)
    {
        _videoSignature = plan.VideoContentSignature;
        _audioSignature = plan.AudioContentSignature;
        _overlaySignature = plan.OverlaySignature;
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
