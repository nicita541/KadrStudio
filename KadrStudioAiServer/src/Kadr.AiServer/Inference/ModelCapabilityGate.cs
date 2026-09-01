using System.Text.Json;
using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Inference;

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
    string QualificationKind = "montage-eval");

public sealed record ModelGateResult(bool IsAllowed, string Error, ModelCapabilityManifest? Manifest)
{
    public static ModelGateResult Allowed(ModelCapabilityManifest? manifest) => new(true, string.Empty, manifest);
    public static ModelGateResult Rejected(string error) => new(false, error, null);
}

public sealed class ModelCapabilityGate(AiServerOptions options)
{
    private static readonly string[] DevelopmentOnlyModelFragments =
    [
        "qwen3-vl:4b",
        "qwen3-vl-4b",
        "qwen3.5:9b",
        "qwen3.5-9b"
    ];

    private readonly string _root = Path.Combine(options.ProductionModelsRoot, "capabilities");

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
        return ModelGateResult.Allowed(manifest);
    }

    private static string SafeName(string model)
        => string.Concat(model.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_'));
}
