using KadrStudio.AiServer.Configuration;

namespace KadrStudio.AiServer.Tests;

public sealed class AiServerOptionsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11436/", true)]
    [InlineData("http://localhost:11436/", true)]
    [InlineData("https://127.0.0.1:11436/", false)]
    [InlineData("http://192.168.1.50:11436/", false)]
    public void AutomaticOllamaManagementIsRestrictedToLocalHttp(
        string endpoint,
        bool expected)
    {
        var options = CreateOptions(new Uri(endpoint));

        Assert.Equal(expected, options.CanManageConfiguredBackend());
    }

    [Fact]
    public void PublicModelAliasesResolveToIsolatedPlannerAndVisionRoles()
    {
        var options = CreateOptions(new Uri("http://127.0.0.1:11436/"));

        var planner = options.ResolveModel(AiServerOptions.DefaultPlannerPublicModelAlias);
        var vision = options.ResolveModel(options.PublicModelAlias);

        Assert.Equal("planner", planner.Role);
        Assert.False(planner.RequiresVision);
        Assert.Equal(AiServerOptions.DefaultPlannerBackendModel, planner.BackendModel);
        Assert.Equal("vision", vision.Role);
        Assert.True(vision.RequiresVision);
        Assert.Throws<InvalidOperationException>(() => options.ResolveModel("unmanaged-model"));
    }

    private static AiServerOptions CreateOptions(Uri endpoint)
        => new()
        {
            OllamaEndpoint = endpoint,
            BackendModel = "server-model",
            PublicModelAlias = "kadr-vision:latest",
            ModelsRoot = Path.Combine(Path.GetTempPath(), "kadr-ai-tests"),
            ManageOllama = true,
            AutoPull = false,
            StartupTimeout = TimeSpan.FromSeconds(5),
            RequestTimeout = TimeSpan.FromMinutes(1),
            MaxRequestBodyBytes = 32 * 1024 * 1024,
            MaxImageCount = 8,
            MaxPromptCharacters = 100_000,
            ListenUrls = AiServerOptions.DefaultListenUrls
        };
}
