using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.Editing;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using KadrStudio.Services.Agent;

namespace KadrStudio.UiAdapters.Tests;

public sealed class AiServerAgentModelTests
{
    [Fact]
    public async Task Clear_edit_brief_does_not_run_question_generation()
    {
        var handler = new AgentOllamaHandler(
            """
            {"task_kind":"edit","goal":"Удалить указанные части","scope":"Активная последовательность","protected_elements":["Остальной монтаж"],"constraints":["Не менять source"],"acceptance_criteria":["Изменён только Agent Draft"],"assumptions":[],"missing_information":["Точные границы нужно исследовать"],"needs_user_clarification":false,"clarification_reason":""}
            """);
        var options = new AiServerClientOptions(new Uri("https://ai.example.test/"), "agent-secret", "vision-model");
        using var service = new AiVideoAnalysisService(new FfmpegLocator(), new ProcessRunner(), options, handler);
        var model = new AiServerAgentModel(service);

        var result = await model.UnderstandAsync(
            new AgentModelTurnRequest(CreateTask(), [], [], [], 0),
            CancellationToken.None);

        Assert.Equal(AgentTaskKind.Edit, result.Brief.Kind);
        Assert.Empty(result.Questions);
        Assert.Single(handler.ChatRequestBodies);
    }

    [Fact]
    public async Task Real_planner_completes_two_investigation_turns_without_context_or_json_failure()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("KADR_STUDIO_RUN_AI_AGENT_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var endpointValue = Environment.GetEnvironmentVariable("KADR_STUDIO_AI_ENDPOINT");
        var endpoint = string.IsNullOrWhiteSpace(endpointValue)
            ? AiServerClientOptions.DefaultServerEndpoint
            : new Uri(endpointValue.TrimEnd('/') + "/", UriKind.Absolute);
        using var service = new AiVideoAnalysisService(
            new FfmpegLocator(),
            new ProcessRunner(),
            new AiServerClientOptions(
                endpoint,
                PlannerModelAlias: AiVideoAnalysisService.DefaultPlannerModelAlias));
        var model = new AiServerAgentModel(service);
        var task = CreateTask() with
        {
            UserRequest = "удали опенинг и эндинг остальное не трож",
            Phase = AgentTaskPhase.Understanding
        };
        var editorContext = JsonSerializer.SerializeToElement(new
        {
            channel = "editor_context",
            project_revision = 6,
            active_sequence_id = task.SourceSequenceId,
            active_sequence_revision = 0,
            playhead_seconds = 0,
            selected_clip_id = (Guid?)null,
            truncated = false,
            recommended_next_inspection = "inspect_timeline"
        });
        var projectContext = JsonSerializer.SerializeToElement(new
        {
            channel = "project",
            project_revision = 6,
            active_sequence_id = task.SourceSequenceId,
            canvas = new { width = 1920, height = 1080, frame_rate = "30" },
            source_count = 1,
            sequence_count = 1,
            duration_seconds = 1427.144,
            truncated = false,
            recommended_next_inspection = "inspect_timeline"
        });
        var observations = ImmutableArray.Create(
            new AgentModelObservation(
                1,
                "inspect_editor_context",
                AgentToolResultStatus.Succeeded,
                "Editor context inspected.",
                editorContext,
                null),
            new AgentModelObservation(
                2,
                "inspect_project",
                AgentToolResultStatus.Succeeded,
                "Project inspection completed.",
                projectContext,
                null));
        var conversation = ImmutableArray.Create(
            new AgentConversationContextMessage(
                AgentConversationRole.User,
                task.UserRequest,
                DateTimeOffset.UtcNow));

        var understanding = await model.UnderstandAsync(
            new AgentModelTurnRequest(
                task,
                [],
                observations,
                conversation,
                0),
            CancellationToken.None);

        Assert.Equal(AgentTaskKind.Edit, understanding.Brief.Kind);
        Assert.Empty(understanding.Questions);

        var investigationTask = task with
        {
            Phase = AgentTaskPhase.Investigating,
            Brief = understanding.Brief
        };
        var registry = AgentReadOnlyToolSet.Create(new DescriptorOnlyReadBackend());
        AgentEditingToolSet.RegisterDefaults(registry, new DescriptorOnlyEditingBackend());
        var availableTools = registry.Descriptors
            .Where(descriptor => !string.Equals(
                descriptor.Name,
                "inspect_agent_edits",
                StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                investigationTask,
                availableTools,
                observations,
                conversation,
                1),
            CancellationToken.None);

        Assert.Equal(AgentModelActionKind.UseTool, decision.Action);
        var selectedTool = Assert.Single(
            availableTools.Where(tool => tool.Name == decision.ToolName));
        Assert.Equal(AgentToolAccess.ReadOnly, selectedTool.Access);

        var videoTrackId = Guid.NewGuid();
        var audioTrackId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var linkGroupId = Guid.NewGuid();
        var timeline = JsonSerializer.SerializeToElement(new
        {
            channel = "timeline",
            project_revision = 6,
            sequence_id = task.SourceSequenceId,
            revision = 0,
            sequence_revision = 0,
            duration_seconds = 1427.144,
            track_count = 2,
            tracks = new[]
            {
                new { id = videoTrackId, kind = "visual", index = 0, name = "V1" },
                new { id = audioTrackId, kind = "audio", index = 0, name = "A1" }
            },
            media_clip_count = 2,
            media_clips = new[]
            {
                new
                {
                    id = Guid.NewGuid(), source_id = sourceId, source_name = "episode.mkv",
                    track_id = videoTrackId, track_name = "V1", track_kind = "visual",
                    start_seconds = 0.0, end_seconds = 1427.144, duration_seconds = 1427.144,
                    source_in_seconds = 0.0, source_out_seconds = 1427.144,
                    link_group_id = linkGroupId
                },
                new
                {
                    id = Guid.NewGuid(), source_id = sourceId, source_name = "episode.mkv",
                    track_id = audioTrackId, track_name = "A1", track_kind = "audio",
                    start_seconds = 0.0, end_seconds = 1427.144, duration_seconds = 1427.144,
                    source_in_seconds = 0.0, source_out_seconds = 1427.144,
                    link_group_id = linkGroupId
                }
            },
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Use inspect_sequence_overview, then inspect_range for content evidence."
        });
        var secondTurnObservations = observations.Add(new AgentModelObservation(
            3,
            "inspect_timeline",
            AgentToolResultStatus.Succeeded,
            "Timeline inspected.",
            timeline,
            null));

        var secondDecision = await model.DecideAsync(
            new AgentModelTurnRequest(
                investigationTask,
                availableTools,
                secondTurnObservations,
                conversation,
                2),
            CancellationToken.None);

        Assert.Equal(AgentModelActionKind.UseTool, secondDecision.Action);
        var secondSelectedTool = Assert.Single(
            availableTools.Where(tool => tool.Name == secondDecision.ToolName));
        Assert.Equal(AgentToolAccess.ReadOnly, secondSelectedTool.Access);
    }

