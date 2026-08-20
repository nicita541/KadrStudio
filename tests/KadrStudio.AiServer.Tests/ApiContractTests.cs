using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KadrStudio.AiServer.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace KadrStudio.AiServer.Tests;

public sealed class ApiContractTests : IClassFixture<ApiContractFactory>
{
    private readonly ApiContractFactory _factory;

    public ApiContractTests(ApiContractFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/tags")]
    [InlineData("/api/chat")]
    [InlineData("/v1/agent/turn")]
    [InlineData("/v1/vision/analyze")]
    public async Task RemovedPublicRoutesReturnNotFound(string route)
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedRouteRequiresBearerApiKey()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync(
            "/v1/inference/structured",
            ValidRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("requires a valid Bearer API key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LiveHealthDoesNotRequireAuthentication()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void StandaloneServerDoesNotRegisterWindowsEventLogProvider()
    {
        var providerNames = _factory.Services
            .GetServices<ILoggerProvider>()
            .Select(provider => provider.GetType().FullName)
            .ToArray();

        Assert.DoesNotContain(
            providerNames,
            name => string.Equals(
                name,
                "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider",
                StringComparison.Ordinal));
        Assert.Contains(
            providerNames,
            name => string.Equals(
                name,
                "Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task StructuredInferenceRejectsNonObjectSchemaBeforeBackendCall()
    {
        using var client = _factory.CreateAuthorizedClient();
        var request = ValidRequest() with { Schema = JsonSerializer.SerializeToElement("json") };
        using var response = await client.PostAsJsonAsync("/v1/inference/structured", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("schema must be a JSON object", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StructuredInferenceRejectsEmptyPromptBeforeBackendCall()
    {
        using var client = _factory.CreateAuthorizedClient();
        var request = ValidRequest() with { UserPrompt = "" };
        using var response = await client.PostAsJsonAsync("/v1/inference/structured", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Prompt cannot be empty", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StructuredInferenceRejectsUnknownPublicModelBeforeBackendCall()
    {
        using var client = _factory.CreateAuthorizedClient();
        var request = ValidRequest() with { Model = "user-selected-private-model" };
        using var response = await client.PostAsJsonAsync("/v1/inference/structured", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown public AI model alias", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PlannerRoleRejectsImagesBeforeBackendCall()
    {
        using var client = _factory.CreateAuthorizedClient();
        var request = ValidRequest() with
        {
            Model = AiServerOptions.DefaultPlannerPublicModelAlias,
            Think = true,
            Images = ["base64-image"]
        };
        using var response = await client.PostAsJsonAsync("/v1/inference/structured", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not accept images", await response.Content.ReadAsStringAsync());
    }

    private static StructuredContractRequest ValidRequest()
        => new(
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { answer = new { type = "string" } },
                required = new[] { "answer" }
            }),
            "Return JSON.",
            "Answer the request.");

    private sealed record StructuredContractRequest(
        JsonElement Schema,
        string SystemPrompt,
        string UserPrompt,
        string[]? Images = null,
        string? Model = null,
        bool Think = false);
}

public sealed class ApiContractFactory : WebApplicationFactory<Program>
{
    private const string TestApiKey = "contract-test-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AiServerOptions>();
            services.AddSingleton(new AiServerOptions
            {
                OllamaEndpoint = new Uri("http://127.0.0.1:59999/"),
                BackendModel = "test-backend",
                PublicModelAlias = "test-model",
                ModelsRoot = Path.Combine(Path.GetTempPath(), "kadr-ai-contract-tests"),
                ApiKey = TestApiKey,
                ManageOllama = false,
                AutoPull = false,
                StartupTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(5),
                MaxRequestBodyBytes = 4 * 1024 * 1024,
                MaxImageCount = 4,
                MaxPromptCharacters = 10_000,
                ListenUrls = AiServerOptions.DefaultListenUrls
            });
        });
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }
}
