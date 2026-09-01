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
    public async Task Direct_payload_is_checksum_verified_and_corruption_is_rejected()
    {
        using var directory = new TemporaryCacheDirectory();
        await using var store = new DiskMediaArtifactCache(new ArtifactStoreOptions(
            directory.Path, 16 * 1024 * 1024, 1024 * 1024));
        var source = Path.Combine(directory.Path, "source.mp4");
        await File.WriteAllBytesAsync(source, Enumerable.Range(0, 4096).Select(item => (byte)item).ToArray());
        var key = new MediaCacheKey(Guid.NewGuid(), "fingerprint", MediaArtifactKind.ProxyVideo, 0, 0, 2);
        var path = await store.PutFileAsync(key, source, ".mp4");

        Assert.Equal(path, await store.TryGetPayloadPathAsync(key, ".mp4"));
        await File.WriteAllBytesAsync(path, [9, 9, 9]);

        Assert.Null(await store.TryGetPayloadPathAsync(key, ".mp4"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Artifact_store_can_move_to_user_selected_root()
    {
        using var directory = new TemporaryCacheDirectory();
        var original = Path.Combine(directory.Path, "original");
        var destination = Path.Combine(directory.Path, "moved");
        await using var store = new DiskMediaArtifactCache(original, 1024 * 1024);
        var key = Key(Guid.NewGuid(), 1);
        await store.PutAsync(key, new byte[] { 1, 2, 3 });

        await store.MoveAsync(destination);

        Assert.Equal(Path.GetFullPath(destination), store.Options.Root);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.TryGetAsync(key))!.Value.ToArray());
        Assert.False(Directory.Exists(original));
    }

    [Fact]
    public async Task Artifact_store_applies_new_lru_budget_immediately()
    {
        using var directory = new TemporaryCacheDirectory();
        await using var store = new DiskMediaArtifactCache(new ArtifactStoreOptions(
            directory.Path, 16 * 1024 * 1024, 1024 * 1024));
        for (var index = 0; index < 8; index++)
            await store.PutAsync(Key(Guid.NewGuid(), index), new byte[256 * 1024]);

        await store.SetDiskBudgetAsync(1024 * 1024);

        Assert.Equal(1024 * 1024, store.Options.DiskBudgetBytes);
        Assert.True((await store.GetSnapshotAsync()).DiskBytes <= 1024 * 1024);
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
