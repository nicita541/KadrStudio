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
    public async Task Planning_model_only_receives_read_only_tool_descriptors()
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
        var descriptor = Assert.Single(request.AvailableTools);
        Assert.Equal("inspect_counter", descriptor.Name);
        Assert.Equal(AgentToolAccess.ReadOnly, descriptor.Access);
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
                    }));
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
}
