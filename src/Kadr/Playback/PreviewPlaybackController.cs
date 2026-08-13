using System.Windows;
using System.Windows.Controls;
using KadrStudio.Services;

namespace KadrStudio.Playback;

/// <summary>
/// Owns the WPF decoder layer for an editor preview. Video and audio each use
/// their own double buffer. A prepared source replaces the visible source only
/// after MediaOpened, so decoder latency cannot produce a black transition.
/// </summary>
public sealed class PreviewPlaybackController : IDisposable
{
    private MediaElement _activeVideo;
    private MediaElement _standbyVideo;
    private MediaElement _activeAudio;
    private MediaElement _standbyAudio;
    private TimelinePreviewSegment? _activeVideoSegment;
    private TimelinePreviewSegment? _standbyVideoSegment;
    private TimelinePreviewSegment? _activeAudioSegment;
    private TimelinePreviewSegment? _standbyAudioSegment;
    private double _standbyVideoPosition;
    private double _standbyAudioPosition;
    private bool _standbyVideoReady;
    private bool _standbyAudioReady;
    private bool _videoOpened;
    private bool _audioOpened;
    private bool _disposed;

    public PreviewPlaybackController(
        MediaElement videoA,
        MediaElement videoB,
        MediaElement audioA,
        MediaElement audioB)
    {
        _activeVideo = videoA;
        _standbyVideo = videoB;
        _activeAudio = audioA;
        _standbyAudio = audioB;
        SubscribeVideo(videoA);
        SubscribeVideo(videoB);
        SubscribeAudio(audioA);
        SubscribeAudio(audioB);
        ConfigureVideo(videoA);
        ConfigureVideo(videoB);
        ConfigureAudio(audioA);
        ConfigureAudio(audioB);
    }

    public bool IsPlaying { get; private set; }
    public double TimelinePosition { get; private set; }
    public event EventHandler? VideoPresented;
    public event EventHandler<PreviewPlaybackFailedEventArgs>? VideoFailed;
    public event EventHandler<PreviewPlaybackFailedEventArgs>? AudioFailed;
    public event EventHandler? VideoEnded;

    public void UpdateVideo(TimelinePreviewSegment segment, double timelinePosition, bool forceSeek)
    {
        ThrowIfDisposed();
        TimelinePosition = timelinePosition;
        var position = SegmentPosition(segment, timelinePosition);
        if (SamePath(_activeVideoSegment, segment))
        {
            if (_videoOpened && (forceSeek || Drift(_activeVideo, position) > 0.35))
                _activeVideo.Position = TimeSpan.FromSeconds(position);
            if (IsPlaying)
            {
                _activeVideo.Visibility = Visibility.Visible;
                _activeVideo.Play();
                VideoPresented?.Invoke(this, EventArgs.Empty);
            }
            return;
        }
        if (SamePath(_standbyVideoSegment, segment))
        {
            if (_standbyVideoReady && segment.Contains(timelinePosition)) PresentStandbyVideo(segment);
            return;
        }

        _standbyVideoSegment = segment;
        _standbyVideoReady = false;
        _standbyVideoPosition = position;
        ResetMedia(_standbyVideo);
        ConfigureVideo(_standbyVideo);
        _standbyVideo.Source = new Uri(segment.Path, UriKind.Absolute);
    }

    public void UpdateAudio(TimelinePreviewSegment segment, double timelinePosition, bool forceSeek)
    {
        ThrowIfDisposed();
        TimelinePosition = timelinePosition;
        var position = SegmentPosition(segment, timelinePosition);
        if (SamePath(_activeAudioSegment, segment))
        {
            if (_audioOpened && (forceSeek || Drift(_activeAudio, position) > 0.35))
                _activeAudio.Position = TimeSpan.FromSeconds(position);
            if (IsPlaying) _activeAudio.Play(); else _activeAudio.Pause();
            return;
        }
        if (SamePath(_standbyAudioSegment, segment))
        {
            if (_standbyAudioReady && segment.Contains(timelinePosition)) PresentStandbyAudio(segment);
            return;
        }

        _standbyAudioSegment = segment;
        _standbyAudioReady = false;
        _standbyAudioPosition = position;
        ResetMedia(_standbyAudio);
        ConfigureAudio(_standbyAudio);
        _standbyAudio.Source = new Uri(segment.Path, UriKind.Absolute);
    }

