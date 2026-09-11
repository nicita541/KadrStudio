using KadrStudio.Application.Caching;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Caching;

namespace KadrStudio.Core.Tests;

public sealed class MediaArtifactCacheTests
{
    [Fact]
    public async Task Registered_project_source_cannot_be_evicted_automatically()
    {
        using var directory = new TemporaryCacheDirectory();
        await using var cache = new DiskMediaArtifactCache(directory.Path);
        var key = Key(Guid.NewGuid(), 0);
        await cache.PutAsync(key, new byte[] { 1, 2, 3 });
        var path = Directory.EnumerateFiles(directory.Path, "*.cache", SearchOption.AllDirectories).Single();
        cache.SetProtectedPaths([path]);

        await Assert.ThrowsAsync<IOException>(() => cache.TrimAsync(0));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Move_preserves_foreign_files_in_previous_owned_root()
    {
        using var directory = new TemporaryCacheDirectory();
        var root = Path.Combine(directory.Path, "owned");
        await using var cache = new DiskMediaArtifactCache(root);
        var key = Key(Guid.NewGuid(), 0);
        await cache.PutAsync(key, new byte[] { 1, 2, 3 });
        var foreign = Path.Combine(root, "original.mp4");
        await File.WriteAllTextAsync(foreign, "original");
        await cache.MoveAsync(Path.Combine(directory.Path, "destination"));

        Assert.Equal("original", await File.ReadAllTextAsync(foreign));
        Assert.Equal(new byte[] { 1, 2, 3 }, (await cache.TryGetAsync(key))!.Value.ToArray());
    }

    [Fact]
    public async Task Cleanup_refuses_junctions_without_following_them()
    {
        using var directory = new TemporaryCacheDirectory();
        var root = Path.Combine(directory.Path, "owned");
        var outside = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(outside);
        var foreign = Path.Combine(outside, "original.mp4");
        await File.WriteAllTextAsync(foreign, "original");
        await using var cache = new DiskMediaArtifactCache(root);
        var link = Path.Combine(root, "junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => cache.ClearAsync());
            await Assert.ThrowsAsync<IOException>(() => cache.TrimAsync(0));
            Assert.Equal("original", await File.ReadAllTextAsync(foreign));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("trim")]
    [InlineData("invalidate")]
    public async Task Destructive_operations_refuse_unowned_directories(string operation)
    {
        using var directory = new TemporaryCacheDirectory();
        var sourceId = Guid.NewGuid();
        var sourceDirectory = Path.Combine(directory.Path, sourceId.ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var foreign = Path.Combine(sourceDirectory, "family.mp4");
        await File.WriteAllTextAsync(foreign, "original media");
        await using var cache = new DiskMediaArtifactCache(directory.Path);

        await Assert.ThrowsAsync<IOException>(() => operation == "trim"
            ? cache.TrimAsync(0) : cache.InvalidateSourceAsync(sourceId));

        Assert.Equal("original media", await File.ReadAllTextAsync(foreign));
    }

    [Theory]
    [InlineData("trim")]
    [InlineData("invalidate")]
    [InlineData("clear")]
    public async Task Cleanup_preserves_foreign_files_inside_owned_cache(string operation)
    {
        using var directory = new TemporaryCacheDirectory();
        await using var cache = new DiskMediaArtifactCache(directory.Path);
        var sourceId = Guid.NewGuid();
        await cache.PutAsync(Key(sourceId, 1), new byte[] { 1, 2, 3 });
        var foreign = Path.Combine(directory.Path, sourceId.ToString("N"), "original.mp4");
        await File.WriteAllTextAsync(foreign, "keep original");

        if (operation == "trim") await cache.TrimAsync(0);
        else if (operation == "invalidate") await cache.InvalidateSourceAsync(sourceId);
        else await cache.ClearAsync();

        Assert.Equal("keep original", await File.ReadAllTextAsync(foreign));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".kadr-cache-owner.json")));
        Assert.Equal(0, (await cache.GetSnapshotAsync()).DiskEntries);
    }

    [Fact]
    public async Task Trim_survives_restart_and_does_not_resurrect_evicted_memory_entries()
    {
        using var directory = new TemporaryCacheDirectory();
        var key = Key(Guid.NewGuid(), 1);
        await using (var cache = new DiskMediaArtifactCache(directory.Path))
        {
            await cache.PutAsync(key, new byte[] { 4, 5, 6 });
            await cache.TrimAsync(0);
            Assert.Null(await cache.TryGetAsync(key));
        }
        await using var reopened = new DiskMediaArtifactCache(directory.Path);
        await reopened.PutAsync(key, new byte[] { 7 });
        await reopened.ClearAsync();
        Assert.Null(await reopened.TryGetAsync(key));
    }

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
    public async Task User_selected_folder_is_only_a_parent_and_foreign_files_survive_clear()
    {
        using var directory = new TemporaryCacheDirectory();
        var original = Path.Combine(directory.Path, "original");
        var selected = Path.Combine(directory.Path, "videos");
        Directory.CreateDirectory(selected);
        var foreign = Path.Combine(selected, "family-video.mp4");
        await File.WriteAllTextAsync(foreign, "not cache data");
        await using var store = new DiskMediaArtifactCache(original, 1024 * 1024);
        var key = Key(Guid.NewGuid(), 1);
        await store.PutAsync(key, new byte[] { 1, 2, 3 });

        await store.MoveAsync(selected);
        await store.ClearAsync();

        Assert.Equal(Path.GetFullPath(Path.Combine(selected, "KadrStudioCache")), store.Options.Root);
        Assert.Equal("not cache data", await File.ReadAllTextAsync(foreign));
        Assert.False(Directory.Exists(original));
    }

