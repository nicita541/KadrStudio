using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Services;
using KadrStudio.Services.Agent;

namespace KadrStudio.UiAdapters.Tests;

public sealed class OllamaAgentModelTests
{
    [Fact]
    public async Task Remote_agent_model_uses_structured_schema_and_returns_tool_action()
    {
        var handler = new AgentOllamaHandler();
        var options = new OllamaServerOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new OllamaAgentModel(service);
        var task = CreateTask();
        var tool = new AgentToolDescriptor(
            "inspect_project",
            "Inspect project facts.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());

        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray.Create(tool),
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                1),
            CancellationToken.None);

        Assert.Equal(AgentModelActionKind.UseTool, decision.Action);
        Assert.Equal("inspect_project", decision.ToolName);
        Assert.Equal(JsonValueKind.Object, decision.ToolArguments.ValueKind);
        Assert.Contains(
            "inspect_project",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"format\"",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"think\":false",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("agent-secret", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task Remote_agent_model_sends_prior_conversation_as_context()
    {
        var handler = new AgentOllamaHandler();
        var options = new OllamaServerOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new OllamaAgentModel(service);
        var context = ImmutableArray.Create(
            new AgentConversationContextMessage(
                AgentConversationRole.User,
                "Не трогай первую минуту исходника.",
                DateTimeOffset.UtcNow.AddMinutes(-1)));

        await model.DecideAsync(
            new AgentModelTurnRequest(
                CreateTask(),
                ImmutableArray<AgentToolDescriptor>.Empty,
                ImmutableArray<AgentModelObservation>.Empty,
                context,
                1),
            CancellationToken.None);

        Assert.Contains(
            "Не трогай первую минуту исходника.",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "conversation",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_agent_model_parses_user_approvable_plan()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","plan_objective":"Собрать безопасный черновик.","plan_summary":"Изменения будут выполнены только после утверждения.","plan_constraints":["Не менять основной таймлайн."],"plan_steps":[{"title":"Исследовать","description":"Использовать только необходимые наблюдения."},{"title":"Смонтировать","description":"Работать в отдельном Agent Draft."},{"title":"Проверить","description":"Проверить результат после монтажа."}]}
            """);

        var options = new OllamaServerOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new OllamaAgentModel(service);
        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                CreateTask(),
                ImmutableArray<AgentToolDescriptor>.Empty,
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                3),
            CancellationToken.None);

        Assert.Equal(AgentModelActionKind.PublishPlan, decision.Action);
        Assert.NotNull(decision.Plan);
        Assert.Equal(
            "Собрать безопасный черновик.",
            decision.Plan!.Objective);
        Assert.Equal(3, decision.Plan.Steps.Length);
        Assert.Contains(
            "Не менять основной таймлайн.",
            decision.Plan.Constraints);
    }

    [Fact]
    public async Task Local_agent_model_prepares_recommended_model_before_first_turn()
    {
        var handler = new LocalAgentOllamaHandler();

        using var service = new OllamaVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            new OllamaServerOptions(),
            handler);

        var model = new OllamaAgentModel(service);
        var task = CreateTask();

        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray<AgentToolDescriptor>.Empty,
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                1),
            CancellationToken.None);

        Assert.Equal(AgentModelActionKind.AskUser, decision.Action);
        Assert.Equal(1, handler.PullCount);
        Assert.Equal(
            OllamaVideoAnalysisService.RecommendedLocalModel,
            handler.LastChatModel);
    }

    private static AgentTaskState CreateTask()
    {
        var now = DateTimeOffset.UtcNow;

        return new AgentTaskState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "Разберись в материале и сначала предложи план.",
            AgentTaskPhase.Investigating,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            now,
            now);
    }

    private sealed class AgentOllamaHandler : HttpMessageHandler
    {
        private readonly string _responseContent;

        public AgentOllamaHandler(string? responseContent = null)
        {
            _responseContent = string.IsNullOrWhiteSpace(responseContent)
                ? """
                  {"action":"use_tool","progress":"Смотрю структуру проекта.","tool_name":"inspect_project","tool_arguments":{},"question":"","question_context":"","plan_objective":"","plan_summary":"","plan_constraints":[],"plan_steps":[]}
                  """
                : responseContent;
        }

        public string ChatRequestBody { get; private set; } = string.Empty;
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            var path = request.RequestUri?.AbsolutePath;
            var json = path switch
            {
                "/api/version" => "{\"version\":\"test\"}",
                "/api/chat" => await HandleChatAsync(
                    request,
                    cancellationToken),
                _ => "{}"
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private async Task<string> HandleChatAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ChatRequestBody = await request.Content!
                .ReadAsStringAsync(cancellationToken);

            return WrapChatContent(_responseContent);
        }
    }

    private sealed class LocalAgentOllamaHandler : HttpMessageHandler
    {
        private bool _installed;

        public int PullCount { get; private set; }
        public string? LastChatModel { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;

            if (path == "/api/pull")
            {
                PullCount++;
                _installed = true;
            }

            string json;
            switch (path)
            {
                case "/api/version":
                    json = "{\"version\":\"test\"}";
                    break;

                case "/api/tags":
                    json = _installed
                        ? $"{{\"models\":[{{\"name\":\"{OllamaVideoAnalysisService.RecommendedLocalModel}\",\"size\":1000}}]}}"
                        : "{\"models\":[]}";
                    break;

                case "/api/pull":
                    json = "{\"status\":\"success\"}";
                    break;

                case "/api/chat":
                    var body = await request.Content!
                        .ReadAsStringAsync(cancellationToken);
                    using (var document = JsonDocument.Parse(body))
                    {
                        LastChatModel = document.RootElement
                            .GetProperty("model")
                            .GetString();
                    }

                    json = WrapChatContent(
                        """
                        {"action":"ask_user","progress":"Нужно уточнение.","tool_name":"","tool_arguments":{},"question":"Какой результат считать приоритетным?","question_context":"Данных проекта недостаточно для выбора.","plan_objective":"","plan_summary":"","plan_constraints":[],"plan_steps":[]}
                        """);
                    break;

                default:
                    json = "{}";
                    break;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private static string WrapChatContent(string content)
        => JsonSerializer.Serialize(new
        {
            message = new
            {
                content
            },
            done_reason = "stop",
            eval_count = 64
        });
}
