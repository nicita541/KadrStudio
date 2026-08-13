using KadrStudio.Application.Caching;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Caching;

namespace KadrStudio.Core.Tests;

public sealed class MediaArtifactCacheTests
{
    [Fact]
    public async Task Artifacts_survive_restart_and_are_verified()
    {
        using var directory = new TemporaryCacheDirectory();
        var key = Key(Guid.NewGuid(), segment: 4);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await using (var cache = new DiskMediaArtifactCache(directory.Path, 1024 * 1024))
            await cache.PutAsync(key, payload);

        await using var reopened = new DiskMediaArtifactCache(directory.Path, 1024 * 1024);
        var loaded = await reopened.TryGetAsync(key);
        Assert.NotNull(loaded);
        Assert.Equal(payload, loaded.Value.ToArray());
    }

    [Fact]
    public async Task Source_invalidation_does_not_remove_other_sources()
    {
        using var directory = new TemporaryCacheDirectory();
        await using var cache = new DiskMediaArtifactCache(directory.Path, 1024 * 1024);
        var firstSource = Guid.NewGuid();
        var secondSource = Guid.NewGuid();
        var first = Key(firstSource, 0);
        var second = Key(secondSource, 0);
        await cache.PutAsync(first, new byte[] { 1 });
        await cache.PutAsync(second, new byte[] { 2 });

        await cache.InvalidateSourceAsync(firstSource);

        Assert.Null(await cache.TryGetAsync(first));
        Assert.Equal(new byte[] { 2 }, (await cache.TryGetAsync(second))!.Value.ToArray());
    }

    [Fact]
    public async Task Disk_trim_removes_old_artifacts_to_target()
    {
        using var directory = new TemporaryCacheDirectory();
        await using var cache = new DiskMediaArtifactCache(directory.Path, 1024 * 1024);
        var source = Guid.NewGuid();
        for (var index = 0; index < 4; index++)
            await cache.PutAsync(Key(source, index), new byte[1024]);

        await cache.TrimAsync(1500);

        var snapshot = await cache.GetSnapshotAsync();
        Assert.True(snapshot.DiskBytes <= 1500, $"Disk cache has {snapshot.DiskBytes} bytes.");
    }

    [Fact]
    public void Pyramid_increases_detail_as_visible_range_narrows()
    {
        var pyramid = new MediaPyramid(TimelineTime.FromSeconds(3600), targetBuckets: 100);
        var full = pyramid.SelectLevel(new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(3600)));
        var minute = pyramid.SelectLevel(new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(60)));
        var tenSeconds = pyramid.SelectLevel(new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(10)));

        Assert.True(full.BucketDuration > minute.BucketDuration);
        Assert.True(minute.BucketDuration > tenSeconds.BucketDuration);
        var visible = pyramid.GetVisibleBuckets(new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(10)), tenSeconds);
        Assert.InRange(visible.Last - visible.First + 1, 50, 100);
    }

    private static MediaCacheKey Key(Guid sourceId, long segment)
        => new(sourceId, "fingerprint", MediaArtifactKind.Waveform, 2, segment);

    private sealed class TemporaryCacheDirectory : IDisposable
    {
        public TemporaryCacheDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KadrStudio", "cache-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
