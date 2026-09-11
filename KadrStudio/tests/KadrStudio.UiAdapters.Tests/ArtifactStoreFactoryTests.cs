using KadrStudio.Application.Caching;
using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class ArtifactStoreFactoryTests
{
    [Fact]
    public async Task Unmarked_cache_uses_owned_child_and_preserves_legacy_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "legacy-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var legacy = Path.Combine(root, "unknown.txt");
        await File.WriteAllTextAsync(legacy, "preserve");
        try
        {
            await using var cache = ArtifactStoreFactory.Create(new ArtifactStoreOptions(root));
            Assert.NotEqual(root, cache.Options.Root);
            Assert.True(cache.IsOwned);
            await cache.ClearAsync();
            Assert.Equal("preserve", await File.ReadAllTextAsync(legacy));
            Assert.False(File.Exists(Path.Combine(root, ".kadr-cache-owner.json")));
            await using var reopened = ArtifactStoreFactory.Create(new ArtifactStoreOptions(root));
            Assert.Equal(cache.Options.Root, reopened.Options.Root);
            Assert.Equal(cache.Options.OwnershipId, reopened.Options.OwnershipId);
        }
        finally { Directory.Delete(root, true); }
    }
}
