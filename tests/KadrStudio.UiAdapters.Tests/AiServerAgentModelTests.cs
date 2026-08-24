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
using KadrStudio.Application.Automation.Agent.Verification;
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
            {"task_kind":"edit","investigation_strategy":"content_discovery","goal":"Удалить указанные части","scope":"Активная последовательность","protected_elements":["Остальной монтаж"],"constraints":["Не менять source"],"acceptance_criteria":["Изменён только Agent Draft"],"assumptions":[],"missing_information":["Точные границы нужно исследовать"],"needs_user_clarification":false,"clarification_reason":""}
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
            {"task_kind":"edit","investigation_strategy":"direct_target","goal":"Удалить выбранный фрагмент","scope":"Активная последовательность","protected_elements":["Остальной монтаж"],"constraints":["Не менять source"],"acceptance_criteria":["Изменён только утверждённый диапазон"],"assumptions":[],"missing_information":["Способ закрытия зазора"],"needs_user_clarification":true,"clarification_reason":"Способ удаления меняет тайминг."}
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
            """{"accepted":false,"summary":"План нельзя безопасно выполнить.","issues":["Нет доказательства диапазона."],"step_assessments":[{"step_order":1,"evidence_supports_action":true,"protected_content_detected":false,"exact_arguments_supported":true,"counterevidence_checked":true,"adjacent_context_checked":true,"unresolved_contradictions":[],"summary":"Формальный assessment заполнен."}]}""");
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
    public async Task Remote_agent_critic_cannot_approve_a_step_with_unresolved_counterevidence()
    {
        var handler = new AgentOllamaHandler(
            """{"accepted":true,"summary":"План принят.","issues":[],"step_assessments":[{"step_order":1,"evidence_supports_action":true,"protected_content_detected":false,"exact_arguments_supported":true,"counterevidence_checked":true,"adjacent_context_checked":true,"unresolved_contradictions":["Перекрывающийся vision-проход показывает сюжет внутри диапазона."],"summary":"Факты конфликтуют."}]}""");
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
        Assert.Contains(review.Issues, issue =>
            issue.Contains("vision-проход", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Remote_agent_critic_cannot_approve_without_counterevidence_and_adjacent_context_checks()
    {
        var handler = new AgentOllamaHandler(
            """{"accepted":true,"summary":"План принят.","issues":[],"step_assessments":[{"step_order":1,"evidence_supports_action":true,"protected_content_detected":false,"exact_arguments_supported":true,"counterevidence_checked":false,"adjacent_context_checked":false,"unresolved_contradictions":[],"summary":"Проверен только выбранный диапазон."}]}""");
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
        Assert.Contains(review.Issues, issue =>
            issue.Contains("только выбранный диапазон", StringComparison.OrdinalIgnoreCase));
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
        using var requestDocument = JsonDocument.Parse(handler.ChatRequestBody);
        var responseSchema = requestDocument.RootElement
            .GetProperty("schema")
            .GetRawText();
        Assert.DoesNotContain(
            "plan_steps",
            responseSchema,
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
    public async Task Remote_agent_model_surfaces_dense_observed_ocr_regions_without_classifying_them()
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
        task = task with
        {
            EvidenceLedger =
            [
                new AgentEvidenceRecord(
                    Guid.NewGuid(),
                    1,
                    AgentEvidenceChannel.Frames,
                    "inspect_content_overview",
                    task.SourceSequenceId,
                    task.SourceSequenceRevision,
                    0,
                    1427,
                    "Tile 1 @ 2.000s: landscape [text: place] | " +
                    "Tile 2 @ 42.000s: room [text: name] | " +
                    "Tile 3 @ 121.407s: face [text: credit A] | " +
                    "Tile 4 @ 131.228s: sky [text: credit B] | " +
                    "Tile 5 @ 141.049s: motion [text: credit C] | " +
                    "Tile 6 @ 150.513s: white card [text: episode title]",
                    ["Observed frame facts"],
                    null,
                    DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames)
            ]
        };

        await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray.Create(new AgentToolDescriptor(
                    "inspect_project",
                    "Inspect project facts.",
                    AgentToolAccess.ReadOnly,
                    AgentToolJson.EmptyObject())),
                [],
                [],
                1),
            CancellationToken.None);

        using var requestDocument = JsonDocument.Parse(handler.ChatRequestBody);
        using var turnDocument = JsonDocument.Parse(
            requestDocument.RootElement.GetProperty("userPrompt").GetString()!);
        var regions = turnDocument.RootElement
            .GetProperty("observed_text_activity_regions")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();

        Assert.Equal(3, regions.Length);
        Assert.Contains("121.407–150.513s: 4 observed OCR facts", regions[0], StringComparison.Ordinal);
        Assert.Contains("2–2s: 1 observed OCR facts", regions[1], StringComparison.Ordinal);
        Assert.Contains("42–42s: 1 observed OCR facts", regions[2], StringComparison.Ordinal);
        Assert.DoesNotContain("opening", string.Join(' ', regions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remote_agent_model_keeps_the_last_overview_window_in_a_bounded_nonduplicated_context()
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
        var overview = "Complete overview.\n" + string.Join('\n', Enumerable.Range(1, 20).Select(index =>
            $"window {index:00} {(index == 20 ? "LAST_WINDOW_SENTINEL " : string.Empty)}{new string('x', 1_200)}"));
        task = task with
        {
            EvidenceLedger =
            [
                new AgentEvidenceRecord(
                    Guid.NewGuid(),
                    1,
                    AgentEvidenceChannel.Frames,
                    "inspect_content_overview",
                    task.SourceSequenceId,
                    task.SourceSequenceRevision,
                    0,
                    2_000,
                    overview,
                    ["Evenly sampled frame facts"],
                    null,
                    DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames)
            ]
        };
        var rawObservation = AgentToolJson.ToElement(new
        {
            sequence_id = task.SourceSequenceId,
            start_seconds = 0,
            end_seconds = 2_000,
            huge_duplicate = new string('z', 18_000)
        });

        await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray.Create(new AgentToolDescriptor(
                    "inspect_project",
                    "Inspect project facts.",
                    AgentToolAccess.ReadOnly,
                    AgentToolJson.EmptyObject())),
                [new AgentModelObservation(
                    1,
                    "inspect_content_overview",
                    AgentToolResultStatus.Succeeded,
                    overview,
                    rawObservation,
                    null,
                    EvidenceCapabilities: AgentEvidenceCapabilities.Frames)],
                [],
                1),
            CancellationToken.None);

        using var requestDocument = JsonDocument.Parse(handler.ChatRequestBody);
        var userPrompt = requestDocument.RootElement.GetProperty("userPrompt").GetString()!;
        using var turnDocument = JsonDocument.Parse(userPrompt);
        var evidenceSummary = turnDocument.RootElement
            .GetProperty("evidence_ledger")[0]
            .GetProperty("summary")
            .GetString()!;
        var observation = turnDocument.RootElement.GetProperty("observations")[0];

        Assert.Contains("window 01", evidenceSummary, StringComparison.Ordinal);
        Assert.Contains("LAST_WINDOW_SENTINEL", evidenceSummary, StringComparison.Ordinal);
        Assert.InRange(evidenceSummary.Length, 1, 14_000);
        Assert.Equal(JsonValueKind.Null, observation.GetProperty("data").ValueKind);
        Assert.DoesNotContain("huge_duplicate", userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partial_overview_samples_stay_bounded_and_keep_every_window_without_raw_duplicates()
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
        var evidence = ImmutableArray.CreateBuilder<AgentEvidenceRecord>();
        var observations = ImmutableArray.CreateBuilder<AgentModelObservation>();
        for (var sequence = 1; sequence <= 12; sequence++)
        {
            var summary =
                $"Content overview sample {sequence * 100}-{(sequence + 1) * 100}s.\n" +
                $"Time mapping: source {sequence * 100}-{(sequence + 1) * 100}s.\n" +
                "Measurement: changing scenes and measured audio.\n" +
                $"Audio loudness change: {new string('a', 900)}\n" +
                "Audio sample start: -24 LUFS.\n" +
                $"Sample part: Tile 1 visible person | Tile 3 visible room | " +
                $"Tile 5 visible title {new string('v', 500)} | " +
                $"Tile 8 visible transition {(sequence == 12 ? "LAST_SAMPLE_SENTINEL" : string.Empty)}";
            evidence.Add(new AgentEvidenceRecord(
                Guid.NewGuid(),
                sequence,
                AgentEvidenceChannel.Frames,
                "inspect_content_sample",
                task.SourceSequenceId,
                task.SourceSequenceRevision,
                sequence * 100,
                (sequence + 1) * 100,
                summary,
                ["Measured sample"],
                null,
                DateTimeOffset.UtcNow,
                AgentEvidenceCapabilities.Frames | AgentEvidenceCapabilities.Audio));
            observations.Add(new AgentModelObservation(
                sequence,
                "inspect_content_sample",
                AgentToolResultStatus.Succeeded,
                summary,
                AgentToolJson.ToElement(new
                {
                    sequence,
                    huge_duplicate = new string('z', 18_000)
                }),
                null,
                AgentEvidenceCapabilities.Frames | AgentEvidenceCapabilities.Audio));
        }
        task = task with { EvidenceLedger = evidence.ToImmutable() };

        await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray.Create(new AgentToolDescriptor(
                    "inspect_project",
                    "Inspect project facts.",
                    AgentToolAccess.ReadOnly,
                    AgentToolJson.EmptyObject())),
                observations.ToImmutable(),
                [],
                1),
            CancellationToken.None);

        using var requestDocument = JsonDocument.Parse(handler.ChatRequestBody);
        var userPrompt = requestDocument.RootElement.GetProperty("userPrompt").GetString()!;
        using var turnDocument = JsonDocument.Parse(userPrompt);
        var ledger = turnDocument.RootElement.GetProperty("evidence_ledger");
        var sentSummaries = string.Join(
            '\n',
            ledger.EnumerateArray().Select(item => item.GetProperty("summary").GetString()));

        Assert.Equal(12, ledger.GetArrayLength());
        Assert.Contains("LAST_SAMPLE_SENTINEL", sentSummaries, StringComparison.Ordinal);
        Assert.InRange(userPrompt.Length, 1, 70_000);
        Assert.All(
            turnDocument.RootElement.GetProperty("observations").EnumerateArray(),
            observation => Assert.Equal(JsonValueKind.Null, observation.GetProperty("data").ValueKind));
        Assert.DoesNotContain("huge_duplicate", userPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_agent_model_parses_user_approvable_plan()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","completion_summary":""}
            """,
            """
            {"plan_objective":"Собрать безопасный черновик.","plan_summary":"Изменения будут выполнены только после утверждения.","plan_constraints":["Не менять основной таймлайн."],"plan_steps":[{"title":"Смонтировать","description":"Работать в отдельном Agent Draft.","expected_editing_tool":"ripple_delete_ranges","expected_editing_arguments":{"ranges":[{"start_seconds":10,"end_seconds":20}]},"evidence_requirement":"frames","evidence_observation_sequences":[2],"expected_effect":"Диапазон удалён.","protected_invariants":["Остальной монтаж сохранён."],"verification_checks":["Проверить новую склейку."]}]}
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
        using var investigationRequest = JsonDocument.Parse(handler.ChatRequestBodies[0]);
        using var planRequest = JsonDocument.Parse(handler.ChatRequestBodies[1]);
        var investigationSchema = investigationRequest.RootElement
            .GetProperty("schema")
            .GetRawText();
        var planSchema = planRequest.RootElement
            .GetProperty("schema")
            .GetRawText();
        Assert.DoesNotContain("plan_steps", investigationSchema, StringComparison.Ordinal);
        Assert.Contains("plan_steps", planSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("ripple_delete_ranges", investigationSchema, StringComparison.Ordinal);
        Assert.Contains("ripple_delete_ranges", planSchema, StringComparison.Ordinal);
        var editingToolEnum = planRequest.RootElement
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("plan_steps")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("expected_editing_tool")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.DoesNotContain(string.Empty, editingToolEnum);
        var editingStep = Assert.Single(decision.Plan.Steps);
        Assert.Equal("Смонтировать", editingStep.Title);
        Assert.Contains(
            "Не менять основной таймлайн.",
            decision.Plan.Constraints);
        Assert.Equal("ripple_delete_ranges", editingStep.ExpectedEditingTool);
        Assert.Equal(AgentEvidenceRequirement.Frames, editingStep.EvidenceRequirement);
        Assert.Equal(10, editingStep.ExpectedEditingArguments!.Value
            .GetProperty("ranges")[0]
            .GetProperty("start_seconds")
            .GetDouble());
    }

    [Fact]
    public async Task Published_plan_schema_requires_evidence_while_parser_defensively_defaults_missing_fields()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","completion_summary":""}
            """,
            """
            {"plan_objective":"Удалить найденный фрагмент.","plan_summary":"План требует проверки доказательств.","plan_constraints":[],"plan_steps":[{"title":"Удалить","description":"Удалить диапазон в Agent Draft.","expected_editing_tool":"ripple_delete_ranges","expected_editing_arguments":{"ranges":[{"start_seconds":10,"end_seconds":20}]}}]}
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
                ImmutableArray.Create(new AgentToolDescriptor(
                    "ripple_delete_ranges",
                    "Delete approved ranges from Agent Draft.",
                    AgentToolAccess.Editing,
                    AgentToolJson.EmptyObject())),
                ImmutableArray<AgentModelObservation>.Empty,
                ImmutableArray<AgentConversationContextMessage>.Empty,
                3),
            CancellationToken.None);

        var step = Assert.Single(decision.Plan!.Steps);
        Assert.Equal(AgentEvidenceRequirement.Timeline, step.EvidenceRequirement);
        Assert.Empty(step.EvidenceObservationSequences);
        Assert.NotEmpty(step.ExpectedEffect);
        Assert.Contains(
            step.ProtectedInvariants,
            item => item.Contains("Исходная последовательность", StringComparison.Ordinal));
        Assert.NotEmpty(step.VerificationChecks);

        using var request = JsonDocument.Parse(handler.ChatRequestBodies[1]);
        var required = request.RootElement
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("plan_steps")
            .GetProperty("items")
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("evidence_requirement", required);
        Assert.Contains("evidence_observation_sequences", required);
        Assert.DoesNotContain("expected_effect", required);
        Assert.DoesNotContain("protected_invariants", required);
        Assert.DoesNotContain("verification_checks", required);
        Assert.False(request.RootElement
            .GetProperty("schema")
            .GetProperty("properties")
            .TryGetProperty("plan_constraints", out _));
    }

    [Fact]
    public async Task Homogeneous_ripple_steps_are_folded_into_one_atomic_multi_range_action()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","completion_summary":""}
            """,
            """
            {"plan_objective":"Удалить два блока.","plan_summary":"Два доказанных диапазона.","plan_steps":[{"title":"Первый","description":"Удалить первый диапазон.","expected_editing_tool":"ripple_delete_range","expected_editing_arguments":{"start_seconds":10,"end_seconds":20},"evidence_requirement":"frames","evidence_observation_sequences":[1]},{"title":"Второй","description":"Удалить второй диапазон.","expected_editing_tool":"ripple_delete_ranges","expected_editing_arguments":{"ranges":[{"start_seconds":30,"end_seconds":40}]},"evidence_requirement":"frames","evidence_observation_sequences":[2]}]}
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
        var task = CreateTask();
        task = task with
        {
            EvidenceLedger =
            [
                new AgentEvidenceRecord(
                    Guid.NewGuid(), 1, AgentEvidenceChannel.Frames, "inspect_range",
                    task.SourceSequenceId, task.SourceSequenceRevision, 10, 20,
                    "Measured first range.", ["frames"], null, DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames),
                new AgentEvidenceRecord(
                    Guid.NewGuid(), 2, AgentEvidenceChannel.Frames, "inspect_range",
                    task.SourceSequenceId, task.SourceSequenceRevision, 30, 40,
                    "Measured second range.", ["frames"], null, DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames)
            ]
        };

        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray.Create(
                    new AgentToolDescriptor(
                        "ripple_delete_range", "Delete one range.", AgentToolAccess.Editing,
                        AgentToolJson.EmptyObject()),
                    new AgentToolDescriptor(
                        "ripple_delete_ranges", "Delete ranges atomically.", AgentToolAccess.Editing,
                        AgentToolJson.EmptyObject())),
                [],
                [],
                2),
            CancellationToken.None);

        var step = Assert.Single(decision.Plan!.Steps);
        Assert.Equal("ripple_delete_ranges", step.ExpectedEditingTool);
        var ranges = step.ExpectedEditingArguments!.Value.GetProperty("ranges");
        Assert.Equal(2, ranges.GetArrayLength());
        Assert.Equal(10, ranges[0].GetProperty("start_seconds").GetDouble());
        Assert.Equal(40, ranges[1].GetProperty("end_seconds").GetDouble());
        Assert.Equal([1, 2], step.EvidenceObservationSequences.ToArray());
    }

    [Fact]
    public async Task Published_plan_deterministically_attaches_overlapping_typed_evidence()
    {
        var handler = new AgentOllamaHandler(
            """
            {"action":"publish_plan","progress":"План готов.","tool_name":"","tool_arguments":{},"question":"","question_context":"","completion_summary":""}
            """,
            """
            {"plan_objective":"Удалить блок.","plan_summary":"Диапазон измерен.","plan_steps":[{"title":"Удалить","description":"Удалить измеренный диапазон.","expected_editing_tool":"ripple_delete_range","expected_editing_arguments":{"start_seconds":10,"end_seconds":20},"evidence_requirement":"all","evidence_observation_sequences":[1]}]}
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
        var task = CreateTask();
        task = task with
        {
            EvidenceLedger =
            [
                new AgentEvidenceRecord(
                    Guid.NewGuid(), 1, AgentEvidenceChannel.Frames, "inspect_boundary",
                    task.SourceSequenceId, task.SourceSequenceRevision, 2, 18,
                    "Boundary facts.", ["frames"], null, DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames, BoundarySeconds: 10),
                new AgentEvidenceRecord(
                    Guid.NewGuid(), 2, AgentEvidenceChannel.Frames, "inspect_range",
                    task.SourceSequenceId, task.SourceSequenceRevision, 0, 30,
                    "Frames, audio and transcript measured.", ["all channels"], null,
                    DateTimeOffset.UtcNow,
                    AgentEvidenceCapabilities.Frames |
                    AgentEvidenceCapabilities.Audio |
                    AgentEvidenceCapabilities.Transcript)
            ]
        };

        var decision = await model.DecideAsync(
            new AgentModelTurnRequest(
                task,
                ImmutableArray.Create(new AgentToolDescriptor(
                    "ripple_delete_range", "Delete one range.", AgentToolAccess.Editing,
                    AgentToolJson.EmptyObject())),
                [],
                [],
                2),
            CancellationToken.None);

        Assert.Equal(
            [1, 2],
            Assert.Single(decision.Plan!.Steps).EvidenceObservationSequences.ToArray());
    }

    [Fact]
    public async Task Remote_agent_model_rejects_removed_execution_action()
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.DecideAsync(
                new AgentModelTurnRequest(
                    CreateTask(),
                    ImmutableArray<AgentToolDescriptor>.Empty,
                    ImmutableArray<AgentModelObservation>.Empty,
                    ImmutableArray<AgentConversationContextMessage>.Empty,
                    4),
                CancellationToken.None)
            .AsTask());

        using var requestDocument = JsonDocument.Parse(
            handler.ChatRequestBody);
        using var turnDocument = JsonDocument.Parse(
            requestDocument.RootElement.GetProperty("userPrompt").GetString()!);

        Assert.Equal(
            "planning",
            turnDocument.RootElement
                .GetProperty("mode")
                .GetString());
    }

    [Fact]
    public async Task Remote_agent_model_reports_verification_without_owning_the_result()
    {
        var handler = new AgentOllamaHandler(
            """
            {"accepted":false,"summary":"Факты проверки уже рассчитаны политикой.","issues":["Текстовое замечание модели"]}
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
        var report = await model.ReportVerificationAsync(
            new AgentVerificationReportRequest(
                CreateTask(),
                new AgentDeterministicVerificationResult(
                    false,
                    "Deterministic verification failed.",
                    ["Receipt mismatch."]),
                ImmutableArray<AgentModelObservation>.Empty,
                5),
            CancellationToken.None);

        Assert.False(report.Accepted);
        Assert.Equal("Факты проверки уже рассчитаны политикой.", report.Summary);
        Assert.Equal("Текстовое замечание модели", Assert.Single(report.Issues));
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
