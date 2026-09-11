using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using Xunit;

namespace KadrStudio.Core.Tests;

public sealed class ArtifactPinTests
{
    [Fact]
    public async Task Pins_survive_maintenance_until_last_release_and_block_move()
    {
        var root = Path.Combine(Path.GetTempPath(), "kadr-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var cache = new DiskMediaArtifactCache(new ArtifactStoreOptions(Path.Combine(root, "cache"), DiskBudgetBytes: 1024 * 1024));
            var key = new MediaCacheKey(Guid.NewGuid(), "source", MediaArtifactKind.ProxyVideo, 0, 0);
            var source = Path.Combine(root, "payload.mp4");
            await File.WriteAllBytesAsync(source, new byte[2 * 1024 * 1024]);
            using var first = await cache.PinAsync(key, ".mp4");
            var second = await cache.PinAsync(key, ".mp4");
            var path = await cache.PutFileAsync(key, source, ".mp4");
            Assert.True(File.Exists(path)); // Put's automatic trim must honor reservations.
            await cache.TrimAsync(0);
            await cache.ClearAsync();
            await cache.InvalidateSourceAsync(key.SourceId);
            await using (var other = new DiskMediaArtifactCache(cache.Options.Root))
                await other.TrimAsync(0);
            Assert.Equal(path, await cache.TryGetPayloadPathAsync(key, ".mp4"));
            await Assert.ThrowsAsync<IOException>(() => cache.MoveAsync(Path.Combine(root, "destination")));
            second.Dispose();
            second.Dispose();
            await cache.TrimAsync(0);
            Assert.True(File.Exists(path));
            first.Dispose();
            await cache.TrimAsync(0);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".sha256"));
        }
        finally { Directory.Delete(root, true); }
    }
}
