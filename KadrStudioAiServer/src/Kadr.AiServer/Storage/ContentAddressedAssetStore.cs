using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace KadrStudio.AiServer.Storage;

public sealed record StoredAsset(
    string Id,
    string Path,
    long Length,
    string MediaType,
    bool IsComplete);

public sealed class ContentAddressedAssetStore
{
    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public ContentAddressedAssetStore(string dataRoot, long maximumBytes)
    {
        _root = Path.GetFullPath(Path.Combine(dataRoot, "assets"));
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredAsset> AppendAsync(
        string contentHash,
        long totalBytes,
        long offset,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken)
    {
        var id = NormalizeHash(contentHash);
        if (totalBytes <= 0 || totalBytes > _maximumBytes)
            throw new AssetUploadException("asset_size_invalid", $"Asset size must be between 1 and {_maximumBytes} bytes.");
        if (offset < 0 || offset > totalBytes)
            throw new AssetUploadException("asset_offset_invalid", "Asset offset is outside the declared upload.");
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(_root, id[..2]);
            Directory.CreateDirectory(directory);
            var finalPath = Path.Combine(directory, id + ".blob");
            var metadataPath = Path.Combine(directory, id + ".media-type");
            if (File.Exists(finalPath))
            {
                var length = new FileInfo(finalPath).Length;
                if (length != totalBytes)
                    throw new AssetUploadException("asset_hash_collision", "Existing asset length differs from the declared length.");
                return new StoredAsset(id, finalPath, length, ReadMediaType(metadataPath, mediaType), true);
            }

            var partialPath = Path.Combine(directory, id + ".upload");
            var current = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (current != offset)
                throw new AssetUploadException("asset_offset_mismatch", $"Server expects offset {current}, not {offset}.");
            await using (var output = new FileStream(
                             partialPath, FileMode.Append, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            current = new FileInfo(partialPath).Length;
            if (current > totalBytes)
            {
                File.Delete(partialPath);
                throw new AssetUploadException("asset_too_large", "Uploaded bytes exceed the declared asset length.");
            }
            if (current < totalBytes)
                return new StoredAsset(id, partialPath, current, NormalizeMediaType(mediaType), false);

            var actual = await ComputeHashAsync(partialPath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(id, StringComparison.Ordinal))
            {
                File.Delete(partialPath);
                throw new AssetUploadException("asset_hash_mismatch", "Completed upload does not match its SHA-256 id.");
            }
            File.Move(partialPath, finalPath);
            await File.WriteAllTextAsync(metadataPath, NormalizeMediaType(mediaType), cancellationToken).ConfigureAwait(false);
            return new StoredAsset(id, finalPath, current, NormalizeMediaType(mediaType), true);
        }
        finally
        {
            gate.Release();
        }
    }

    public StoredAsset? Find(string contentHash)
    {
        var id = NormalizeHash(contentHash);
        var directory = Path.Combine(_root, id[..2]);
        var path = Path.Combine(directory, id + ".blob");
        if (!File.Exists(path)) return null;
        return new StoredAsset(
            id, path, new FileInfo(path).Length,
            ReadMediaType(Path.Combine(directory, id + ".media-type"), "application/octet-stream"), true);
    }

    public StoredAsset? FindUpload(string contentHash)
    {
        var id = NormalizeHash(contentHash);
        var directory = Path.Combine(_root, id[..2]);
        var finalPath = Path.Combine(directory, id + ".blob");
        if (File.Exists(finalPath))
            return new StoredAsset(
                id, finalPath, new FileInfo(finalPath).Length,
                ReadMediaType(Path.Combine(directory, id + ".media-type"), "application/octet-stream"), true);
        var partialPath = Path.Combine(directory, id + ".upload");
        return File.Exists(partialPath)
            ? new StoredAsset(
                id, partialPath, new FileInfo(partialPath).Length,
                "application/octet-stream", false)
            : null;
    }

    private static string NormalizeHash(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new AssetUploadException("asset_hash_invalid", "Asset id must be a lowercase or uppercase SHA-256 hex digest.");
        return normalized;
    }

    private static string NormalizeMediaType(string value)
        => string.IsNullOrWhiteSpace(value) ? "application/octet-stream" : value.Trim()[..Math.Min(value.Trim().Length, 200)];

    private static string ReadMediaType(string path, string fallback)
        => File.Exists(path) ? File.ReadAllText(path).Trim() : NormalizeMediaType(fallback);

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class AssetUploadException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