    [Fact]
    public async Task Remote_agent_interpreter_defers_blocking_questions_until_investigation()
    {
        var handler = new AgentOllamaHandler(
            """
            {"task_kind":"edit","goal":"Удалить выбранный фрагмент","scope":"Активная последовательность","protected_elements":["Остальной монтаж"],"constraints":["Не менять source"],"acceptance_criteria":["Изменён только утверждённый диапазон"],"assumptions":[],"missing_information":["Способ закрытия зазора"],"needs_user_clarification":true,"clarification_reason":"Способ удаления меняет тайминг."}
            """);
        var options = new AiServerClientOptions(new Uri("https://ai.example.test/"), "agent-secret", "vision-model");
        using var service = new AiVideoAnalysisService(new FfmpegLocator(), new ProcessRunner(), options, handler);
        var model = new AiServerAgentModel(service);

        var result = await model.UnderstandAsync(
            new AgentModelTurnRequest(
                CreateTask(),
                [],
                [],
                [],
                0),
            CancellationToken.None);

        Assert.Equal(AgentTaskKind.Edit, result.Brief.Kind);
        Assert.Equal("Удалить выбранный фрагмент", result.Brief.Goal);
        Assert.Empty(result.Questions);
        Assert.Single(handler.ChatRequestBodies);
        Assert.Contains("\"model\":\"kadr-planner:latest\"", handler.ChatRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"think\":false", handler.ChatRequestBodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_agent_critic_rejects_unverifiable_plan_without_rewriting_it()
    {
        var handler = new AgentOllamaHandler(
            """{"accepted":false,"summary":"План нельзя безопасно выполнить.","issues":["Нет доказательства диапазона."]}""");
        var options = new AiServerClientOptions(new Uri("https://ai.example.test/"), "agent-secret", "vision-model");
        using var service = new AiVideoAnalysisService(new FfmpegLocator(), new ProcessRunner(), options, handler);
        var model = new AiServerAgentModel(service);
        var task = CreateTask() with
        {
            Brief = AgentTaskBrief.Create(AgentTaskKind.Edit, "Удалить фрагмент", "Активная последовательность")
        };
        var plan = AgentPlanDraft.Create(
            "Удалить фрагмент",
            "Один шаг",
            [],
            [new AgentPlanStepDraft("Удалить", "Удалить диапазон")]);

        var review = await model.ReviewPlanAsync(
            new AgentPlanReviewRequest(task, plan, [], [], 1),
            CancellationToken.None);

        Assert.False(review.Accepted);
        Assert.Contains("доказательства", Assert.Single(review.Issues), StringComparison.OrdinalIgnoreCase);
    }

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
        Assert.DoesNotContain(
            "plan_steps",
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
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","completion_summary":""}
            """,
            """
            {"plan_objective":"Собрать безопасный черновик.","plan_summary":"Изменения будут выполнены только после утверждения.","plan_constraints":["Не менять основной таймлайн."],"plan_steps":[{"title":"Исследовать","description":"Использовать только необходимые наблюдения.","expected_editing_tool":"","expected_editing_arguments":{},"evidence_requirement":"timeline","evidence_observation_sequences":[],"expected_effect":"Доказательства собраны.","protected_invariants":["Source не меняется."],"verification_checks":[]},{"title":"Смонтировать","description":"Работать в отдельном Agent Draft.","expected_editing_tool":"ripple_delete_ranges","expected_editing_arguments":{"ranges":[{"start_seconds":10,"end_seconds":20}]},"evidence_requirement":"frames","evidence_observation_sequences":[2],"expected_effect":"Диапазон удалён.","protected_invariants":["Остальной монтаж сохранён."],"verification_checks":["Проверить новую склейку."]},{"title":"Проверить","description":"Проверить результат после монтажа.","expected_editing_tool":"","expected_editing_arguments":{},"evidence_requirement":"timeline","evidence_observation_sequences":[],"expected_effect":"Целостность подтверждена.","protected_invariants":["Source не меняется."],"verification_checks":["Сверить edit log."]}]}
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
        var readDescriptor = new AgentToolDescriptor(
            "inspect_timeline",
            "Inspect timeline.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());
        var editDescriptor = new AgentToolDescriptor(
            "ripple_delete_ranges",
            "Delete approved ranges from Agent Draft.",
            AgentToolAccess.Editing,
            AgentToolJson.EmptyObject());
        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                CreateTask(),
                ImmutableArray.Create(readDescriptor, editDescriptor),
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                3),
            CancellationToken.None);

        Assert.Equal(AgentModelActionKind.PublishPlan, decision.Action);
        Assert.NotNull(decision.Plan);
        Assert.Equal(
            "Собрать безопасный черновик.",
            decision.Plan!.Objective);
        Assert.Equal(2, handler.ChatRequestBodies.Count);
        Assert.DoesNotContain("plan_steps", handler.ChatRequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("plan_steps", handler.ChatRequestBodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("ripple_delete_ranges", handler.ChatRequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("ripple_delete_ranges", handler.ChatRequestBodies[1], StringComparison.Ordinal);
        Assert.Collection(
            decision.Plan.Steps,
            step => Assert.Equal("Исследовать", step.Title),
            step => Assert.Equal("Смонтировать", step.Title),
            step => Assert.Equal("Проверить", step.Title));
        Assert.Contains(
            "Не менять основной таймлайн.",
            decision.Plan.Constraints);
        var editingStep = decision.Plan.Steps[1];
        Assert.Equal("ripple_delete_ranges", editingStep.ExpectedEditingTool);
        Assert.Equal(AgentEvidenceRequirement.Frames, editingStep.EvidenceRequirement);
        Assert.Equal(10, editingStep.ExpectedEditingArguments!.Value
            .GetProperty("ranges")[0]
            .GetProperty("start_seconds")
            .GetDouble());
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

    private sealed class DescriptorOnlyReadBackend : IAgentReadOnlyToolBackend
    {
        public ValueTask<JsonElement> InspectProjectAsync(
            AgentToolContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectTimelineAsync(
            AgentToolContext context,
            Guid sequenceId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectMediaAsync(
            AgentToolContext context,
            Guid mediaId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectRangeAsync(
            AgentToolContext context,
            AgentRangeInspectionRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());
    }

    private sealed class DescriptorOnlyEditingBackend : IAgentEditingToolBackend
    {
        public ValueTask<JsonElement> RippleDeleteRangeAsync(
            AgentToolContext context, double startSeconds, double endSeconds,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> RippleDeleteRangesAsync(
            AgentToolContext context, IReadOnlyList<AgentTimelineRange> ranges,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> SplitTimelineAsync(
            AgentToolContext context, double positionSeconds, string reason,
            CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> DeleteClipsAsync(
            AgentToolContext context, IReadOnlyCollection<Guid> clipIds,
            bool includeLinked, string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> TrimClipAsync(
            AgentToolContext context, Guid clipId, string edge,
            double edgeSeconds, string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> MoveClipAsync(
            AgentToolContext context, Guid clipId, Guid targetTrackId,
            double startSeconds, string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> SetClipVolumeAsync(
            AgentToolContext context, Guid clipId, double volume, bool muted,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> SetClipVideoAsync(
            AgentToolContext context, Guid clipId, VideoParameters parameters,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> SetClipAudioAsync(
            AgentToolContext context, Guid clipId, AudioParameters parameters,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> UnlinkClipsAsync(
            AgentToolContext context, IReadOnlyCollection<Guid> clipIds,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> DeleteTimelineObjectsAsync(
            AgentToolContext context, IReadOnlyCollection<Guid> textClipIds,
            IReadOnlyCollection<Guid> transitionIds, IReadOnlyCollection<Guid> markerIds,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> AddMarkerAsync(
            AgentToolContext context, double startSeconds, double durationSeconds,
            string title, string description, string reason,
            CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> AddTextAsync(
            AgentToolContext context, double startSeconds, double durationSeconds,
            string text, bool subtitle, double fontSize, double x, double y,
            string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> AddTransitionAsync(
            AgentToolContext context, Guid fromClipId, string kind,
            double durationSeconds, string reason, CancellationToken cancellationToken)
            => Empty();

        public ValueTask<JsonElement> InspectEditLogAsync(
            AgentToolContext context,
            CancellationToken cancellationToken)
            => Empty();

        public void Reset(Guid taskId)
        {
        }

        private static ValueTask<JsonElement> Empty()
            => ValueTask.FromResult(AgentToolJson.EmptyObject());
    }

    private sealed class AgentOllamaHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responseContents;

        public AgentOllamaHandler(params string[] responseContents)
        {
            var normalized = responseContents
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .ToArray();
            _responseContents = new Queue<string>(normalized.Length == 0
                ?
                [
                    """
                    {"action":"use_tool","progress":"Смотрю структуру проекта.","tool_name":"inspect_project","tool_arguments":{},"question":"","question_context":"","plan_objective":"","plan_summary":"","plan_constraints":[],"plan_steps":[],"completion_summary":""}
                    """
                ]
                : normalized);
        }

        public string ChatRequestBody { get; private set; } = string.Empty;
        public List<string> ChatRequestBodies { get; } = [];
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
            ChatRequestBodies.Add(ChatRequestBody);

            var content = _responseContents.Count > 1
                ? _responseContents.Dequeue()
                : _responseContents.Peek();
            return WrapInferenceContent(content);
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
