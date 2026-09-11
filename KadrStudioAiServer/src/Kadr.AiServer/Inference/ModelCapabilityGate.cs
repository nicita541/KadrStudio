using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Inference;

public sealed record VerifiedModelIdentity(
    string Path,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    string Revision);

public sealed record ModelCapabilityManifest(
    string Model,
    string ModelHash,
    string Tokenizer,
    int ContextWindowTokens,
    string[] SupportedRoles,
    string[] SupportedProfiles,
    string MontageEvalRevision,
    bool MontageEvalPassed,
    bool ProductionApproved,
    DateTimeOffset EvaluatedAt,
    string QualificationKind = "montage-eval",
    VerifiedModelIdentity? VerifiedIdentity = null);

public sealed record RuntimeModelIdentity(
    string Path,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    string Revision)
{
    public string Fingerprint => $"{Path}|{SizeBytes}|{LastWriteTimeUtc.UtcTicks}|{Sha256}|{Revision}";
}

public sealed record ModelGateResult(bool IsAllowed, string Error, ModelCapabilityManifest? Manifest)
{
    public static ModelGateResult Allowed(ModelCapabilityManifest? manifest) => new(true, string.Empty, manifest);
    public static ModelGateResult Rejected(string error) => new(false, error, null);
}

public sealed class ModelCapabilityGate
{
    private static readonly string[] DevelopmentOnlyModelFragments =
    [
        "qwen3-vl:4b",
        "qwen3-vl-4b",
        "qwen3.5:9b",
        "qwen3.5-9b"
    ];

    private readonly string _modelsRoot;
    private readonly string _root;
    private readonly ModelIdentityVerifier _identityVerifier = new();

    internal int HashComputationCount => _identityVerifier.HashComputationCount;

    public ModelCapabilityGate(AiServerOptions options)
    {
        _modelsRoot = Path.GetFullPath(options.ProductionModelsRoot);
        _root = Path.Combine(_modelsRoot, "capabilities");
    }

