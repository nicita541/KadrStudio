using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>
/// Owns one editor preview session. It coordinates cache generations and in-flight
/// renders, while keeping video and audio state, cancellation and invalidation fully separate.
/// The UI only consumes ready timeline segments and never manages render-cache keys.
/// </summary>
public sealed class TimelinePreviewSession(PreviewCompositionService renderer) : IDisposable
{
    private readonly object _videoSync = new();
    private readonly object _audioSync = new();
    private readonly Dictionary<double, TimelinePreviewSegment> _videoSegments = [];
    private readonly Dictionary<double, TimelinePreviewSegment> _audioSegments = [];
    private readonly Dictionary<double, Task<TimelinePreviewSegment>> _videoJobs = [];
    private readonly Dictionary<double, Task<TimelinePreviewSegment>> _audioJobs = [];
    private CancellationTokenSource _videoGeneration = new();
    private CancellationTokenSource _audioGeneration = new();
    private string? _videoSignature;
    private string? _audioSignature;
    private long _videoRevision = -1;
    private long _audioRevision = -1;
    private bool _halfQuality;
    private bool _disposed;

    public TimelinePreviewSegment? TryGetVideo(
        EditorProject project,
        double timelinePosition,
        bool halfQuality)
    {
        var signature = EnsureVideoGeneration(project, halfQuality);
        lock (_videoSync)
        {
            return FindSegment(_videoSegments, signature, timelinePosition);
        }
    }

    public TimelinePreviewSegment? TryGetAudio(EditorProject project, double timelinePosition)
    {
        var signature = EnsureAudioGeneration(project);
        lock (_audioSync)
        {
            return FindSegment(_audioSegments, signature, timelinePosition);
        }
    }

    public bool IsCurrentVideo(EditorProject project, bool halfQuality, string signature)
        => string.Equals(EnsureVideoGeneration(project, halfQuality), signature, StringComparison.Ordinal);

