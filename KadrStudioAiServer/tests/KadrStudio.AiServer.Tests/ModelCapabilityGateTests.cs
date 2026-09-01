using System.Text.Json;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Inference;

namespace KadrStudio.AiServer.Tests;

public sealed class ModelCapabilityGateTests
{
    [Theory]
    [InlineData("qwen3-vl:4b-instruct", "Director")]
    [InlineData("Qwen/Qwen3-VL-4B-Instruct", "VideoUnderstanding")]
    [InlineData("qwen3.5:9b", "Critic")]
    [InlineData("Qwen/Qwen3.5-9B", "Critic")]
    public async Task Development_models_cannot_receive_production_roles_even_with_forged_manifest(
        string model,
        string role)
    {
        using var root = new TemporaryModelRoot();
        root.WriteManifest(Approved(model, role));
        var gate = new ModelCapabilityGate(CreateOptions(root.Path));

        var result = await gate.CheckAsync(model, role, "anime-episode", true, CancellationToken.None);

        Assert.False(result.IsAllowed);
        Assert.Contains("development-only", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_candidate_requires_manifest_and_passed_eval()
    {
        using var root = new TemporaryModelRoot();
        const string model = "Qwen/Qwen3-30B-A3B-Instruct-2507";
        var gate = new ModelCapabilityGate(CreateOptions(root.Path));

        var missing = await gate.CheckAsync(
            model, "Director", "anime-episode", true, CancellationToken.None);
        Assert.False(missing.IsAllowed);
        Assert.Contains("no montage-eval", missing.Error, StringComparison.OrdinalIgnoreCase);

        root.WriteManifest(Approved(model, "Director") with { MontageEvalPassed = false });
        var failed = await gate.CheckAsync(
            model, "Director", "anime-episode", true, CancellationToken.None);
        Assert.False(failed.IsAllowed);
        Assert.Contains("has not passed", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Passed_manifest_allows_only_declared_role_and_profile()
    {
        using var root = new TemporaryModelRoot();
        const string model = "Qwen/Qwen3-30B-A3B-Instruct-2507";
        root.WriteManifest(Approved(model, "Director"));
        var gate = new ModelCapabilityGate(CreateOptions(root.Path));

        var allowed = await gate.CheckAsync(
            model, "Director", "anime-episode", true, CancellationToken.None);
        var wrongRole = await gate.CheckAsync(
            model, "Critic", "anime-episode", true, CancellationToken.None);
        var wrongProfile = await gate.CheckAsync(
            model, "Director", "podcast", true, CancellationToken.None);

        Assert.True(allowed.IsAllowed, allowed.Error);
        Assert.False(wrongRole.IsAllowed);
        Assert.False(wrongProfile.IsAllowed);
    }

    [Fact]
    public async Task Pinned_first_release_qualification_allows_only_anime_vision()
    {
        using var root = new TemporaryModelRoot();
        const string model = AiServerOptions.DefaultVisionBackendModel;
        root.WriteManifest(Approved(model, "VideoUnderstanding") with
        {
            MontageEvalPassed = false,
            QualificationKind = "pinned-anime-exact"
        });
        var gate = new ModelCapabilityGate(CreateOptions(root.Path));

        var allowed = await gate.CheckAsync(
            model, "VideoUnderstanding", "anime-episode", true, CancellationToken.None);
        var wrongProfile = await gate.CheckAsync(
            model, "VideoUnderstanding", "generic", true, CancellationToken.None);
        var wrongRole = await gate.CheckAsync(
            model, "Director", "anime-episode", true, CancellationToken.None);

        Assert.True(allowed.IsAllowed, allowed.Error);
        Assert.False(wrongProfile.IsAllowed);
        Assert.False(wrongRole.IsAllowed);
    }

    [Fact]
    public async Task Pinned_director_qualification_allows_only_anime_director_brief()
    {
        using var root = new TemporaryModelRoot();
        const string model = AiServerOptions.DefaultPlannerBackendModel;
        root.WriteManifest(Approved(model, "Director") with
        {
            MontageEvalPassed = false,
            QualificationKind = "pinned-anime-director"
        });
        var gate = new ModelCapabilityGate(CreateOptions(root.Path));

        var allowed = await gate.CheckAsync(
            model, "Director", "anime-episode", true, CancellationToken.None);
        var wrongProfile = await gate.CheckAsync(
            model, "Director", "generic", true, CancellationToken.None);
        var roughCut = await gate.CheckAsync(
            model, "RoughCut", "anime-episode", true, CancellationToken.None);

        Assert.True(allowed.IsAllowed, allowed.Error);
        Assert.False(wrongProfile.IsAllowed);
        Assert.False(roughCut.IsAllowed);
    }

    private static ModelCapabilityManifest Approved(string model, string role)
        => new(
            model,
            "sha256:test-model-hash",
            "qwen-tokenizer-v1",
            32_768,
            [role],
            ["anime-episode"],
            "anime-eval-v1",
            MontageEvalPassed: true,
            ProductionApproved: true,
            DateTimeOffset.UtcNow);

    private static AiServerOptions CreateOptions(string root)
        => new()
        {
            PlannerBackendModel = AiServerOptions.DefaultPlannerBackendModel,
            PlannerPublicModelAlias = AiServerOptions.DefaultPlannerPublicModelAlias,
            ProductionModelsRoot = root,
            DataRoot = System.IO.Path.Combine(root, "data"),
            WorkersRoot = System.IO.Path.Combine(root, "workers"),
            MaxRequestBodyBytes = 8 * 1024 * 1024,
            MaxImageCount = 8,
            MaxPromptCharacters = 100_000
        };

    private sealed class TemporaryModelRoot : IDisposable
    {
        public TemporaryModelRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kadr-model-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "capabilities"));
        }

        public string Path { get; }

        public void WriteManifest(ModelCapabilityManifest manifest)
        {
            var name = string.Concat(manifest.Model.Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
            File.WriteAllText(
                System.IO.Path.Combine(Path, "capabilities", name + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
