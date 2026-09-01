using System.Security.Cryptography;
using System.Text.Json;

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
        => (await PutAsync(content, "application/json", ".json", cancellationToken).ConfigureAwait(false)).Id;

    public async Task<StoredArtifact> PutFileAsync(
        string sourcePath,
        string mediaType,
        string extension,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var id = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        input.Position = 0;
        var directory = Path.Combine(_root, id[..2]);
        Directory.CreateDirectory(directory);
        var normalizedExtension = NormalizeExtension(extension);
        var path = Path.Combine(directory, id + normalizedExtension);
        if (!File.Exists(path))
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(
                                 temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
        var artifact = new StoredArtifact(id, path, mediaType, new FileInfo(path).Length, normalizedExtension);
        await WriteMetadataAsync(artifact, cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    public StoredArtifact? Find(string id)
    {
        var normalized = (id ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character))) return null;
        var directory = Path.Combine(_root, normalized[..2]);
        var metadataPath = Path.Combine(directory, normalized + ".meta.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<ArtifactMetadata>(File.ReadAllText(metadataPath));
                if (metadata is not null)
                {
                    var path = Path.Combine(directory, normalized + NormalizeExtension(metadata.Extension));
                    if (File.Exists(path))
                        return new StoredArtifact(normalized, path, metadata.MediaType, new FileInfo(path).Length,
                            NormalizeExtension(metadata.Extension));
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
        var legacy = Path.Combine(directory, normalized + ".json");
        return File.Exists(legacy)
            ? new StoredArtifact(normalized, legacy, "application/json", new FileInfo(legacy).Length, ".json")
            : null;
    }

    private async Task<StoredArtifact> PutAsync(
        byte[] content,
        string mediaType,
        string extension,
        CancellationToken cancellationToken)
    {
        var id = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var directory = Path.Combine(_root, id[..2]);
        Directory.CreateDirectory(directory);
        var normalizedExtension = NormalizeExtension(extension);
        var path = Path.Combine(directory, id + normalizedExtension);
        if (!File.Exists(path))
            await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
        var artifact = new StoredArtifact(id, path, mediaType, content.LongLength, normalizedExtension);
        await WriteMetadataAsync(artifact, cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    private static string NormalizeExtension(string extension)
    {
        var value = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension.Trim().ToLowerInvariant();
        if (!value.StartsWith('.')) value = "." + value;
        return value.Length <= 12 && value.Skip(1).All(char.IsLetterOrDigit) ? value : ".bin";
    }

    private static async Task WriteMetadataAsync(StoredArtifact artifact, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetDirectoryName(artifact.Path)!, artifact.Id + ".meta.json");
        if (File.Exists(path)) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ArtifactMetadata(artifact.MediaType, artifact.Extension));
        await File.WriteAllBytesAsync(path, payload, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ArtifactMetadata(string MediaType, string Extension);
}

public sealed record StoredArtifact(string Id, string Path, string MediaType, long Length, string Extension);
