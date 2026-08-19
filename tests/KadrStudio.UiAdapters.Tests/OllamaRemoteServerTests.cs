using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using KadrStudio.Application.Automation;
using KadrStudio.Core.Domain;
using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class OllamaRemoteServerTests
{
    [Fact]
    public async Task Local_endpoint_starts_without_settings_and_installs_recommended_model_automatically()
    {
        var handler = new AutoInstallOllamaHandler();
        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(), new ProcessRunner(), new OllamaServerOptions(), handler);

        var models = await service.GetModelsAsync();

        var model = Assert.Single(models);
        Assert.False(service.IsRemote);
        Assert.Equal("127.0.0.1", service.Endpoint.Host);
        Assert.Equal(11435, service.Endpoint.Port);
        Assert.Equal(OllamaVideoAnalysisService.RecommendedLocalModel, model.Name);
        Assert.True(model.SupportsVision);
        Assert.Equal(1, handler.PullCount);
    }

    [Fact]
    public async Task Installed_local_model_runs_real_structured_inference_when_enabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_STUDIO_RUN_LOCAL_OLLAMA_TESTS"),
                "1", StringComparison.Ordinal))
            return;
        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(), new ProcessRunner(), new OllamaServerOptions());

        var models = await service.GetModelsAsync();
        var model = Assert.Single(models, item => item.Name.Equals(
            "qwen3-vl:4b-instruct", StringComparison.OrdinalIgnoreCase));
        Assert.True(model.SupportsVision);
        await service.VerifyModelAsync(model.Name);

        var profile = GameEditingProfiles.Get("universal");
        var project = ProjectState.CreateNew("Ollama planning smoke");
        var source = new MediaSource(
            Guid.NewGuid(), "smoke.mp4", "smoke.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(20), true, 1920, 1080, new FrameRate(30), Fingerprint: "smoke");
        project = project with { Sources = project.Sources.Add(source.Id, source) };
        var segments = Enumerable.Range(0, 4).Select(index => new AnalysisSegment(
            Guid.NewGuid(), source.Id,
            new TimeRange(TimelineTime.FromSeconds(index * 4), TimelineTime.FromSeconds(3)),
            0.5 + index * 0.1, 0.4, index == 1 ? 0.8 : 0.1,
            index == 1 ? "Ключевая мысль героя" : string.Empty,
            ImmutableDictionary<string, double>.Empty.Add(index == 3 ? "result" : "action", 0.8),
            0.85,
            [new AnalysisEvidence(MontageEvidenceKind.Vision, $"Подтверждённый фрагмент {index + 1}")]))
            .ToImmutableArray();
        var manifest = new MediaAnalysisManifest(
            source.Id, "smoke", "smoke-v1", model.Name, profile.Id, profile.Version,
            DateTimeOffset.UtcNow, segments);
        var request = new MontageRequest(
            Guid.NewGuid(), new MontageScope(MontageScopeKind.MediaLibrary, [source.Id]),
            MontageTargetFormat.Source, TimelineTime.FromSeconds(3), TimelineTime.FromSeconds(8),
            TimelineTime.FromSeconds(14), "Собери понятную универсальную историю с сильным финалом",
            profile, []);

        var plan = await service.PlanMontageAsync(new MontagePlanningContext(
            project, request, ImmutableDictionary<Guid, MediaAnalysisManifest>.Empty.Add(source.Id, manifest)));

        Assert.NotEmpty(plan.Items);
        Assert.Equal(model.Name, plan.Dependencies.Model);
        Assert.DoesNotContain(plan.Warnings, warning => warning.StartsWith(
            "ИИ не предложил безопасных изменений", StringComparison.Ordinal));
        Assert.All(plan.Items, item => Assert.Contains(item.Id, segments.Select(segment => segment.Id)));
    }

    [Fact]
    public async Task Remote_server_uses_bearer_auth_discovers_vision_and_runs_inference_probe()
    {
        var handler = new FakeOllamaHandler();
        var options = new OllamaServerOptions(
            new Uri("https://ai.example.test/"), "secret-token", "qwen-cloud");
        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(), new ProcessRunner(), options, handler);

        var models = await service.GetModelsAsync();
        await service.VerifyModelAsync("qwen-cloud");

        var model = Assert.Single(models);
        Assert.True(service.IsRemote);
        Assert.True(model.IsRemote);
        Assert.True(model.SupportsVision);
        Assert.Equal("qwen-cloud", model.Name);
        Assert.Contains("облако", model.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(handler.Requests, request => request.Path == "/api/chat");
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.Scheme);
            Assert.Equal("secret-token", request.Parameter);
        });
    }

    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        public List<(string Path, string? Scheme, string? Parameter)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            var json = request.RequestUri?.AbsolutePath switch
            {
                "/api/version" => "{\"version\":\"test\"}",
                "/api/tags" => "{\"models\":[{\"name\":\"qwen-cloud\",\"size\":1000}]}",
                "/api/show" => "{\"capabilities\":[\"vision\"]}",
                "/api/chat" => "{\"message\":{\"content\":\"{\\\"status\\\":\\\"ok\\\"}\"}}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AutoInstallOllamaHandler : HttpMessageHandler
    {
        private bool _installed;
        public int PullCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/pull")
            {
                PullCount++;
                _installed = true;
            }

            var models = _installed
                ? $"{{\"models\":[{{\"name\":\"{OllamaVideoAnalysisService.RecommendedLocalModel}\",\"size\":3295625769}}]}}"
                : "{\"models\":[]}";
            var json = path switch
            {
                "/api/version" => "{\"version\":\"test\"}",
                "/api/tags" => models,
                "/api/show" => "{\"capabilities\":[\"vision\"]}",
                "/api/pull" => "{\"status\":\"success\"}",
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