    public async Task<ModelGateResult> CheckAsync(
        string model,
        string role,
        string profile,
        bool requireProduction,
        CancellationToken cancellationToken)
    {
        if (requireProduction && DevelopmentOnlyModelFragments.Any(fragment =>
                model.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return ModelGateResult.Rejected(
                $"Model '{model}' is development-only and cannot be assigned a production montage role.");
        var path = Path.Combine(_root, SafeName(model) + ".json");
        if (!File.Exists(path))
            return requireProduction
                ? ModelGateResult.Rejected($"Model '{model}' has no montage-eval capability manifest.")
                : ModelGateResult.Allowed(null);
        ModelCapabilityManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModelCapabilityManifest>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return ModelGateResult.Rejected("Model capability manifest is invalid: " + exception.Message);
        }
        if (manifest is null || !manifest.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            return ModelGateResult.Rejected("Model capability manifest identity does not match the configured model.");
        if (string.IsNullOrWhiteSpace(manifest.ModelHash) || string.IsNullOrWhiteSpace(manifest.Tokenizer) ||
            manifest.ContextWindowTokens < 2_048)
            return ModelGateResult.Rejected("Model capability manifest is incomplete.");
        if (!manifest.SupportedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return ModelGateResult.Rejected($"Model '{model}' is not approved for role '{role}'.");
        if (!string.IsNullOrWhiteSpace(profile) &&
            !manifest.SupportedProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
            return ModelGateResult.Rejected($"Model '{model}' is not approved for profile '{profile}'.");
        var pinnedAnimeExact =
            string.Equals(manifest.QualificationKind, "pinned-anime-exact", StringComparison.OrdinalIgnoreCase) &&
            model.Equals(AiServerOptions.DefaultVisionBackendModel, StringComparison.OrdinalIgnoreCase) &&
            role.Equals("VideoUnderstanding", StringComparison.OrdinalIgnoreCase) &&
            profile.Equals("anime-episode", StringComparison.OrdinalIgnoreCase) &&
            manifest.ProductionApproved &&
            !string.IsNullOrWhiteSpace(manifest.MontageEvalRevision);
        var pinnedAnimeDirector =
            string.Equals(manifest.QualificationKind, "pinned-anime-director", StringComparison.OrdinalIgnoreCase) &&
            model.Equals(AiServerOptions.DefaultPlannerBackendModel, StringComparison.OrdinalIgnoreCase) &&
            role.Equals("Director", StringComparison.OrdinalIgnoreCase) &&
            profile.Equals("anime-episode", StringComparison.OrdinalIgnoreCase) &&
            manifest.ProductionApproved &&
            !string.IsNullOrWhiteSpace(manifest.MontageEvalRevision);
        var montageEvalQualified = manifest.ProductionApproved && manifest.MontageEvalPassed &&
                                   !string.IsNullOrWhiteSpace(manifest.MontageEvalRevision);
        if (requireProduction && !pinnedAnimeExact && !pinnedAnimeDirector && !montageEvalQualified)
            return ModelGateResult.Rejected($"Model '{model}' has not passed the required production montage-eval.");
        if (requireProduction)
        {
            if (manifest.VerifiedIdentity is null ||
                string.IsNullOrWhiteSpace(manifest.VerifiedIdentity.Revision))
                return ModelGateResult.Rejected("Production model capability manifest has no verified model identity.");
            if (!IsInsideModelsRoot(manifest.VerifiedIdentity.Path))
                return ModelGateResult.Rejected("Verified model path escapes the production model root.");
            if (!HashesEqual(manifest.ModelHash, manifest.VerifiedIdentity.Sha256))
                return ModelGateResult.Rejected("Model capability hash does not match its verified identity.");
            var verified = await _identityVerifier.VerifyAsync(
                manifest.VerifiedIdentity, cancellationToken).ConfigureAwait(false);
            if (!verified.IsValid)
                return ModelGateResult.Rejected(verified.Error);
        }
        return ModelGateResult.Allowed(manifest);
    }

    public Task<RuntimeIdentityResult> ResolveRuntimeIdentityAsync(
        string path,
        string revision,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsInsideModelsRoot(path))
            return Task.FromResult(RuntimeIdentityResult.Rejected(
                "Model path escapes the production model root."));
        return _identityVerifier.ResolveAsync(path, revision, expectedSha256, cancellationToken);
    }

    private bool IsInsideModelsRoot(string path)
    {
        var canonical = Path.GetFullPath(path);
        return canonical.Equals(_modelsRoot, StringComparison.OrdinalIgnoreCase) ||
               canonical.StartsWith(_modelsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashesEqual(string left, string right)
        => NormalizeHash(left).Equals(NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string hash)
        => hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? hash[7..] : hash;

    private static string SafeName(string model)
        => string.Concat(model.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_'));
}

public sealed record RuntimeIdentityResult(bool IsValid, string Error, RuntimeModelIdentity? Identity)
{
    public static RuntimeIdentityResult Valid(RuntimeModelIdentity identity) => new(true, string.Empty, identity);
    public static RuntimeIdentityResult Rejected(string error) => new(false, error, null);
}

internal sealed class ModelIdentityVerifier
{
    private readonly ConcurrentDictionary<string, CachedHash> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _hashComputationCount;

    internal int HashComputationCount => Volatile.Read(ref _hashComputationCount);

    public async Task<RuntimeIdentityResult> VerifyAsync(
        VerifiedModelIdentity expected,
        CancellationToken cancellationToken)
    {
        var current = ReadMetadata(expected.Path);
        if (current is null)
            return RuntimeIdentityResult.Rejected($"Verified model payload is missing: {expected.Path}");
        var expectedHash = NormalizeHash(expected.Sha256);
        if (current.Value.SizeBytes == expected.SizeBytes &&
            current.Value.LastWriteTimeUtc.UtcTicks == expected.LastWriteTimeUtc.UtcTicks)
        {
            return RuntimeIdentityResult.Valid(new RuntimeModelIdentity(
                current.Value.Path, current.Value.SizeBytes, current.Value.LastWriteTimeUtc,
                expectedHash, expected.Revision));
        }
        return await ResolveAsync(expected.Path, expected.Revision, expectedHash, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<RuntimeIdentityResult> ResolveAsync(
        string path,
        string revision,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(revision))
            return RuntimeIdentityResult.Rejected("Model revision is required.");
        var metadata = ReadMetadata(path);
        if (metadata is null)
            return RuntimeIdentityResult.Rejected($"Model payload is missing: {path}");
        var key = metadata.Value.Path;
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            metadata = ReadMetadata(path);
            if (metadata is null)
                return RuntimeIdentityResult.Rejected($"Model payload is missing: {path}");
            if (!_hashes.TryGetValue(key, out var cached) ||
                cached.SizeBytes != metadata.Value.SizeBytes ||
                cached.LastWriteTimeUtc.UtcTicks != metadata.Value.LastWriteTimeUtc.UtcTicks)
            {
                Interlocked.Increment(ref _hashComputationCount);
                cached = new CachedHash(
                    metadata.Value.SizeBytes,
                    metadata.Value.LastWriteTimeUtc,
                    await ComputeHashAsync(metadata.Value.Path, cancellationToken).ConfigureAwait(false));
                _hashes[key] = cached;
            }
            if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                !cached.Sha256.Equals(NormalizeHash(expectedSha256), StringComparison.OrdinalIgnoreCase))
                return RuntimeIdentityResult.Rejected(
                    $"Model payload SHA-256 mismatch for '{metadata.Value.Path}'.");
            return RuntimeIdentityResult.Valid(new RuntimeModelIdentity(
                metadata.Value.Path, metadata.Value.SizeBytes, metadata.Value.LastWriteTimeUtc,
                cached.Sha256, revision.Trim()));
        }
        catch (IOException exception)
        {
            return RuntimeIdentityResult.Rejected("Could not verify model payload: " + exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ModelMetadata? ReadMetadata(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (File.Exists(canonical))
        {
            var file = new FileInfo(canonical);
            return new ModelMetadata(canonical, file.Length, file.LastWriteTimeUtc);
        }
        if (!Directory.Exists(canonical)) return null;
        var files = EnumerateModelFiles(canonical).ToArray();
        if (files.Length == 0) return null;
        return new ModelMetadata(
            canonical,
            files.Sum(file => file.Length),
            files.Max(file => new DateTimeOffset(file.LastWriteTimeUtc)));
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumerateModelFiles(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(path, file.FullName);
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            await using var stream = new FileStream(
                file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<FileInfo> EnumerateModelFiles(string root)
        => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(file => !file.Name.Equals("kadr-snapshot.json", StringComparison.OrdinalIgnoreCase) &&
                           !file.Name.EndsWith(".kadr-part", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeHash(string hash)
        => hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? hash[7..] : hash;

    private readonly record struct ModelMetadata(string Path, long SizeBytes, DateTimeOffset LastWriteTimeUtc);
    private sealed record CachedHash(long SizeBytes, DateTimeOffset LastWriteTimeUtc, string Sha256);
}
