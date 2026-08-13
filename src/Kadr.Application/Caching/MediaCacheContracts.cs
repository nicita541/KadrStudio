using System.Security.Cryptography;
using System.Text;

namespace KadrStudio.Application.Caching;

public enum MediaArtifactKind
{
    Thumbnail,
    Waveform,
    ProxyVideo,
    AnalysisFrame,
    SceneFingerprint,
    SubtitleAudio
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

public interface IMediaArtifactCache : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(MediaCacheKey key, CancellationToken cancellationToken = default);
    ValueTask PutAsync(MediaCacheKey key, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task InvalidateSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);
    Task TrimAsync(long targetDiskBytes, CancellationToken cancellationToken = default);
    Task<MediaCacheSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
