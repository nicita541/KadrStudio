using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Core.Tests;

public sealed class AgentPlanningLoopTests
{
    [Fact]
    public async Task Tool_observation_is_returned_to_model_before_plan_is_published()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var tool = new CountingReadTool();
        registry.Register(tool);

        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool(
                "inspect_counter",
                AgentToolJson.EmptyObject(),
                "Изучаю проект."),
            AgentModelDecision.PublishPlan(
                CreatePlan(),
                "План готов."));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
        Assert.Equal(1, tool.CallCount);
        Assert.NotNull(state.Plan);
        Assert.Collection(model.Requests, _ => { }, _ => { });

        var secondRequest = model.Requests[1];
        var observation = Assert.Single(secondRequest.Observations);
        Assert.Equal("inspect_counter", observation.ToolName);
        Assert.Equal(AgentToolResultStatus.Succeeded, observation.Status);
        Assert.Equal(1, observation.Data!.Value.GetProperty("call_count").GetInt32());
    }

    [Fact]
    public async Task Real_uncertainty_pauses_for_user_and_answer_is_available_on_resume()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var model = new ScriptedAgentModel(
            AgentModelDecision.AskUser(
                "Какую из двух равнозначных версий оставить?",
                "Инструменты не дают достаточного различия."),
            AgentModelDecision.PublishPlan(CreatePlan()));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var waiting = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, waiting.Phase);
        Assert.Single(model.Requests);

        var question = Assert.Single(waiting.Questions);
        orchestrator.AnswerQuestion(
            question.Id,
            "Оставь вторую версию.");

        var planned = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, planned.Phase);
        Assert.Collection(model.Requests, _ => { }, _ => { });

        var answered = Assert.Single(
            model.Requests[1].Task.Questions.Where(item => item.IsAnswered));
        Assert.Equal("Оставь вторую версию.", answered.Answer);
    }

    [Fact]
    public async Task Rejected_tool_call_becomes_observation_and_model_can_recover()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool(
                "missing_tool",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.PublishPlan(CreatePlan()));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
        var observation = Assert.Single(model.Requests[1].Observations);
        Assert.Equal(AgentToolResultStatus.Rejected, observation.Status);
        Assert.Equal("tool_not_found", observation.ErrorCode);
    }

    [Fact]
    public async Task Repeated_identical_tool_call_is_stopped_before_extra_execution()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var tool = new CountingReadTool();
        registry.Register(tool);

        var sameArguments = AgentToolJson.EmptyObject();
        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool("inspect_counter", sameArguments),
            AgentModelDecision.UseTool("inspect_counter", sameArguments),
            AgentModelDecision.UseTool("inspect_counter", sameArguments),
            AgentModelDecision.PublishPlan(CreatePlan()));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model,
            new AgentPlanningLoopOptions(
                MaxModelTurns: 8,
                MaxObservationCount: 8,
                MaxObservationContextCharacters: 20_000,
                MaxConsecutiveIdenticalToolCalls: 2,
                MaxProgressCharacters: 600));

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
        Assert.Equal(2, tool.CallCount);
        Assert.Contains(
            model.Requests[^1].Observations,
            item => item.ErrorCode == "repeated_tool_call");
    }

    [Fact]
    public async Task Equivalent_boundary_calls_with_different_windows_are_bounded()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var tool = new CountingBoundaryTool();
        registry.Register(tool);
        var targetId = orchestrator.CurrentTask!.SourceSequenceId;
        var decisions = new[] { 10, 8, 2 }
            .Select(window => AgentModelDecision.UseTool(
                "inspect_boundary",
                AgentToolJson.ToElement(new
                {
                    target_kind = "sequence",
                    target_id = targetId,
                    at_seconds = 3.667,
                    window_seconds = window,
                    detail = "all",
                    query = $"Equivalent wording {window}."
                })))
            .Append(AgentModelDecision.PublishPlan(CreatePlan()))
            .ToArray();
        var model = new ScriptedAgentModel(decisions);
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
        Assert.Equal(2, tool.CallCount);
        Assert.Contains(
            loop.Observations,
            item => item.ErrorCode == "repeated_boundary_inspection");
    }

    [Fact]
    public async Task Retry_with_typed_evidence_requests_plan_without_more_read_tools()
    {
        var orchestrator = CreateStartedTask();
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Выполнить подтверждённое изменение.",
            "Активная последовательность."));
        var registry = new AgentToolRegistry();
        var readTool = new CountingReadTool();
        registry.Register(readTool);
        registry.Register(new FakeEditingTool());
        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool("inspect_counter", AgentToolJson.EmptyObject()));
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var failed = await loop.RunUntilPauseAsync();
        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        orchestrator.RetryFailedPlanning();
        model.Enqueue(AgentModelDecision.PublishPlan(
            AgentPlanDraft.Create(
                "Выполнить изменение в Agent Draft.",
                "План опирается на типизированное наблюдение.",
                ["Не менять source."],
                [new AgentPlanStepDraft(
                    "Применить изменение",
                    "Выполнить утверждённый editing tool.",
                    "fake_edit",
                    [1],
                    AgentToolJson.EmptyObject(),
                    AgentEvidenceRequirement.Timeline)])));

        Assert.True(loop.PrepareRetryFromExistingEvidence());
        var planned = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, planned.Phase);
        Assert.Equal(1, readTool.CallCount);
        var retryRequest = model.Requests[^1];
        Assert.Equal(
            AgentModelTurnDirective.PublishPlanFromExistingEvidence,
            retryRequest.Directive);
        Assert.All(
            retryRequest.AvailableTools,
            descriptor => Assert.Equal(AgentToolAccess.Editing, descriptor.Access));
    }

    [Fact]
    public async Task New_planning_loop_restores_persisted_evidence_and_continues_sequence_numbers()
    {
        var orchestrator = CreateStartedTask();
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Выполнить подтверждённое изменение.",
            "Активная последовательность."));
        var registry = new AgentToolRegistry();
        var readTool = new CountingReadTool();
        registry.Register(readTool);
        registry.Register(new FakeEditingTool());

        var firstModel = new ScriptedAgentModel(
            AgentModelDecision.UseTool("inspect_counter", AgentToolJson.EmptyObject()));
        var firstLoop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            firstModel);
        var failed = await firstLoop.RunUntilPauseAsync();
        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Equal(1, Assert.Single(failed.Evidence).Sequence);

        orchestrator.RetryFailedPlanning();
        var secondModel = new ScriptedAgentModel(
            AgentModelDecision.UseTool("inspect_counter", AgentToolJson.EmptyObject()),
            AgentModelDecision.PublishPlan(
                AgentPlanDraft.Create(
                    "Выполнить изменение в Agent Draft.",
                    "План опирается на восстановленное наблюдение.",
                    ["Не менять source."],
                    [new AgentPlanStepDraft(
                        "Применить изменение",
                        "Выполнить утверждённый editing tool.",
                        "fake_edit",
                        [1],
                        AgentToolJson.EmptyObject(),
                        AgentEvidenceRequirement.Timeline)])));
        var restoredLoop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            secondModel);

        Assert.True(restoredLoop.PrepareRetryFromExistingEvidence());
        var planned = await restoredLoop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, planned.Phase);
        var retryRequest = secondModel.Requests[0];
        var restored = Assert.Single(retryRequest.Observations);
        Assert.Equal(1, restored.Sequence);
        Assert.Equal("inspect_counter", restored.ToolName);
        Assert.Equal(AgentEvidenceCapabilities.Timeline, restored.EvidenceCapabilities);
        Assert.Equal(
            AgentModelTurnDirective.PublishPlanFromExistingEvidence,
            retryRequest.Directive);
        Assert.Equal(2, planned.Evidence.Max(item => item.Sequence));
    }

    [Fact]
    public async Task Content_discovery_retry_does_not_force_plan_from_coarse_evidence_only()
    {
        var orchestrator = CreateStartedTask();
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Найти и удалить смысловой блок.",
            "Вся последовательность."));
        var task = orchestrator.CurrentTask!;
        orchestrator.ReplaceEvidenceLedger([
            new AgentEvidenceRecord(
                Guid.NewGuid(),
                1,
                AgentEvidenceChannel.Frames,
                "inspect_content_overview",
                task.SourceSequenceId,
                task.SourceSequenceRevision,
                0,
                300,
                "Coarse overview",
                ["Sampled frames"],
                null,
                DateTimeOffset.UtcNow,
                AgentEvidenceCapabilities.Frames)
        ]);
        var registry = new AgentToolRegistry();
        registry.Register(new CountingReadTool());
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            new ScriptedAgentModel(AgentModelDecision.AskUser("Нужны точные границы.")));

        Assert.False(loop.PrepareRetryFromExistingEvidence());
    }

    [Fact]
    public async Task Content_discovery_probes_the_densest_unexplored_ocr_region_before_model_turn()
    {
        var orchestrator = CreateStartedTask();
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Найти и удалить смысловой блок.",
            "Вся последовательность.",
            investigationStrategy: AgentInvestigationStrategy.ContentDiscovery));
        var task = orchestrator.BeginInvestigation();
        orchestrator.ReplaceEvidenceLedger([
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
                "Tile 4 @ 131.407s: sky [text: credit B] | " +
                "Tile 5 @ 141.407s: motion [text: credit C] | " +
                "Tile 6 @ 151.407s: profile [text: credit D] | " +
                "Tile 7 @ 161.407s: running [text: credit E] | " +
                "Tile 8 @ 171.407s: street [text: credit F] | " +
                "Tile 9 @ 181.407s: crowd [text: credit G] | " +
                "Tile 10 @ 191.407s: room [text: credit H] | " +
                "Tile 11 @ 201.407s: close-up [text: credit I] | " +
                "Tile 12 @ 211.407s: landscape [text: credit J] | " +
                "Tile 13 @ 220.513s: white card [text: episode title]",
                ["Observed frame facts"],
                null,
                DateTimeOffset.UtcNow,
                AgentEvidenceCapabilities.Frames | AgentEvidenceCapabilities.Audio)
        ]);
        var registry = new AgentToolRegistry();
        var rangeTool = new CoverageRangeTool(blackFrameStart: 146.407);
        var boundaryTool = new BoundaryCaptureTool();
        registry.Register(rangeTool);
        registry.Register(boundaryTool);
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            new ContentDiscoveryModel());

        var result = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, result.Phase);
        Assert.Equal(3, rangeTool.Requests.Count);
        Assert.Equal(91.407, rangeTool.Requests[0].Start, 3);
        Assert.Equal(185.960, rangeTool.Requests[0].End, 3);
        Assert.Equal(185.960, rangeTool.Requests[1].Start, 3);
        Assert.Equal(280.513, rangeTool.Requests[1].End, 3);
        Assert.Equal(0, rangeTool.Requests[2].Start, 3);
        Assert.Equal(102, rangeTool.Requests[2].End, 3);
        Assert.All(rangeTool.Requests, request => Assert.Equal("all", request.Detail));
        Assert.Contains(boundaryTool.AtSeconds, value => Math.Abs(value - 146.407) < 0.001);
        Assert.Contains(boundaryTool.AtSeconds, value => Math.Abs(value - 225.513) < 0.001);
        Assert.Contains(result.Evidence, evidence =>
            evidence.ToolName == "inspect_range" &&
            Math.Abs(evidence.StartSeconds!.Value - 91.407) < 0.001);
        Assert.Contains(result.Evidence, evidence =>
            evidence.ToolName == "inspect_boundary" &&
            Math.Abs((evidence.StartSeconds!.Value + evidence.EndSeconds!.Value) / 2d - 146.407) < 0.001);
    }

    [Fact]
    public async Task Planning_turn_limit_fails_task_instead_of_looping_forever()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        registry.Register(new CountingReadTool());

        var model = new ScriptedAgentModel(
            Enumerable.Range(0, 8)
                .Select(index => AgentModelDecision.UseTool(
                    "inspect_counter",
                    AgentToolJson.ToElement(new { probe = index })))
                .ToArray());

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model,
            new AgentPlanningLoopOptions(
                MaxModelTurns: 3,
                MaxObservationCount: 8,
                MaxObservationContextCharacters: 20_000,
                MaxConsecutiveIdenticalToolCalls: 2,
                MaxProgressCharacters: 600));

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, state.Phase);
        Assert.NotNull(state.FailureMessage);
        Assert.Contains(
            "safety limit",
            state.FailureMessage!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Collection(model.Requests, _ => { }, _ => { }, _ => { });
    }

    [Fact]
    public async Task Waiting_for_approval_does_not_call_model_again()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var model = new ScriptedAgentModel(
            AgentModelDecision.PublishPlan(CreatePlan()));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var first = await loop.RunUntilPauseAsync();
        var second = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, first.Phase);
        Assert.Equal(first, second);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task Conversation_context_is_available_to_model_before_it_asks_questions()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var model = new ScriptedAgentModel(
            AgentModelDecision.PublishPlan(CreatePlan()));
        var priorMessage = new AgentConversationContextMessage(
            AgentConversationRole.User,
            "Не трогай первую минуту исходника.",
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model,
            conversationProvider: () => [priorMessage]);

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, state.Phase);
        var request = Assert.Single(model.Requests);
        var conversation = Assert.Single(request.Conversation);
        Assert.Equal(AgentConversationRole.User, conversation.Role);
        Assert.Equal("Не трогай первую минуту исходника.", conversation.Text);
    }

    [Fact]
    public async Task User_plan_revision_creates_new_plan_version_instead_of_replacing_task()
    {
        var orchestrator = CreateStartedTask();
        orchestrator.BeginPlanning();
        orchestrator.PublishPlan(CreatePlan());
        orchestrator.BeginInvestigation(
            "User requested a plan correction.");

        var revisedDraft = AgentPlanDraft.Create(
            "Подготовить исправленный агентский черновик.",
            "Новая версия учитывает уточнение пользователя.",
            new[]
            {
                "Не менять основной таймлайн.",
                "Не трогать первую минуту."
            },
            new[]
            {
                new AgentPlanStepDraft(
                    "Проверить уточнение",
                    "Сверить новую границу задачи с материалом."),
                new AgentPlanStepDraft(
                    "Выполнить только исправленный план",
                    "Не выходить за подтверждённые ограничения."),
                new AgentPlanStepDraft(
                    "Проверить результат",
                    "Сверить черновик после выполнения.")
            });
        var model = new ScriptedAgentModel(
            AgentModelDecision.PublishPlan(revisedDraft));
        var registry = new AgentToolRegistry();
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var revised = await loop.RunUntilPauseAsync();

        Assert.Equal(
            AgentTaskPhase.WaitingForApproval,
            revised.Phase);
        Assert.NotNull(revised.Plan);
        Assert.Equal(2, revised.Plan!.Version);
        Assert.Null(revised.Plan.ApprovedAt);
        Assert.Equal(
            AgentPlanRevisionSource.Agent,
            revised.Plan.LastRevisionSource);
        Assert.Equal(
            "Подготовить исправленный агентский черновик.",
            revised.Plan.Objective);
        Assert.Contains(
            "Не трогать первую минуту.",
            revised.Plan.Constraints);
    }

    [Fact]
    public async Task Source_revision_change_discards_stale_planning_observations()
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Подготовь план.",
            sourceSequenceRevision: 10);

        var registry = new AgentToolRegistry();
        registry.Register(new CountingReadTool());
        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool(
                "inspect_counter",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.PublishPlan(CreatePlan()),
            AgentModelDecision.PublishPlan(CreatePlan()));
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var first = await loop.RunUntilPauseAsync();
        Assert.Equal(AgentTaskPhase.WaitingForApproval, first.Phase);
        Assert.NotEmpty(model.Requests[1].Observations);

        orchestrator.BeginInvestigation(
            "Source timeline changed; refresh evidence.",
            sourceSequenceRevision: 11);

        var revised = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, revised.Phase);
        Assert.Empty(model.Requests[^1].Observations);
        Assert.Equal(11L, revised.SourceSequenceRevision);
    }

    [Fact]
    public async Task Planning_model_receives_read_tools_and_plan_only_editing_catalog()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        registry.Register(new CountingReadTool());
        registry.Register(new FakeEditingTool());

        var model = new ScriptedAgentModel(
            AgentModelDecision.PublishPlan(CreatePlan()));

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        await loop.RunUntilPauseAsync();

        var request = Assert.Single(model.Requests);
        Assert.Collection(
            request.AvailableTools.OrderBy(item => item.Name, StringComparer.Ordinal),
            descriptor =>
            {
                Assert.Equal("fake_edit", descriptor.Name);
                Assert.Equal(AgentToolAccess.Editing, descriptor.Access);
            },
            descriptor =>
            {
                Assert.Equal("inspect_counter", descriptor.Name);
                Assert.Equal(AgentToolAccess.ReadOnly, descriptor.Access);
            });
    }

    [Fact]
    public async Task Planning_model_cannot_execute_visible_editing_catalog_entry()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        registry.Register(new FakeEditingTool());
        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool("fake_edit", AgentToolJson.EmptyObject()),
            AgentModelDecision.PublishPlan(CreatePlan()));
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var result = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForApproval, result.Phase);
        Assert.Contains(loop.Observations, observation =>
            observation.ErrorCode == "editing_tool_requires_approved_plan");
    }

    [Fact]
    public async Task Semantic_edit_cannot_be_planned_from_timeline_structure_alone()
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Удали повторяющуюся служебную сцену, остальное не трогай.");
        var registry = new AgentToolRegistry();
        registry.Register(new CountingReadTool());
        registry.Register(new FakeEditingTool());
        var unsupportedPlan = AgentPlanDraft.Create(
            "Удалить служебную сцену.",
            "Границы якобы определены по структуре таймлайна.",
            ["Остальное не менять."],
            [new AgentPlanStepDraft(
                "Удалить диапазоны",
                "Удалить начало и конец.",
                "fake_edit",
                [1],
                AgentToolJson.EmptyObject(),
                AgentEvidenceRequirement.Frames)]);
        var model = new ScriptedAgentModel(
            AgentModelDecision.UseTool("inspect_counter", AgentToolJson.EmptyObject()),
            AgentModelDecision.PublishPlan(unsupportedPlan),
            AgentModelDecision.AskUser(
                "Уточните границы или разрешите визуально исследовать узкие диапазоны.",
                "Одной структуры таймлайна недостаточно."));
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var waiting = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, waiting.Phase);
        Assert.Null(waiting.Plan);
        Assert.Contains(loop.Observations, item => item.ErrorCode == "plan_evidence_required");
        Assert.Contains(
            "визуально",
            Assert.Single(waiting.Questions.Where(item => !item.IsAnswered)).Prompt,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Multiple_ripple_delete_steps_are_rejected_before_approval()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        var unsafePlan = AgentPlanDraft.Create(
            "Удалить два диапазона.",
            "Небезопасные последовательные координаты.",
            ["Остальное не менять."],
            [
                new AgentPlanStepDraft(
                    "Удалить слева",
                    "Первый ripple меняет координаты.",
                    "ripple_delete_ranges",
                    [1],
                    AgentToolJson.ToElement(new { ranges = new[] { new { start_seconds = 1, end_seconds = 2 } } }),
                    AgentEvidenceRequirement.Timeline),
                new AgentPlanStepDraft(
                    "Удалить справа",
                    "Второй ripple использует уже устаревшие координаты.",
                    "ripple_delete_ranges",
                    [1],
                    AgentToolJson.ToElement(new { ranges = new[] { new { start_seconds = 8, end_seconds = 9 } } }),
                    AgentEvidenceRequirement.Timeline)
            ]);
        var model = new ScriptedAgentModel(
            AgentModelDecision.PublishPlan(unsafePlan),
            AgentModelDecision.AskUser(
                "План нужно пересобрать одним пакетным действием.",
                "Последовательные ripple-вызовы меняют координаты."));
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var result = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, result.Phase);
        Assert.Null(result.Plan);
        Assert.Contains(loop.Observations, item =>
            item.ErrorCode == "plan_invalid" &&
            item.Summary.Contains("ripple_delete_ranges", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Content_discovery_retries_a_transient_frame_sensor_failure()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        registry.Register(new ProjectDurationTool(240));
        var rangeTool = new CoverageRangeTool(failFirstCall: true);
        registry.Register(rangeTool);
        var model = new ContentDiscoveryModel();
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model,
            new AgentPlanningLoopOptions(MaxDiscoveryCoverageAttemptsPerWindow: 2));

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, state.Phase);
        Assert.Equal(3, rangeTool.CallCount);
        var overview = Assert.Single(loop.Observations, observation =>
            observation.ToolName == "inspect_content_overview");
        Assert.Equal(AgentToolResultStatus.Succeeded, overview.Status);
        Assert.Equal(AgentEvidenceCapabilities.Frames, overview.EvidenceCapabilities);
        var samples = overview.Data!.Value.GetProperty("samples").EnumerateArray().ToArray();
        Assert.Equal(2, samples.Length);
        Assert.Equal(2, samples[0].GetProperty("attempt_count").GetInt32());
        Assert.Equal(1, samples[1].GetProperty("attempt_count").GetInt32());
    }

    [Fact]
    public async Task Incomplete_content_discovery_retains_successful_windows_with_exact_ranges()
    {
        var orchestrator = CreateStartedTask();
        var registry = new AgentToolRegistry();
        registry.Register(new ProjectDurationTool(240));
        var rangeTool = new CoverageRangeTool(permanentlyFailFromSeconds: 120);
        registry.Register(rangeTool);
        var model = new ContentDiscoveryModel();
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model,
            new AgentPlanningLoopOptions(MaxDiscoveryCoverageAttemptsPerWindow: 2));

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, state.Phase);
        Assert.Equal(3, rangeTool.CallCount);
        var partial = Assert.Single(loop.Observations, observation =>
            observation.ToolName == "inspect_content_sample");
        Assert.Equal(AgentEvidenceCapabilities.Frames, partial.EvidenceCapabilities);
        Assert.Equal(0, partial.Data!.Value.GetProperty("start_seconds").GetDouble());
        Assert.Equal(120, partial.Data.Value.GetProperty("end_seconds").GetDouble());
        var overview = Assert.Single(loop.Observations, observation =>
            observation.ToolName == "inspect_content_overview");
        Assert.Equal(AgentToolResultStatus.Failed, overview.Status);
        Assert.Equal(AgentEvidenceCapabilities.None, overview.EvidenceCapabilities);
    }

    [Fact]
    public async Task Recovery_reuses_persisted_content_samples_and_measures_only_missing_windows()
    {
        var orchestrator = CreateStartedTask();
        var task = orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Найти смысловые блоки.",
            "Вся последовательность.",
            investigationStrategy: AgentInvestigationStrategy.ContentDiscovery));
        orchestrator.ReplaceEvidenceLedger(
        [
            new AgentEvidenceRecord(
                Guid.NewGuid(),
                1,
                AgentEvidenceChannel.Frames,
                "inspect_content_sample",
                task.SourceSequenceId,
                task.SourceSequenceRevision,
                0,
                120,
                "Persisted neutral facts for 0-120s.",
                ["frames measured"],
                null,
                DateTimeOffset.UtcNow,
                AgentEvidenceCapabilities.Frames),
            new AgentEvidenceRecord(
                Guid.NewGuid(),
                2,
                AgentEvidenceChannel.Project,
                "inspect_project",
                task.SourceSequenceId,
                task.SourceSequenceRevision,
                null,
                null,
                "Persisted project summary without runtime JSON payload.",
                ["project inspected"],
                null,
                DateTimeOffset.UtcNow,
                AgentEvidenceCapabilities.Project | AgentEvidenceCapabilities.Timeline)
        ]);
        var registry = new AgentToolRegistry();
        registry.Register(new ProjectDurationTool(240));
        var rangeTool = new CoverageRangeTool();
        registry.Register(rangeTool);
        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            new ContentDiscoveryModel());

        var state = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.WaitingForUserInput, state.Phase);
        var request = Assert.Single(rangeTool.Requests);
        Assert.Equal(120, request.Start, 3);
        Assert.Equal(240, request.End, 3);
        var overview = Assert.Single(loop.Observations, observation =>
            observation.ToolName == "inspect_content_overview");
        Assert.Equal(AgentToolResultStatus.Succeeded, overview.Status);
        var samples = overview.Data!.Value.GetProperty("samples").EnumerateArray().ToArray();
        Assert.Equal(0, samples[0].GetProperty("attempt_count").GetInt32());
        Assert.Equal(1, samples[1].GetProperty("attempt_count").GetInt32());
    }

    private static AiAgentOrchestrator CreateStartedTask()
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Сделай безопасный монтаж по моей задаче.");
        return orchestrator;
    }

    private static AgentPlanDraft CreatePlan()
        => AgentPlanDraft.Create(
            "Подготовить агентский черновик.",
            "План основан на собранных наблюдениях.",
            new[]
            {
                "Не менять основной таймлайн."
            },
            new[]
            {
                new AgentPlanStepDraft(
                    "Подготовить черновик",
                    "Создать отдельную последовательность для будущего выполнения."),
                new AgentPlanStepDraft(
                    "Выполнить задачу",
                    "Применить только утверждённые изменения."),
                new AgentPlanStepDraft(
                    "Проверить",
                    "Проверить результат перед завершением.")
            });

    private sealed class ScriptedAgentModel : IAgentModel
    {
        private readonly Queue<AgentModelDecision> _decisions;

        public ScriptedAgentModel(
            params AgentModelDecision[] decisions)
        {
            _decisions = new Queue<AgentModelDecision>(decisions);
        }

        public List<AgentModelTurnRequest> Requests { get; } = [];

        public void Enqueue(AgentModelDecision decision)
            => _decisions.Enqueue(decision);

        public ValueTask<AgentModelDecision> DecideAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            if (_decisions.Count == 0)
                throw new InvalidOperationException(
                    "The scripted model has no decision left.");

            return ValueTask.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class CountingReadTool : IAgentTool
    {
        public int CallCount { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_counter",
            "Read-only counter used by agent loop tests.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.ParseObject(
                """
                {
                  "type": "object",
                  "properties": {
                    "probe": { "type": "integer" }
                  },
                  "additionalProperties": false
                }
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            return ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    "Counter inspected.",
                    new
                    {
                        call_count = CallCount,
                        task_id = context.TaskId
                    },
                    AgentEvidenceCapabilities.Timeline));
        }
    }

    private sealed class FakeEditingTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "fake_edit",
            "Editing capability that must not be shown to the planning model.",
            AgentToolAccess.Editing,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Planning loop must never execute this tool.");
    }

    private sealed class CountingBoundaryTool : IAgentTool
    {
        public int CallCount { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_boundary",
            "Read-only boundary counter used by planning policy tests.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.ParseObject(
                """
                {
                  "type":"object",
                  "properties":{
                    "target_kind":{"type":"string"},
                    "target_id":{"type":"string","format":"uuid"},
                    "at_seconds":{"type":"number"},
                    "window_seconds":{"type":"number"},
                    "detail":{"type":"string"},
                    "query":{"type":"string"}
                  },
                  "required":["target_id","at_seconds"],
                  "additionalProperties":false
                }
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(AgentToolExecutionOutput.From(
                "Boundary observed.",
                new { sequence_id = context.SourceSequenceId },
                AgentEvidenceCapabilities.Frames));
        }
    }

    private sealed class ContentDiscoveryModel : IAgentModel, IAgentTaskInterpreter
    {
        public ValueTask<AgentTaskUnderstanding> UnderstandAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new AgentTaskUnderstanding(
                AgentTaskBrief.Create(
                    AgentTaskKind.Edit,
                    "Найти и удалить смысловой блок.",
                    "Исходная последовательность.",
                    investigationStrategy: AgentInvestigationStrategy.ContentDiscovery),
                []));

        public ValueTask<AgentModelDecision> DecideAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentModelDecision.AskUser(
                "Тест завершил обзор материала."));
    }

    private sealed class ProjectDurationTool(double durationSeconds) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_project",
            "Returns source duration for discovery coverage tests.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolExecutionOutput.From(
                "Project inspected.",
                new
                {
                    channel = "timeline",
                    sequence_id = context.SourceSequenceId,
                    duration_seconds = durationSeconds
                },
                AgentEvidenceCapabilities.Project | AgentEvidenceCapabilities.Timeline));
    }

    private sealed class CoverageRangeTool(
        bool failFirstCall = false,
        double? permanentlyFailFromSeconds = null,
        double? blackFrameStart = null) : IAgentTool
    {
        public int CallCount { get; private set; }
        public List<(double Start, double End, string? Detail)> Requests { get; } = [];

        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_range",
            "Frame sensor used by deterministic discovery coverage tests.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.ParseObject(
                """
                {"type":"object","additionalProperties":true}
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var start = arguments.GetProperty("start_seconds").GetDouble();
            var end = arguments.GetProperty("end_seconds").GetDouble();
            var detail = arguments.TryGetProperty("detail", out var detailValue)
                ? detailValue.GetString()
                : null;
            Requests.Add((start, end, detail));
            if ((failFirstCall && CallCount == 1) ||
                permanentlyFailFromSeconds is { } threshold && start >= threshold)
            {
                throw new InvalidOperationException("Transient frame sensor failure.");
            }

            return ValueTask.FromResult(AgentToolExecutionOutput.From(
                $"Visible facts observed in {start:0.###}-{end:0.###}s.",
                new
                {
                    channel = "frames",
                    sequence_id = context.SourceSequenceId,
                    start_seconds = start,
                    end_seconds = end,
                    analyses = blackFrameStart is null
                        ? []
                        : new object[]
                        {
                            new
                            {
                                timeline_start_seconds = start,
                                timeline_end_seconds = end,
                                source_start_seconds = start,
                                source_end_seconds = end,
                                observation = new
                                {
                                    analysis = new
                                    {
                                        ranges = new[]
                                        {
                                            new
                                            {
                                                kind = "blackframe",
                                                start_seconds = blackFrameStart.Value,
                                                end_seconds = blackFrameStart.Value + 1.5
                                            }
                                        }
                                    }
                                }
                            }
                        }
                },
                AgentEvidenceCapabilities.Frames));
        }
    }

    private sealed class BoundaryCaptureTool : IAgentTool
    {
        public List<double> AtSeconds { get; } = [];

        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_boundary",
            "Captures deterministic boundary probes.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.ParseObject(
                """
                {"type":"object","additionalProperties":true}
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            var at = arguments.GetProperty("at_seconds").GetDouble();
            AtSeconds.Add(at);
            return ValueTask.FromResult(AgentToolExecutionOutput.From(
                $"Boundary around {at:0.###}s observed.",
                new
                {
                    channel = "all",
                    sequence_id = context.SourceSequenceId,
                    start_seconds = Math.Max(0, at - 15),
                    end_seconds = at + 15
                },
                AgentEvidenceCapabilities.Frames |
                AgentEvidenceCapabilities.Audio |
                AgentEvidenceCapabilities.Transcript));
        }
    }

}
