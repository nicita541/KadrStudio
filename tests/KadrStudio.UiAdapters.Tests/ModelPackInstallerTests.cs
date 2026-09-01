using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class ModelPackInstallerTests
{
    [Fact]
    public async Task Verified_installation_marker_and_file_sizes_are_recognized_without_download()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kadr-model-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, ".kadr-ai", "models", "tiny"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "KadrStudio.sln"), string.Empty);
            await File.WriteAllBytesAsync(Path.Combine(root, ".kadr-ai", "models", "tiny", "model.gguf"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(root, "config", "ai-model-pack.production.json"),
                """
                {"schemaVersion":1,"id":"test-pack","displayName":"Test","modelBudgetBytes":30,"aiRootBudgetBytes":45,
                 "artifacts":[{"id":"vision","displayName":"Tiny","repository":"test/repo",
                 "revision":"0000000000000000000000000000000000000000","targetDirectory":"tiny",
                 "files":[{"path":"model.gguf","size":3,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}]}
                """);
            await File.WriteAllTextAsync(Path.Combine(root, ".kadr-ai", "model-pack.installation.json"),
                """
                {"schemaVersion":1,"manifestId":"test-pack","installedAt":"2026-09-01T00:00:00Z","payloadBytes":3}
                """);

            var status = await new PowerShellModelPackInstaller(root).GetStatusAsync();

            Assert.True(status.IsInstalled);
            Assert.Equal(3, status.InstalledBytes);
            Assert.Empty(status.MissingOrInvalidPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Pinned_anime_pack_has_exact_budget_and_no_runtime_quantization_artifacts()
    {
        var plan = await new PowerShellModelPackInstaller().GetPlanAsync();

        Assert.Equal("kadr-anime-production-2026-08", plan.Id);
        Assert.Equal(27_561_133_051, plan.TotalSizeBytes);
        Assert.True(plan.TotalSizeBytes < plan.ModelBudgetBytes);
        Assert.Equal(18, plan.Artifacts.Length);
        Assert.All(plan.Artifacts, artifact =>
        {
            Assert.Equal(64, artifact.Sha256.Length);
            Assert.True(artifact.SizeBytes > 0);
            if (!artifact.IsLocal) Assert.Equal(40, artifact.Revision.Length);
            Assert.DoesNotContain("snapshot", artifact.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bf16", artifact.RelativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fp16", artifact.RelativePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(plan.Artifacts, artifact => artifact.Id == "vision" && artifact.RelativePath.EndsWith("Q4_K_M.gguf", StringComparison.Ordinal));
        Assert.Contains(plan.Artifacts, artifact => artifact.Id == "upscaler" && artifact.IsExperimental);
    }
}
