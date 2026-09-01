using System.Security.Cryptography;
using System.Text;

namespace KadrStudio.Application.Caching;

public enum MediaArtifactKind
{
    Thumbnail,
    TimelineThumbnail,
    Waveform,
    ProxyVideo,
    ConformedAudio,
    RenderedPreview,
    AnalysisFrame,
    SceneFingerprint,
    SubtitleAudio,
    AnalysisManifest,
    AgentObservation
}

public readonly record struct MediaCacheKey(
    Guid SourceId,
    string SourceFingerprint,
    MediaArtifactKind Kind,
    int Level,
    long Segment,
    int FormatVersion = 1)
{
    public string StableHash
    {
        get
        {
            var value = $"{SourceId:N}|{SourceFingerprint}|{(int)Kind}|{Level}|{Segment}|{FormatVersion}";
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
    }
}

public sealed record MediaCacheSnapshot(long MemoryBytes, long DiskBytes, int MemoryEntries, int DiskEntries);

public sealed record ArtifactStoreOptions(
    string Root,
    long DiskBudgetBytes = 8L * 1024 * 1024 * 1024,
    long MemoryBudgetBytes = 128L * 1024 * 1024)
{
    public ArtifactStoreOptions Normalize()
    {
        if (string.IsNullOrWhiteSpace(Root)) throw new ArgumentException("An artifact root is required.", nameof(Root));
        if (DiskBudgetBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(DiskBudgetBytes));
        if (MemoryBudgetBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MemoryBudgetBytes));
        return this with { Root = Path.GetFullPath(Root) };
    }
}

public interface IMediaArtifactCache : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(MediaCacheKey key, CancellationToken cancellationToken = default);
    ValueTask PutAsync(MediaCacheKey key, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task InvalidateSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task TrimAsync(long targetDiskBytes, CancellationToken cancellationToken = default);
    Task<MediaCacheSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public interface IArtifactStore : IMediaArtifactCache
{
    ArtifactStoreOptions Options { get; }
    string GetPayloadPath(MediaCacheKey key, string extension);
    Task<string?> TryGetPayloadPathAsync(
        MediaCacheKey key,
        string extension,
        CancellationToken cancellationToken = default);
    Task<string> PutFileAsync(
        MediaCacheKey key,
        string sourcePath,
        string extension,
        CancellationToken cancellationToken = default);
    Task MoveAsync(string newRoot, CancellationToken cancellationToken = default);
    Task SetDiskBudgetAsync(long diskBudgetBytes, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
