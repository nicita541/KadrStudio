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

public sealed class AiServerAgentModelTests
{
    [Fact]
    public async Task Remote_agent_model_uses_structured_schema_and_returns_tool_action()
    {
        var handler = new AgentOllamaHandler();
        var options = new AiServerClientOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new AiServerAgentModel(service);
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
            "\"schema\"",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"systemPrompt\"",
            handler.ChatRequestBody,
            StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("agent-secret", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task Remote_agent_model_sends_prior_conversation_as_context()
    {
        var handler = new AgentOllamaHandler();
        var options = new AiServerClientOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new AiServerAgentModel(service);
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

        using var requestDocument = JsonDocument.Parse(
            handler.ChatRequestBody);

        var turnPayload = requestDocument.RootElement
            .GetProperty("userPrompt")
            .GetString();

        Assert.NotNull(turnPayload);
        Assert.Contains(
            "Не трогай первую минуту исходника.",
            turnPayload,
            StringComparison.Ordinal);

        using var turnDocument = JsonDocument.Parse(turnPayload);
        var conversation = turnDocument.RootElement
            .GetProperty("conversation");

        Assert.Equal(1, conversation.GetArrayLength());
        Assert.Equal(
            "Не трогай первую минуту исходника.",
            conversation[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Remote_agent_model_parses_user_approvable_plan()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","plan_objective":"Собрать безопасный черновик.","plan_summary":"Изменения будут выполнены только после утверждения.","plan_constraints":["Не менять основной таймлайн."],"plan_steps":[{"title":"Исследовать","description":"Использовать только необходимые наблюдения."},{"title":"Смонтировать","description":"Работать в отдельном Agent Draft."},{"title":"Проверить","description":"Проверить результат после монтажа."}],"completion_summary":""}
            """);

        var options = new AiServerClientOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new AiServerAgentModel(service);
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
        Assert.Collection(
            decision.Plan.Steps,
            step => Assert.Equal("Исследовать", step.Title),
            step => Assert.Equal("Смонтировать", step.Title),
            step => Assert.Equal("Проверить", step.Title));
        Assert.Contains(
            "Не менять основной таймлайн.",
            decision.Plan.Constraints);
    }

    [Fact]
    public async Task Remote_agent_model_supports_begin_verification_action()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"begin_verification","progress":"Перехожу к проверке.","tool_name":"","tool_arguments":{},"question":"","question_context":"","plan_objective":"","plan_summary":"","plan_constraints":[],"plan_steps":[],"completion_summary":""}
            """);
        var options = new AiServerClientOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new AiServerAgentModel(service);
        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                CreateTask(),
                ImmutableArray<AgentToolDescriptor>.Empty,
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                4,
                AgentModelTurnMode.Execution),
            CancellationToken.None);

        Assert.Equal(
            AgentModelActionKind.BeginVerification,
            decision.Action);

        using var requestDocument = JsonDocument.Parse(
            handler.ChatRequestBody);
        using var turnDocument = JsonDocument.Parse(
            requestDocument.RootElement.GetProperty("userPrompt").GetString()!);

        Assert.Equal(
            "execution",
            turnDocument.RootElement
                .GetProperty("mode")
                .GetString());
    }

    [Fact]
    public async Task Remote_agent_model_parses_verified_completion_summary()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"complete_task","progress":"Проверка завершена.","tool_name":"","tool_arguments":{},"question":"","question_context":"","plan_objective":"","plan_summary":"","plan_constraints":[],"plan_steps":[],"completion_summary":"Agent Draft проверен; утверждённый план выполнен."}
            """);
        var options = new AiServerClientOptions(
            new Uri("https://ai.example.test/"),
            "agent-secret",
            "agent-model");

        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            options,
            handler);

        var model = new AiServerAgentModel(service);
        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                CreateTask(),
                ImmutableArray<AgentToolDescriptor>.Empty,
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                5,
                AgentModelTurnMode.Verification),
            CancellationToken.None);

        Assert.Equal(
            AgentModelActionKind.CompleteTask,
            decision.Action);
        Assert.Equal(
            "Agent Draft проверен; утверждённый план выполнен.",
            decision.CompletionSummary);
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
                  {"action":"use_tool","progress":"Смотрю структуру проекта.","tool_name":"inspect_project","tool_arguments":{},"question":"","question_context":"","plan_objective":"","plan_summary":"","plan_constraints":[],"plan_steps":[],"completion_summary":""}
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
                "/health/live" => "{\"status\":\"live\"}",
                "/v1/inference/structured" => await HandleInferenceAsync(
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

        private async Task<string> HandleInferenceAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ChatRequestBody = await request.Content!
                .ReadAsStringAsync(cancellationToken);

            return WrapInferenceContent(_responseContent);
        }
    }

    private static string WrapInferenceContent(string content)
        => JsonSerializer.Serialize(new
        {
            content,
            doneReason = "stop",
            evalCount = 64
        });
}