    [Fact]
    public async Task Move_refuses_a_non_empty_target_without_overwriting_it()
    {
        using var directory = new TemporaryCacheDirectory();
        var original = Path.Combine(directory.Path, "original");
        var selected = Path.Combine(directory.Path, "videos");
        var target = Path.Combine(selected, "KadrStudioCache");
        Directory.CreateDirectory(target);
        var foreign = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(foreign, "foreign");
        await using var store = new DiskMediaArtifactCache(original, 1024 * 1024);
        var key = Key(Guid.NewGuid(), 1);
        await store.PutAsync(key, new byte[] { 1, 2, 3 });

        await Assert.ThrowsAsync<IOException>(() => store.MoveAsync(selected));

        Assert.Equal("foreign", await File.ReadAllTextAsync(foreign));
        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.TryGetAsync(key))!.Value.ToArray());
    }

    [Fact]
    public async Task Clear_refuses_when_ownership_marker_is_missing()
    {
        using var directory = new TemporaryCacheDirectory();
        var root = Path.Combine(directory.Path, "owned");
        await using var store = new DiskMediaArtifactCache(root, 1024 * 1024);
        await store.PutAsync(Key(Guid.NewGuid(), 1), new byte[] { 1, 2, 3 });
        var foreign = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(foreign, "foreign");
        File.Delete(Path.Combine(root, ".kadr-cache-owner.json"));

        await Assert.ThrowsAsync<IOException>(() => store.ClearAsync());

        Assert.Equal("foreign", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task Move_from_an_unowned_legacy_root_leaves_it_untouched_and_starts_a_new_owned_cache()
    {
        using var directory = new TemporaryCacheDirectory();
        var root = Path.Combine(directory.Path, "legacy");
        var selected = Path.Combine(directory.Path, "videos");
        await using var store = new DiskMediaArtifactCache(root, 1024 * 1024);
        var legacyFile = Path.Combine(root, "legacy.cache");
        await File.WriteAllTextAsync(legacyFile, "leave me");
        File.Delete(Path.Combine(root, ".kadr-cache-owner.json"));

        await store.MoveAsync(selected);
        await store.ClearAsync();

        Assert.Equal("leave me", await File.ReadAllTextAsync(legacyFile));
        Assert.Equal(Path.GetFullPath(Path.Combine(selected, "KadrStudioCache")), store.Options.Root);
    }

    [Fact]
    public async Task Clear_refuses_a_forged_ownership_marker()
    {
        using var directory = new TemporaryCacheDirectory();
        var root = Path.Combine(directory.Path, "owned");
        await using var store = new DiskMediaArtifactCache(root, 1024 * 1024);
        await store.PutAsync(Key(Guid.NewGuid(), 1), new byte[] { 1, 2, 3 });
        var marker = Path.Combine(root, ".kadr-cache-owner.json");
        await File.WriteAllTextAsync(marker, "{\"schemaVersion\":1,\"product\":\"KadrStudio\",\"ownerId\":\"00000000-0000-0000-0000-000000000001\"}");

        await Assert.ThrowsAsync<IOException>(() => store.ClearAsync());
    }

    [Fact]
    public void Drive_root_cannot_be_configured_as_an_artifact_cache()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath());

        Assert.False(string.IsNullOrWhiteSpace(driveRoot));
        Assert.Throws<IOException>(() => new DiskMediaArtifactCache(driveRoot!, 1024 * 1024));
    }

    [Fact]
    public async Task Moved_cache_reopens_with_the_same_owner_and_artifacts()
    {
        using var directory = new TemporaryCacheDirectory();
        var original = Path.Combine(directory.Path, "original");
        var selected = Path.Combine(directory.Path, "videos");
        var key = Key(Guid.NewGuid(), 1);
        ArtifactStoreOptions movedOptions;
        await using (var store = new DiskMediaArtifactCache(original, 1024 * 1024))
        {
            await store.PutAsync(key, new byte[] { 1, 2, 3 });
            await store.MoveAsync(selected);
            movedOptions = store.Options;
        }

        await using var reopened = new DiskMediaArtifactCache(movedOptions);

        Assert.Equal(new byte[] { 1, 2, 3 }, (await reopened.TryGetAsync(key))!.Value.ToArray());
        await reopened.ClearAsync();
    }

    [Theory]
    [InlineData("source-video.mp4")]
    [InlineData("project.kadr")]
    public async Task Clear_refuses_when_a_source_or_project_path_is_inside_the_cache(string protectedName)
    {
        using var directory = new TemporaryCacheDirectory();
        var root = Path.Combine(directory.Path, "owned");
        await using var store = new DiskMediaArtifactCache(root, 1024 * 1024);
        var protectedPath = Path.Combine(root, protectedName);
        await File.WriteAllTextAsync(protectedPath, "must survive");

        await Assert.ThrowsAsync<IOException>(() => store.ClearAsync([protectedPath]));

        Assert.Equal("must survive", await File.ReadAllTextAsync(protectedPath));
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
