using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Caching;

public sealed record MediaPyramidLevel(int Level, TimelineTime BucketDuration, long BucketCount);

/// <summary>
/// Chooses a deterministic cache level from the visible time range. At every zoom
/// level the UI requests approximately the same number of buckets, while the
/// underlying samples become progressively more detailed.
/// </summary>
public sealed class MediaPyramid(
    TimelineTime sourceDuration,
    int targetBuckets = 256,
    TimelineTime? finestBucket = null)
{
    private readonly TimelineTime _sourceDuration = sourceDuration > TimelineTime.Zero
        ? sourceDuration
        : throw new ArgumentOutOfRangeException(nameof(sourceDuration));
    private readonly int _targetBuckets = targetBuckets is >= 32 and <= 4096
        ? targetBuckets
        : throw new ArgumentOutOfRangeException(nameof(targetBuckets));
    private readonly TimelineTime _finestBucket = finestBucket ?? TimelineTime.FromSeconds(0.01);

    public MediaPyramidLevel SelectLevel(TimeRange visibleRange)
    {
        var desired = Math.Max(
            _finestBucket.Ticks,
            (long)Math.Ceiling(visibleRange.Duration.Ticks / (double)_targetBuckets));
        var level = 0;
        var bucketTicks = _finestBucket.Ticks;
        while (bucketTicks < desired && bucketTicks <= long.MaxValue / 2)
        {
            bucketTicks *= 2;
            level++;
        }
        var bucketDuration = new TimelineTime(bucketTicks);
        var count = Math.Max(1, (long)Math.Ceiling(_sourceDuration.Ticks / (double)bucketTicks));
        return new MediaPyramidLevel(level, bucketDuration, count);
    }

    public (long First, long Last) GetVisibleBuckets(TimeRange visibleRange, MediaPyramidLevel level)
    {
        var first = Math.Max(0, visibleRange.Start.Ticks / level.BucketDuration.Ticks);
        var last = Math.Min(
            level.BucketCount - 1,
            Math.Max(first, (visibleRange.End.Ticks - 1) / level.BucketDuration.Ticks));
        return (first, last);
    }
}
