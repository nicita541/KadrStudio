using System.Security.Cryptography;

namespace KadrStudio.AiServer.Storage;

public sealed class ContentAddressedArtifactStore
{
    private readonly string _root;

    public ContentAddressedArtifactStore(string dataRoot)
    {
        _root = Path.GetFullPath(Path.Combine(dataRoot, "artifacts"));
        Directory.CreateDirectory(_root);
    }

    public async Task<string> PutJsonAsync(byte[] content, CancellationToken cancellationToken)
    {
        var id = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var directory = Path.Combine(_root, id[..2]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".json");
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public string? Find(string id)
    {
        var normalized = (id ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character))) return null;
        var path = Path.Combine(_root, normalized[..2], normalized + ".json");
        return File.Exists(path) ? path : null;
    }
}
