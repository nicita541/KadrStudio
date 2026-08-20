using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class AiServerClientTests
{
    [Fact]
    public async Task Default_endpoint_discovers_server_managed_model()
    {
        var handler = new FakeAiServerHandler();
        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(), new ProcessRunner(), new AiServerClientOptions(), handler);

        var model = Assert.Single(await service.GetModelsAsync());

        Assert.Equal("127.0.0.1", service.Endpoint.Host);
        Assert.Equal(5080, service.Endpoint.Port);
        Assert.Equal(AiVideoAnalysisService.DefaultServerModelAlias, model.Name);
        Assert.True(model.SupportsVision);
        Assert.Contains(handler.Requests, request => request.Path == "/v1/models");
        Assert.DoesNotContain(handler.Requests, request => request.Path.StartsWith("/api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Configured_server_uses_bearer_auth_and_structured_inference()
    {
        var handler = new FakeAiServerHandler("qwen-cloud");
        var options = new AiServerClientOptions(
            new Uri("https://ai.example.test/"), "secret-token", "qwen-cloud");
        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(), new ProcessRunner(), options, handler);

        var model = Assert.Single(await service.GetModelsAsync());
        await service.VerifyModelAsync(model.Name);

        Assert.True(model.ServerManaged);
        Assert.True(model.SupportsVision);
        Assert.Equal("qwen-cloud", model.Name);
        Assert.Contains("сервер", model.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler.Requests, request => request.Path == "/v1/inference/structured");
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.Scheme);
            Assert.Equal("secret-token", request.Parameter);
        });
    }

    [Fact]
    public async Task Readiness_probe_uses_schema_and_never_sends_model_or_ollama_fields()
    {
        var handler = new FakeAiServerHandler();
        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(), new ProcessRunner(), new AiServerClientOptions(), handler);

        await service.VerifyModelAsync(AiVideoAnalysisService.DefaultServerModelAlias);

        using var document = JsonDocument.Parse(handler.LastInferenceBody!);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("schema").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("model", out _));
        Assert.False(document.RootElement.TryGetProperty("messages", out _));
        Assert.False(document.RootElement.TryGetProperty("think", out _));
    }

    [Fact]
    public async Task Real_default_server_discovers_model_and_runs_probe_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_STUDIO_RUN_AI_SERVER_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            new AiServerClientOptions());

        var model = Assert.Single(await service.GetModelsAsync());
        Assert.Equal(AiVideoAnalysisService.DefaultServerModelAlias, model.Name);
        Assert.True(model.SupportsVision);
        await service.VerifyModelAsync(model.Name);
    }

    private sealed class FakeAiServerHandler : HttpMessageHandler
    {
        private readonly string _model;

        public FakeAiServerHandler(string model = AiVideoAnalysisService.DefaultServerModelAlias)
        {
            _model = model;
        }

        public List<(string Path, string? Scheme, string? Parameter)> Requests { get; } = [];
        public string? LastInferenceBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add((path, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            if (path == "/v1/inference/structured")
                LastInferenceBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            var json = path switch
            {
                "/health/live" => "{\"status\":\"live\"}",
                "/v1/models" => JsonSerializer.Serialize(new
                {
                    models = new[]
                    {
                        new { id = _model, capabilities = new[] { "structured-output", "vision" }, managed = true }
                    }
                }),
                "/v1/inference/structured" => JsonSerializer.Serialize(new
                {
                    content = "{\"status\":\"ok\"}",
                    doneReason = "stop",
                    evalCount = 8
                }),
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