    public void SetPlaying(bool isPlaying)
    {
        ThrowIfDisposed();
        IsPlaying = isPlaying;
        if (isPlaying)
        {
            if (_videoOpened && _activeVideoSegment is not null)
            {
                _activeVideo.Visibility = Visibility.Visible;
                _activeVideo.Play();
                VideoPresented?.Invoke(this, EventArgs.Empty);
            }
            if (_audioOpened && _activeAudioSegment is not null) _activeAudio.Play();
        }
        else
        {
            _activeVideo.Pause();
            _standbyVideo.Pause();
            _activeAudio.Pause();
            _standbyAudio.Pause();
        }
    }

    public void SetTimelinePosition(double timelinePosition) => TimelinePosition = Math.Max(0, timelinePosition);

    public void HideVideo()
    {
        _activeVideo.Pause();
        _standbyVideo.Pause();
        _activeVideo.Visibility = Visibility.Collapsed;
        _standbyVideo.Visibility = Visibility.Collapsed;
    }

    public void ClearVideo()
    {
        ResetMedia(_activeVideo);
        ResetMedia(_standbyVideo);
        _activeVideo.Visibility = Visibility.Collapsed;
        _standbyVideo.Visibility = Visibility.Collapsed;
        _activeVideoSegment = null;
        _standbyVideoSegment = null;
        _standbyVideoReady = false;
        _videoOpened = false;
    }

    public void ClearAudio()
    {
        ResetMedia(_activeAudio);
        ResetMedia(_standbyAudio);
        _activeAudioSegment = null;
        _standbyAudioSegment = null;
        _standbyAudioReady = false;
        _audioOpened = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearVideo();
        ClearAudio();
        UnsubscribeVideo(_activeVideo);
        UnsubscribeVideo(_standbyVideo);
        UnsubscribeAudio(_activeAudio);
        UnsubscribeAudio(_standbyAudio);
        _disposed = true;
    }

