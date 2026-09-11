using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;

namespace KadrStudio.Services;

public static class ArtifactStoreFactory
{
    public static DiskMediaArtifactCache Create(ArtifactStoreOptions options)
    {
        var cache = new DiskMediaArtifactCache(options);
        if (cache.IsOwned) return cache;
        cache.Dispose();

        // Legacy/unmarked content is preserved. Only a new owned namespace is
        // used; no ownership claim is made over an existing user's directory.
        var candidate = Path.Combine(options.Root, "KadrStudioCache");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cache = new DiskMediaArtifactCache(options with { Root = candidate, OwnershipId = Guid.Empty });
            if (cache.IsOwned) return cache;
            cache.Dispose();
            candidate = Path.Combine(options.Root, "KadrStudioCache-" + Guid.NewGuid().ToString("N"));
        }
        throw new IOException("Не удалось создать отдельный каталог управляемого кэша.");
    }
}
