using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Workers;
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
    [InlineData("/v1/inference/structured")]
    [InlineData("/v1/models")]
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
            "/v2/jobs",
            new AnalyzerJobRequest("video-understanding", "2", [new string('a', 64)],
                JsonSerializer.SerializeToElement(new { })));

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
    public async Task Accelerator_release_is_an_authenticated_v2_operation()
    {
        using var anonymous = _factory.CreateClient();
        using var denied = await anonymous.PostAsync("/v2/accelerator/release", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsync("/v2/accelerator/release", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("released", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_rejects_unqualified_planner_and_vision_models()
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("plannerModelError", body);
        Assert.Contains("visionModelError", body);
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
    public async Task V2AssetUploadIsResumableAndContentAddressed()
    {
        using var client = _factory.CreateAuthorizedClient();
        var content = Encoding.UTF8.GetBytes("analysis-proxy");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        using var first = new ByteArrayContent(content[..5]);
        first.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        first.Headers.Add("X-Kadr-Content-Sha256", hash);
        first.Headers.Add("X-Kadr-Total-Length", content.Length.ToString());
        first.Headers.Add("X-Kadr-Offset", "0");
        using var firstResponse = await client.PostAsync("/v2/assets", first);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var status = await client.GetFromJsonAsync<AssetUploadResponse>(
            $"/v2/assets/{hash}?totalBytes={content.Length}");
        Assert.NotNull(status);
        Assert.Equal(5, status!.ReceivedBytes);
        Assert.False(status.IsComplete);

        using var second = new ByteArrayContent(content[5..]);
        second.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        second.Headers.Add("X-Kadr-Content-Sha256", hash);
        second.Headers.Add("X-Kadr-Total-Length", content.Length.ToString());
        second.Headers.Add("X-Kadr-Offset", "5");
        using var secondResponse = await client.PostAsync("/v2/assets", second);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var uploaded = await secondResponse.Content.ReadFromJsonAsync<AssetUploadResponse>();
        Assert.NotNull(uploaded);
        Assert.Equal(hash, uploaded.AssetId);
        Assert.True(uploaded.IsComplete);
    }

    [Fact]
    public async Task V2ReasoningRejectsMeasuredTokenizerOverflowBeforeInference()
    {
        using var client = _factory.CreateAuthorizedClient();
        var request = new RoleStructuredReasoningRequest(
            ReasoningRole.Director,
            JsonSerializer.SerializeToElement(new { type = "object" }),
            JsonSerializer.SerializeToElement(new { brief = "test" }),
            "Create a brief.",
            ContextWindowTokens: 2048,
            RequireProduction: false);

        using var response = await client.PostAsJsonAsync("/v2/reason/structured", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_context_budget", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task V2JobRejectsMalformedAssetIdAsTypedClientError()
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync("/v2/jobs", new AnalyzerJobRequest(
            "video-understanding",
            "2",
            ["not-a-sha256"],
            JsonSerializer.SerializeToElement(new { })));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("asset_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Production_video_job_rejects_development_vision_model_before_execution()
    {
        using var client = _factory.CreateAuthorizedClient();
        var content = Encoding.UTF8.GetBytes("production-gate-video-fixture");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using (var upload = new ByteArrayContent(content))
        {
            upload.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            upload.Headers.Add("X-Kadr-Content-Sha256", hash);
            upload.Headers.Add("X-Kadr-Total-Length", content.Length.ToString());
            upload.Headers.Add("X-Kadr-Offset", "0");
            using var uploaded = await client.PostAsync("/v2/assets", upload);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }
        using var response = await client.PostAsJsonAsync("/v2/jobs", new AnalyzerJobRequest(
            "video-understanding",
            "2",
            [hash],
            JsonSerializer.SerializeToElement(new { profile = "anime-episode" })));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("model_not_qualified", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EmbeddingJobAcceptsTextCorpusWithoutMediaAsset()
    {
        using var client = _factory.CreateAuthorizedClient();
        using var response = await client.PostAsJsonAsync("/v2/jobs", new AnalyzerJobRequest(
            "embedding",
            "2",
            [],
            JsonSerializer.SerializeToElement(new
            {
                query = "ending credits",
                documents = new[] { new { id = Guid.NewGuid(), text = "anime ending credits" } }
            })));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task V2JobTurnsDamagedWorkerJsonIntoRecoverableFailure()
    {
        using var client = _factory.CreateAuthorizedClient();
        var content = Encoding.UTF8.GetBytes("invalid-json-worker-fixture");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using (var upload = new ByteArrayContent(content))
        {
            upload.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            upload.Headers.Add("X-Kadr-Content-Sha256", hash);
            upload.Headers.Add("X-Kadr-Total-Length", content.Length.ToString());
            upload.Headers.Add("X-Kadr-Offset", "0");
            using var uploaded = await client.PostAsync("/v2/assets", upload);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        using var createdResponse = await client.PostAsJsonAsync("/v2/jobs", new AnalyzerJobRequest(
            "video-understanding",
            "invalid-json",
            [hash],
            JsonSerializer.SerializeToElement(new { }),
            RequireProduction: false));
        Assert.Equal(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<AnalyzerJobResponse>();
        Assert.NotNull(created);

        AnalyzerJobResponse? job = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(50);
            job = await client.GetFromJsonAsync<AnalyzerJobResponse>($"/v2/jobs/{created!.Id:D}");
            if (job?.State is AnalyzerJobState.Failed) break;
        }

        Assert.NotNull(job);
        Assert.Equal(AnalyzerJobState.Failed, job!.State);
        Assert.Equal("worker_invalid_json", job.ErrorCode);
    }

    [Fact]
    public async Task V2JobCreationIsIdempotentForRestartResume()
    {
        using var client = _factory.CreateAuthorizedClient();
        var content = Encoding.UTF8.GetBytes("resumable-job-fixture");
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        using (var upload = new ByteArrayContent(content))
        {
            upload.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            upload.Headers.Add("X-Kadr-Content-Sha256", hash);
            upload.Headers.Add("X-Kadr-Total-Length", content.Length.ToString());
            upload.Headers.Add("X-Kadr-Offset", "0");
            using var uploaded = await client.PostAsync("/v2/assets", upload);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        }

        var request = new AnalyzerJobRequest(
            "video-understanding", "2", [hash],
            JsonSerializer.SerializeToElement(new { profile = "generic" }));
        using var firstResponse = await client.PostAsJsonAsync("/v2/jobs", request);
        using var secondResponse = await client.PostAsJsonAsync("/v2/jobs", request);
        var first = await firstResponse.Content.ReadFromJsonAsync<AnalyzerJobResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<AnalyzerJobResponse>();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
    }

}

public sealed class ApiContractFactory : WebApplicationFactory<Program>
{
    private const string TestApiKey = "contract-test-key";
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(), "kadr-ai-contract-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AiServerOptions>();
            services.RemoveAll<IWorkerGateway>();
            services.AddSingleton(new AiServerOptions
            {
                PlannerBackendModel = "test-backend",
                PlannerPublicModelAlias = "test-model",
                ProductionModelsRoot = Path.Combine(_dataRoot, "models"),
                DataRoot = _dataRoot,
                WorkersRoot = Path.Combine(_dataRoot, "workers"),
                ApiKey = TestApiKey,
                MaxRequestBodyBytes = 4 * 1024 * 1024,
                MaxImageCount = 4,
                MaxPromptCharacters = 10_000,
                ListenUrls = AiServerOptions.DefaultListenUrls
            });
            services.AddSingleton<IWorkerGateway, ContractWorkerGateway>();
        });
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    private sealed class ContractWorkerGateway : IWorkerGateway
    {
        public Task<WorkerJobResult> ExecuteAsync(
            WorkerJob job,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
            => Task.FromResult(new WorkerJobResult(
                true,
                job.AnalyzerVersion == "invalid-json" ? "{"u8.ToArray() : "{}"u8.ToArray(),
                []));

        public Task<int> CountTokensAsync(
            string model,
            string text,
            CancellationToken cancellationToken)
            => Task.FromResult(2_000);

        public Task ReleaseAcceleratorAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