    private async void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || player != _standbyVideo || _standbyVideoSegment is null) return;
        var segment = _standbyVideoSegment;
        if (!SourceMatches(player, segment)) return;
        player.Position = TimeSpan.FromSeconds(Math.Max(0, _standbyVideoPosition));
        player.Play();
        await Task.Delay(60);
        if (player != _standbyVideo || !SamePath(_standbyVideoSegment, segment) || !SourceMatches(player, segment)) return;
        _standbyVideoReady = true;
        if (!segment.Contains(TimelinePosition))
        {
            player.Pause();
            return;
        }
        PresentStandbyVideo(segment);
    }

    private void Audio_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement player || player != _standbyAudio || _standbyAudioSegment is null) return;
        var segment = _standbyAudioSegment;
        if (!SourceMatches(player, segment)) return;
        player.Position = TimeSpan.FromSeconds(Math.Max(0, _standbyAudioPosition));
        _standbyAudioReady = true;
        if (!segment.Contains(TimelinePosition))
        {
            player.Pause();
            return;
        }
        PresentStandbyAudio(segment);
    }

    private void PresentStandbyVideo(TimelinePreviewSegment segment)
    {
        if (!SamePath(_standbyVideoSegment, segment)) return;
        var position = SegmentPosition(segment, TimelinePosition);
        if (Drift(_standbyVideo, position) > 0.2) _standbyVideo.Position = TimeSpan.FromSeconds(position);
        var old = _activeVideo;
        old.Pause();
        old.Visibility = Visibility.Collapsed;
        _activeVideo = _standbyVideo;
        _activeVideoSegment = segment;
        _standbyVideo = old;
        _standbyVideoSegment = null;
        _standbyVideoReady = false;
        _videoOpened = true;
        _activeVideo.Visibility = IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        if (IsPlaying)
        {
            _activeVideo.Play();
            VideoPresented?.Invoke(this, EventArgs.Empty);
        }
        ResetMedia(_standbyVideo);
        ConfigureVideo(_standbyVideo);
    }

    private void PresentStandbyAudio(TimelinePreviewSegment segment)
    {
        if (!SamePath(_standbyAudioSegment, segment)) return;
        _standbyAudio.Position = TimeSpan.FromSeconds(SegmentPosition(segment, TimelinePosition));
        var old = _activeAudio;
        old.Pause();
        _activeAudio = _standbyAudio;
        _activeAudioSegment = segment;
        _standbyAudio = old;
        _standbyAudioSegment = null;
        _standbyAudioReady = false;
        _audioOpened = true;
        if (IsPlaying) _activeAudio.Play(); else _activeAudio.Pause();
        ResetMedia(_standbyAudio);
        ConfigureAudio(_standbyAudio);
    }

    private void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender == _activeVideo && IsPlaying) VideoEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement player) return;
        var segment = player == _standbyVideo ? _standbyVideoSegment : _activeVideoSegment;
        if (player == _standbyVideo)
        {
            _standbyVideoSegment = null;
            _standbyVideoReady = false;
        }
        else if (player == _activeVideo)
        {
            _activeVideoSegment = null;
            _videoOpened = false;
            player.Visibility = Visibility.Collapsed;
        }
        ResetMedia(player);
        ConfigureVideo(player);
        VideoFailed?.Invoke(this, new PreviewPlaybackFailedEventArgs(segment, e.ErrorException));
    }

    private void Audio_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement player) return;
        var segment = player == _standbyAudio ? _standbyAudioSegment : _activeAudioSegment;
        if (player == _standbyAudio)
        {
            _standbyAudioSegment = null;
            _standbyAudioReady = false;
        }
        else if (player == _activeAudio)
        {
            _activeAudioSegment = null;
            _audioOpened = false;
        }
        ResetMedia(player);
        ConfigureAudio(player);
        AudioFailed?.Invoke(this, new PreviewPlaybackFailedEventArgs(segment, e.ErrorException));
    }

    private void SubscribeVideo(MediaElement player)
    {
        player.MediaOpened += Video_MediaOpened;
        player.MediaEnded += Video_MediaEnded;
        player.MediaFailed += Video_MediaFailed;
    }

    private void UnsubscribeVideo(MediaElement player)
    {
        player.MediaOpened -= Video_MediaOpened;
        player.MediaEnded -= Video_MediaEnded;
        player.MediaFailed -= Video_MediaFailed;
    }

    private void SubscribeAudio(MediaElement player)
    {
        player.MediaOpened += Audio_MediaOpened;
        player.MediaFailed += Audio_MediaFailed;
    }

    private void UnsubscribeAudio(MediaElement player)
    {
        player.MediaOpened -= Audio_MediaOpened;
        player.MediaFailed -= Audio_MediaFailed;
    }

    private static void ConfigureVideo(MediaElement player)
    {
        player.IsMuted = true;
        player.Volume = 0;
    }

    private static void ConfigureAudio(MediaElement player)
    {
        player.IsMuted = false;
        player.Volume = 1;
        player.Balance = 0;
    }

    private static void ResetMedia(MediaElement player)
    {
        player.Stop();
        player.Source = null;
    }

    private static bool SamePath(TimelinePreviewSegment? left, TimelinePreviewSegment right)
        => left is not null && left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase);

    private static bool SourceMatches(MediaElement player, TimelinePreviewSegment segment)
        => player.Source is not null && Path.GetFullPath(player.Source.LocalPath)
            .Equals(Path.GetFullPath(segment.Path), StringComparison.OrdinalIgnoreCase);

    private static double SegmentPosition(TimelinePreviewSegment segment, double timelinePosition)
        => Math.Clamp(timelinePosition - segment.TimelineStart, 0, Math.Max(0, segment.Duration - 0.01));

    private static double Drift(MediaElement player, double position)
        => Math.Abs(player.Position.TotalSeconds - position);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class PreviewPlaybackFailedEventArgs(
    TimelinePreviewSegment? segment,
    Exception? error) : EventArgs
{
    public TimelinePreviewSegment? Segment { get; } = segment;
    public Exception? Error { get; } = error;
}
