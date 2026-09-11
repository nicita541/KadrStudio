using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
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
    private readonly LatestVideoFrameDispatcher _frameDispatcher;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CoalescingAsyncOperation _proxyActivation;
    private readonly object _stateGate = new();
    private long _projectEpoch;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _disposal;
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
        IArtifactStore? artifacts = null, IPreviewEngine? engine = null)
    {
        _image = image;
        _emptyState = emptyState;
        _coordinator = coordinator;
        var hostPath = Path.Combine(AppContext.BaseDirectory, "mediahost", "Kadr.MediaHost.exe");
        _engine = engine ?? new MediaHostClient(hostPath, locator.FfmpegPath);
        _proxies = new PreviewProxyStore(locator, artifacts);
        _proxyActivation = new CoalescingAsyncOperation(ActivateReadyProxyAsync,
            error => Dispatch(() => { if (!_disposed) Failed?.Invoke(this, error); }));
        _frameDispatcher = new LatestVideoFrameDispatcher(Dispatch, PresentFrame,
            frame => !_disposed && frame.Generation == Volatile.Read(ref _videoGeneration));
        _engine.FramePresented += Engine_FramePresented;
        _engine.AudioMeterUpdated += Engine_AudioMeterUpdated;
        _engine.Failed += Engine_Failed;
        _proxies.ProxyReady += Proxies_ProxyReady;
        _proxies.StatusChanged += Proxies_StatusChanged;
    }

    public event EventHandler<Exception>? Failed;
    public event EventHandler? ProxyStatusChanged;
    public string ProxyStatus => _proxies.StatusText;
    private void Proxies_StatusChanged(object? sender, EventArgs args)
        => Dispatch(() => { if (!_disposed) ProxyStatusChanged?.Invoke(this, EventArgs.Empty); });
    public event EventHandler<AudioMeterLevel>? AudioMeterUpdated;
    public PreviewState State => _engine.State;
    public TimelineTime Position => _engine.Position;

    public void SetProject(ProjectState project, bool halfQuality)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            if (!ReferenceEquals(_project, project) || _halfQuality != halfQuality) _projectEpoch++;
            var identityChanged = _project is null || _project.Id != project.Id ||
                _project.ActiveSequenceId != project.ActiveSequenceId || _project.Sequence != project.Sequence;
            var qualityChanged = _halfQuality != halfQuality;
            _project = project;
            _halfQuality = halfQuality;
            if (identityChanged || qualityChanged)
            {
                _frameDispatcher.Clear();
                _prepared = false;
                _videoSignature = null;
                _audioSignature = null;
                _overlaySignature = null;
                _videoGeneration++;
                _audioGeneration++;
                _overlayGeneration++;
            }
            _proxies.Configure(project);
            if (halfQuality) _proxies.Queue(project, highResolutionOnly: true);
        }
    }

    public async Task UpdateAsync(double timelineSeconds, bool forceSeek, bool playing,
        CancellationToken cancellationToken = default)
    {
        ProjectState? project;
        bool useProxy;
        long epoch;
        CancellationTokenSource linked;
        lock (_stateGate)
        {
            project = _project; useProxy = _halfQuality; epoch = _projectEpoch;
            if (project is null || _disposed) return;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using var operationCancellation = linked;
        cancellationToken = linked.Token;
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrent(epoch)) return;
            var hasVideo = project.MediaClips.Any(clip =>
                project.FindTrack(clip.TrackId)?.Kind == TrackKind.Visual &&
                project.Sources.TryGetValue(clip.SourceId, out var source) &&
                source.OnlineState == MediaOnlineState.Online && File.Exists(source.Path));
            var hasAudio = project.MediaClips.Any(clip =>
                project.FindTrack(clip.TrackId)?.Kind == TrackKind.Audio &&
                project.Sources.TryGetValue(clip.SourceId, out var source) && source.HasAudio &&
                source.OnlineState == MediaOnlineState.Online && File.Exists(source.Path));
            if (!hasVideo && !hasAudio)
            {
                await _engine.StopAsync(cancellationToken).ConfigureAwait(false);
                Dispatch(() => { if (IsCurrent(epoch)) { _image.Visibility = Visibility.Collapsed; _emptyState.Visibility = Visibility.Visible; } });
                return;
            }
            Dispatch(() => { if (IsCurrent(epoch)) _emptyState.Visibility = hasVideo ? Visibility.Collapsed : Visibility.Visible; });
            var plan = _coordinator.CreatePlan(project);
            if (useProxy)
            {
                plan = _proxies.UseAvailable(plan);
            }
            var position = TimelineTime.FromSeconds(Math.Clamp(
                timelineSeconds, 0, Math.Max(0, project.Duration.TotalSeconds)));
            var size = PreviewSizing.Resolve(project, useProxy);
            if (!_prepared)
            {
                PreviewRequest request;
                lock (_stateGate)
                {
                    if (!IsCurrent(epoch)) return;
                    request = new PreviewRequest(position, project.FrameRate, size.Width, size.Height,
                        useProxy, new PreviewGeneration(_videoGeneration, _audioGeneration, _overlayGeneration));
                }
                await _engine.PrepareAsync(plan, request, cancellationToken).ConfigureAwait(false);
                if (!CommitPrepared(plan, epoch)) return;
                _proxies.AcknowledgePlan(plan);
            }
            else
            {
                bool videoChanged, audioChanged, overlayChanged;
                PreviewRequest request;
                lock (_stateGate)
                {
                    if (!IsCurrent(epoch)) return;
                    videoChanged = !string.Equals(_videoSignature, plan.VideoContentSignature, StringComparison.Ordinal);
                    audioChanged = !string.Equals(_audioSignature, plan.AudioContentSignature, StringComparison.Ordinal);
                    overlayChanged = !string.Equals(_overlaySignature, plan.OverlaySignature, StringComparison.Ordinal);
                    if (videoChanged) _videoGeneration++;
                    if (audioChanged) _audioGeneration++;
                    if (overlayChanged) _overlayGeneration++;
                    request = new PreviewRequest(position, project.FrameRate, size.Width, size.Height,
                        useProxy, new PreviewGeneration(_videoGeneration, _audioGeneration, _overlayGeneration));
                }
                if (videoChanged || audioChanged)
                {
                    await _engine.UpdatePlanAsync(plan, request, videoChanged, audioChanged, cancellationToken)
                        .ConfigureAwait(false);
                    if (!CommitPrepared(plan, epoch)) return;
                    _proxies.AcknowledgePlan(plan);
                }
                else if (overlayChanged)
                {
                    lock (_stateGate) if (IsCurrent(epoch)) _overlaySignature = plan.OverlaySignature;
                }

                var frameDuration = 1d / Math.Max(1, project.FrameRate.FramesPerSecond);
                if (forceSeek && Math.Abs(position.TotalSeconds - _engine.Position.TotalSeconds) > frameDuration / 2)
                {
                    PreviewGeneration generation;
                    lock (_stateGate)
                    {
                        if (!IsCurrent(epoch)) return;
                        _videoGeneration++;
                        _audioGeneration++;
                        _frameDispatcher.Clear();
                        generation = new PreviewGeneration(_videoGeneration, _audioGeneration, _overlayGeneration);
                    }
                    await _engine.SeekAsync(position,
                        generation, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            if (!IsCurrent(epoch)) return;
            if (playing) await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
            else await _engine.PauseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    public async Task InvalidateAsync(bool video, bool audio, bool overlay)
    {
        long epoch;
        CancellationTokenSource linked;
        lock (_stateGate)
        {
            if (_disposed) return;
            epoch = ++_projectEpoch;
            linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            if (video) _videoGeneration++;
            if (video) _frameDispatcher.Clear();
            if (audio) _audioGeneration++;
            if (overlay) _overlayGeneration++;
            _prepared = false;
            _videoSignature = null;
            _audioSignature = null;
            _overlaySignature = null;
        }
        using var operationCancellation = linked;
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (IsCurrent(epoch)) await _engine.StopAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _operationGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposal is not null) return new ValueTask(_disposal);
            _disposed = true;
            _projectEpoch++;
            _disposal = Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _frameDispatcher.Clear();
        _engine.FramePresented -= Engine_FramePresented;
        _engine.AudioMeterUpdated -= Engine_AudioMeterUpdated;
        _engine.Failed -= Engine_Failed;
        _proxies.ProxyReady -= Proxies_ProxyReady;
        _proxies.StatusChanged -= Proxies_StatusChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _proxyActivation.DisposeAsync().ConfigureAwait(false);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            await _proxies.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            // Cancelled waiters can still finish unwinding. This managed gate must
            // remain valid for their completion; no wait handle is allocated.
            _operationGate.Release();
        }
    }

    private void Engine_FramePresented(object? sender, VideoFrame frame)
    {
        if (frame.Generation != _videoGeneration) return;
        _frameDispatcher.Offer(frame);
    }

    private bool IsCurrent(long epoch)
    {
        lock (_stateGate) return !_disposed && _projectEpoch == epoch;
    }

    private bool CommitPrepared(KadrStudio.Application.Rendering.RenderPlan plan, long epoch)
    {
        lock (_stateGate)
        {
            if (_disposed || _projectEpoch != epoch) return false;
            RememberSignatures(plan);
            _prepared = true;
            return true;
        }
    }

    private void PresentFrame(VideoFrame frame)
    {
        if (_disposed || frame.Generation != Volatile.Read(ref _videoGeneration)) return;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        if (MemoryMarshal.TryGetArray(frame.Bgra, out ArraySegment<byte> pixels) && pixels.Array is not null)
            _bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height), pixels.Array, frame.Stride, pixels.Offset);
        else
            _bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra.ToArray(), frame.Stride, 0);
        _image.Source = _bitmap;
        _image.Visibility = Visibility.Visible;
        _emptyState.Visibility = Visibility.Collapsed;
    }

    private void Engine_Failed(object? sender, Exception exception) => Failed?.Invoke(this, exception);

    private void Engine_AudioMeterUpdated(object? sender, AudioMeterLevel level)
        => AudioMeterUpdated?.Invoke(this, level);

    private void Proxies_ProxyReady(object? sender, Guid sourceId)
    {
        if (_disposed || _project?.Sources.ContainsKey(sourceId) != true) return;
        _proxyActivation.Request();
    }

    private Task ActivateReadyProxyAsync(CancellationToken cancellationToken)
        => UpdateAsync(Position.TotalSeconds, forceSeek: true, playing: State == PreviewState.Playing,
            cancellationToken: cancellationToken);

    private void RememberSignatures(KadrStudio.Application.Rendering.RenderPlan plan)
    {
        _videoSignature = plan.VideoContentSignature;
        _audioSignature = plan.AudioContentSignature;
        _overlaySignature = plan.OverlaySignature;
    }

    private void Dispatch(Action action)
    {
        var dispatcher = _image.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
