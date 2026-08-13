using System.Windows;
using System.Windows.Threading;
using KadrStudio.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;

namespace KadrStudio.Playback;

/// <summary>
/// Dedicated LibVLC playback adapter. Video and audio use independent decoders;
/// changing one stream cannot replace, mute or reset the other stream.
/// Timeline composition is still prepared by the render pipeline, while this
/// class only owns decoding, seeking and presentation.
/// </summary>
public sealed class PreviewPlaybackController : IDisposable
{
    private readonly VideoView _videoView;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _videoPlayer;
    private readonly MediaPlayer _audioPlayer;
    private TimelinePreviewSegment? _videoSegment;
    private TimelinePreviewSegment? _audioSegment;
    private Media? _videoMedia;
    private Media? _audioMedia;
    private double _pendingVideoPosition;
    private double _pendingAudioPosition;
    private bool _disposed;

    public PreviewPlaybackController(VideoView videoView)
    {
        ArgumentNullException.ThrowIfNull(videoView);
        var nativeDirectory = ResolveNativeDirectory();
        LibVLCSharp.Shared.Core.Initialize(nativeDirectory);
        _videoView = videoView;
        _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--no-snapshot-preview");
        _videoPlayer = new MediaPlayer(_libVlc) { Mute = true, Volume = 0 };
        _audioPlayer = new MediaPlayer(_libVlc) { Mute = false, Volume = 100 };
        _videoView.MediaPlayer = _videoPlayer;
        _videoPlayer.Playing += VideoPlayer_Playing;
        _videoPlayer.EndReached += VideoPlayer_EndReached;
        _videoPlayer.EncounteredError += VideoPlayer_EncounteredError;
        _audioPlayer.Playing += AudioPlayer_Playing;
        _audioPlayer.EncounteredError += AudioPlayer_EncounteredError;
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
        TimelinePosition = Math.Max(0, timelinePosition);
        _pendingVideoPosition = SegmentPosition(segment, TimelinePosition);
        if (!SamePath(_videoSegment, segment))
        {
            _videoSegment = segment;
            ReplaceVideoMedia(segment.Path);
            return;
        }
        if (forceSeek || Drift(_videoPlayer, _pendingVideoPosition) > 0.25)
            Seek(_videoPlayer, _pendingVideoPosition);
        if (IsPlaying && !_videoPlayer.IsPlaying) _videoPlayer.Play();
    }

    public void UpdateAudio(TimelinePreviewSegment segment, double timelinePosition, bool forceSeek)
    {
        ThrowIfDisposed();
        TimelinePosition = Math.Max(0, timelinePosition);
        _pendingAudioPosition = SegmentPosition(segment, TimelinePosition);
        if (!SamePath(_audioSegment, segment))
        {
            _audioSegment = segment;
            ReplaceAudioMedia(segment.Path);
            return;
        }
        if (forceSeek || Drift(_audioPlayer, _pendingAudioPosition) > 0.25)
            Seek(_audioPlayer, _pendingAudioPosition);
        if (IsPlaying && !_audioPlayer.IsPlaying) _audioPlayer.Play();
    }

    public void SetPlaying(bool isPlaying)
    {
        ThrowIfDisposed();
        IsPlaying = isPlaying;
        if (isPlaying)
        {
            if (_videoMedia is not null)
            {
                _videoView.Visibility = Visibility.Visible;
                if (!_videoPlayer.IsPlaying) _videoPlayer.Play();
            }
            if (_audioMedia is not null && !_audioPlayer.IsPlaying) _audioPlayer.Play();
        }
        else
        {
            if (_videoPlayer.CanPause) _videoPlayer.Pause();
            if (_audioPlayer.CanPause) _audioPlayer.Pause();
        }
    }

    public void SetTimelinePosition(double timelinePosition)
        => TimelinePosition = Math.Max(0, timelinePosition);

    public void HideVideo()
    {
        ThrowIfDisposed();
        if (_videoPlayer.CanPause) _videoPlayer.Pause();
        _videoView.Visibility = Visibility.Collapsed;
    }

    public void ClearVideo()
    {
        if (_disposed) return;
        _videoPlayer.Stop();
        _videoPlayer.Media = null;
        _videoMedia?.Dispose();
        _videoMedia = null;
        _videoSegment = null;
        _videoView.Visibility = Visibility.Collapsed;
    }