    public async Task<TimelinePreviewSegment> EnsureVideoAsync(
        EditorProject project,
        double timelinePosition,
        bool halfQuality,
        CancellationToken cancellationToken = default)
    {
        var signature = EnsureVideoGeneration(project, halfQuality);
        var bucket = SegmentBucket(timelinePosition);
        Task<TimelinePreviewSegment> job;
        CancellationToken generationToken;
        lock (_videoSync)
        {
            if (FindSegment(_videoSegments, signature, timelinePosition) is { } ready) return ready;
            generationToken = _videoGeneration.Token;
            if (!_videoJobs.TryGetValue(bucket, out job!))
            {
                job = renderer.EnsureVideoSegmentAsync(project, timelinePosition, halfQuality, generationToken);
                _videoJobs[bucket] = job;
            }
        }

        try
        {
            var segment = await job.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_videoSync)
            {
                if (!string.Equals(segment.Signature, _videoSignature, StringComparison.Ordinal))
                    throw new OperationCanceledException("Устаревшее поколение видеопредпросмотра отброшено.");
                _videoSegments[segment.TimelineStart] = segment;
            }
            return segment;
        }
        finally
        {
            lock (_videoSync)
            {
                if (_videoJobs.TryGetValue(bucket, out var current) && ReferenceEquals(current, job))
                    _videoJobs.Remove(bucket);
            }
        }
    }

    public async Task<TimelinePreviewSegment> EnsureAudioAsync(
        EditorProject project,
        double timelinePosition,
        CancellationToken cancellationToken = default)
    {
        var signature = EnsureAudioGeneration(project);
        var bucket = SegmentBucket(timelinePosition);
        Task<TimelinePreviewSegment> job;
        CancellationToken generationToken;
        lock (_audioSync)
        {
            if (FindSegment(_audioSegments, signature, timelinePosition) is { } ready) return ready;
            generationToken = _audioGeneration.Token;
            if (!_audioJobs.TryGetValue(bucket, out job!))
            {
                job = renderer.EnsureAudioSegmentAsync(project, timelinePosition, generationToken);
                _audioJobs[bucket] = job;
            }
        }

        try
        {
            var segment = await job.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_audioSync)
            {
                if (!string.Equals(segment.Signature, _audioSignature, StringComparison.Ordinal))
                    throw new OperationCanceledException("Устаревшее поколение аудиопредпросмотра отброшено.");
                _audioSegments[segment.TimelineStart] = segment;
            }
            return segment;
        }
        finally
        {
            lock (_audioSync)
            {
                if (_audioJobs.TryGetValue(bucket, out var current) && ReferenceEquals(current, job))
                    _audioJobs.Remove(bucket);
            }
        }
    }

    public Task<CompositedStillFrame> EnsureStillAsync(
        EditorProject project,
        double timelinePosition,
        bool halfQuality,
        CancellationToken cancellationToken = default)
    {
        EnsureVideoGeneration(project, halfQuality);
        return renderer.EnsureStillFrameAsync(project, timelinePosition, halfQuality, cancellationToken);
    }

    public void InvalidateVideo(string? failedFile = null)
    {
        lock (_videoSync)
        {
            if (!string.IsNullOrWhiteSpace(failedFile)) renderer.InvalidateCachedFile(failedFile);
            StartNextVideoGeneration(null);
        }
    }

    public void InvalidateAudio(string? failedFile = null)
    {
        lock (_audioSync)
        {
            if (!string.IsNullOrWhiteSpace(failedFile)) renderer.InvalidateCachedFile(failedFile);
            StartNextAudioGeneration(null);
        }
    }

    public void Reset()
    {
        lock (_videoSync) StartNextVideoGeneration(null);
        lock (_audioSync) StartNextAudioGeneration(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        _videoGeneration.Dispose();
        _audioGeneration.Dispose();
    }

    private string EnsureVideoGeneration(EditorProject project, bool halfQuality)
    {
        ThrowIfDisposed();
        lock (_videoSync)
        {
            if (_videoSignature is not null && _videoRevision == project.VideoRevision && _halfQuality == halfQuality)
                return _videoSignature;
        }
        var signature = renderer.GetVideoSignature(project, halfQuality);
        lock (_videoSync)
        {
            if (!string.Equals(signature, _videoSignature, StringComparison.Ordinal))
                StartNextVideoGeneration(signature);
            _videoRevision = project.VideoRevision;
            _halfQuality = halfQuality;
            return signature;
        }
    }

    private string EnsureAudioGeneration(EditorProject project)
    {
        ThrowIfDisposed();
        lock (_audioSync)
        {
            if (_audioSignature is not null && _audioRevision == project.AudioRevision)
                return _audioSignature;
        }
        var signature = renderer.GetAudioSignature(project);
        lock (_audioSync)
        {
            if (!string.Equals(signature, _audioSignature, StringComparison.Ordinal))
                StartNextAudioGeneration(signature);
            _audioRevision = project.AudioRevision;
            return signature;
        }
    }

    private void StartNextVideoGeneration(string? signature)
    {
        _videoGeneration.Cancel();
        _videoGeneration.Dispose();
        _videoGeneration = new CancellationTokenSource();
        _videoSignature = signature;
        _videoRevision = -1;
        _videoSegments.Clear();
        _videoJobs.Clear();
    }

    private void StartNextAudioGeneration(string? signature)
    {
        _audioGeneration.Cancel();
        _audioGeneration.Dispose();
        _audioGeneration = new CancellationTokenSource();
        _audioSignature = signature;
        _audioRevision = -1;
        _audioSegments.Clear();
        _audioJobs.Clear();
    }

    private static TimelinePreviewSegment? FindSegment(
        IReadOnlyDictionary<double, TimelinePreviewSegment> segments,
        string signature,
        double timelinePosition)
        => segments.Values
            .Where(segment => string.Equals(segment.Signature, signature, StringComparison.Ordinal) &&
                              segment.Contains(timelinePosition) && File.Exists(segment.Path))
            .OrderByDescending(segment => segment.TimelineStart)
            .FirstOrDefault();

    private static double SegmentBucket(double timelinePosition)
        => Math.Floor(Math.Max(0, timelinePosition) / PreviewCompositionService.SegmentStep) *
           PreviewCompositionService.SegmentStep;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