    public void ClearAudio()
    {
        if (_disposed) return;
        _audioPlayer.Stop();
        _audioPlayer.Media = null;
        _audioMedia?.Dispose();
        _audioMedia = null;
        _audioSegment = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ClearVideo();
        ClearAudio();
        _videoPlayer.Playing -= VideoPlayer_Playing;
        _videoPlayer.EndReached -= VideoPlayer_EndReached;
        _videoPlayer.EncounteredError -= VideoPlayer_EncounteredError;
        _audioPlayer.Playing -= AudioPlayer_Playing;
        _audioPlayer.EncounteredError -= AudioPlayer_EncounteredError;
        _videoView.MediaPlayer = null;
        _videoPlayer.Dispose();
        _audioPlayer.Dispose();
        _libVlc.Dispose();
        _disposed = true;
    }

    private void ReplaceVideoMedia(string path)
    {
        var next = CreateMedia(path);
        _videoPlayer.Stop();
        _videoPlayer.Media = next;
        _videoMedia?.Dispose();
        _videoMedia = next;
        if (IsPlaying)
        {
            _videoView.Visibility = Visibility.Visible;
            _videoPlayer.Play();
        }
        else
        {
            _videoPlayer.Play();
            _videoPlayer.SetPause(true);
        }
    }

    private void ReplaceAudioMedia(string path)
    {
        var next = CreateMedia(path);
        _audioPlayer.Stop();
        _audioPlayer.Media = next;
        _audioMedia?.Dispose();
        _audioMedia = next;
        if (IsPlaying) _audioPlayer.Play();
        else
        {
            _audioPlayer.Play();
            _audioPlayer.SetPause(true);
        }
    }

    private Media CreateMedia(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Preview segment was not found.", fullPath);
        return new Media(_libVlc, fullPath, FromType.FromPath);
    }

    public static string ResolveNativeDirectory(string? applicationDirectory = null)
    {
        var root = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "win-x64",
            System.Runtime.InteropServices.Architecture.X86 => "win-x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            var value => throw new PlatformNotSupportedException($"LibVLC is not packaged for {value}.")
        };
        var directory = Path.Combine(root, "libvlc", architecture);
        if (!File.Exists(Path.Combine(directory, "libvlc.dll")) ||
            !File.Exists(Path.Combine(directory, "libvlccore.dll")) ||
            !Directory.Exists(Path.Combine(directory, "plugins")))
        {
            throw new FileNotFoundException(
                $"LibVLC runtime is incomplete for {architecture}. Rebuild the release package.", directory);
        }
        return directory;
    }

    private void VideoPlayer_Playing(object? sender, EventArgs e)
    {
        Seek(_videoPlayer, _pendingVideoPosition);
        if (!IsPlaying) _videoPlayer.SetPause(true);
        Dispatch(() =>
        {
            if (IsPlaying) _videoView.Visibility = Visibility.Visible;
            VideoPresented?.Invoke(this, EventArgs.Empty);
        });
    }

    private void AudioPlayer_Playing(object? sender, EventArgs e)
    {
        Seek(_audioPlayer, _pendingAudioPosition);
        if (!IsPlaying) _audioPlayer.SetPause(true);
    }

    private void VideoPlayer_EndReached(object? sender, EventArgs e)
        => Dispatch(() => { if (IsPlaying) VideoEnded?.Invoke(this, EventArgs.Empty); });

    private void VideoPlayer_EncounteredError(object? sender, EventArgs e)
        => Dispatch(() => VideoFailed?.Invoke(this, new PreviewPlaybackFailedEventArgs(_videoSegment, null)));

    private void AudioPlayer_EncounteredError(object? sender, EventArgs e)
        => Dispatch(() => AudioFailed?.Invoke(this, new PreviewPlaybackFailedEventArgs(_audioSegment, null)));

    private static void Seek(MediaPlayer player, double seconds)
        => player.Time = Math.Max(0, (long)Math.Round(seconds * 1000));

    private static double Drift(MediaPlayer player, double seconds)
        => Math.Abs(player.Time / 1000.0 - seconds);

    private static bool SamePath(TimelinePreviewSegment? left, TimelinePreviewSegment right)
        => left is not null && Path.GetFullPath(left.Path).Equals(Path.GetFullPath(right.Path), StringComparison.OrdinalIgnoreCase);

    private static double SegmentPosition(TimelinePreviewSegment segment, double timelinePosition)
        => Math.Clamp(timelinePosition - segment.TimelineStart, 0, Math.Max(0, segment.Duration - 0.01));

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Render);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

public sealed class PreviewPlaybackFailedEventArgs(
    TimelinePreviewSegment? segment,
    Exception? error) : EventArgs
{
    public TimelinePreviewSegment? Segment { get; } = segment;
    public Exception? Error { get; } = error;
}
