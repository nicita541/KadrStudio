using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Core.Tests;

public sealed class AgentExecutionLoopTests
{
    [Fact]
    public async Task Approved_plan_executes_on_draft_then_requires_verification()
    {
        var orchestrator = CreateExecutingTask();
        var editingTool = new CountingTool(
            "fake_edit",
            AgentToolAccess.Editing);
        var editLogTool = new CountingTool(
            "inspect_agent_edits",
            AgentToolAccess.ReadOnly);
        var readTool = new CountingTool(
            "inspect_timeline",
            AgentToolAccess.ReadOnly);
        var registry = CreateRegistry(
            editingTool,
            editLogTool,
            readTool);
        var model = new QueueAgentModel(
            AgentModelDecision.UseTool(
                "fake_edit",
                AgentToolJson.EmptyObject(),
                "Выполняю изменение."),
            AgentModelDecision.BeginVerification(
                "Проверяю результат."),
            AgentModelDecision.UseTool(
                "inspect_agent_edits",
                AgentToolJson.EmptyObject(),
                "Сверяю журнал изменений."),
            AgentModelDecision.UseTool(
                "inspect_timeline",
                AgentToolJson.EmptyObject(),
                "Сверяю черновик."),
            AgentModelDecision.CompleteTask(
                "Черновик выполнен и проверен."));

        var loop = new AgentExecutionLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var completed = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal(
            "Черновик выполнен и проверен.",
            completed.CompletionSummary);
        Assert.Equal(1, editingTool.ExecutionCount);
        Assert.Equal(1, editLogTool.ExecutionCount);
        Assert.Equal(1, readTool.ExecutionCount);
        Assert.Contains(
            loop.Observations,
            item => item.ToolName == "fake_edit" && item.Status == AgentToolResultStatus.Succeeded);
        Assert.Contains(
            loop.Observations,
            item => item.ToolName == "inspect_timeline" && item.Status == AgentToolResultStatus.Succeeded);
    }

    [Fact]
    public async Task Corrective_edit_during_verification_forces_reverification()
    {
        var orchestrator = CreateExecutingTask();
        var editingTool = new CountingTool(
            "fake_edit",
            AgentToolAccess.Editing);
        var editLogTool = new CountingTool(
            "inspect_agent_edits",
            AgentToolAccess.ReadOnly);
        var readTool = new CountingTool(
            "inspect_timeline",
            AgentToolAccess.ReadOnly);
        var registry = CreateRegistry(
            editingTool,
            editLogTool,
            readTool);
        var model = new QueueAgentModel(
            AgentModelDecision.BeginVerification(),
            AgentModelDecision.UseTool(
                "inspect_agent_edits",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.UseTool(
                "inspect_timeline",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.UseTool(
                "fake_edit",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.CompleteTask(
                "Нельзя завершать до повторной проверки."),
            AgentModelDecision.UseTool(
                "inspect_agent_edits",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.UseTool(
                "inspect_timeline",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.CompleteTask(
                "Исправление повторно проверено."));

        var loop = new AgentExecutionLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var completed = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal(
            "Исправление повторно проверено.",
            completed.CompletionSummary);
        Assert.Equal(1, editingTool.ExecutionCount);
        Assert.Equal(2, editLogTool.ExecutionCount);
        Assert.Equal(2, readTool.ExecutionCount);
        Assert.Contains(
            loop.Observations,
            item => item.ErrorCode == "verification_edit_log_required");
    }

    [Fact]
    public async Task Execution_question_pauses_and_answer_resumes_same_draft()
    {
        var orchestrator = CreateExecutingTask();
        var editLogTool = new CountingTool(
            "inspect_agent_edits",
            AgentToolAccess.ReadOnly);
        var readTool = new CountingTool(
            "inspect_timeline",
            AgentToolAccess.ReadOnly);
        var registry = CreateRegistry(
            editLogTool,
            readTool);
        var model = new QueueAgentModel(
            AgentModelDecision.AskUser(
                "Какой из двух вариантов использовать?",
                "Инструменты не позволяют надёжно выбрать."),
            AgentModelDecision.BeginVerification(),
            AgentModelDecision.UseTool(
                "inspect_agent_edits",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.UseTool(
                "inspect_timeline",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.CompleteTask(
                "Ответ пользователя учтён; результат проверен."));

        var loop = new AgentExecutionLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var waiting = await loop.RunUntilPauseAsync();

        Assert.Equal(
            AgentTaskPhase.WaitingForUserInput,
            waiting.Phase);
        Assert.Equal(
            AgentTaskPhase.Executing,
            waiting.ResumePhase);
        Assert.True(waiting.IsDraftReadOnlyForUser);

        var question = Assert.Single(waiting.Questions);
        orchestrator.AnswerQuestion(
            question.Id,
            "Используй второй вариант.");

        var completed = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal(waiting.DraftSequenceId, completed.DraftSequenceId);
        Assert.False(completed.IsDraftReadOnlyForUser);
    }

    [Fact]
    public async Task Verification_requires_inspection_of_the_actual_agent_draft()
    {
        var orchestrator = CreateExecutingTask();
        var editLogTool = new CountingTool(
            "inspect_agent_edits",
            AgentToolAccess.ReadOnly);
        var projectTool = new CountingTool(
            "inspect_project",
            AgentToolAccess.ReadOnly);
        var sourceTimelineTool = new SourceTimelineTool();
        var draftTimelineTool = new CountingTool(
            "inspect_timeline",
            AgentToolAccess.ReadOnly);
        var registry = CreateRegistry(
            editLogTool,
            projectTool,
            sourceTimelineTool,
            draftTimelineTool);
        var model = new QueueAgentModel(
            AgentModelDecision.BeginVerification(),
            AgentModelDecision.UseTool(
                "inspect_agent_edits",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.UseTool(
                "inspect_project",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.UseTool(
                "inspect_source_timeline",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.CompleteTask(
                "Нельзя завершить без проверки Agent Draft."),
            AgentModelDecision.UseTool(
                "inspect_timeline",
                AgentToolJson.EmptyObject()),
            AgentModelDecision.CompleteTask(
                "Agent Draft проверен."));

        var loop = new AgentExecutionLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var completed = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal("Agent Draft проверен.", completed.CompletionSummary);
        Assert.Contains(
            loop.Observations,
            item => item.ErrorCode == "verification_observation_required");
    }

    [Fact]
    public async Task Execution_does_not_accept_plan_replacement()
    {
        var orchestrator = CreateExecutingTask();
        var registry = CreateRegistry(
            new CountingTool(
                "inspect_result",
                AgentToolAccess.ReadOnly));
        var model = new QueueAgentModel(
            AgentModelDecision.PublishPlan(
                CreatePlanDraft()));

        var loop = new AgentExecutionLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(registry),
            model);

        var failed = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Contains(
            "approved plan",
            failed.FailureMessage ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private static AiAgentOrchestrator CreateExecutingTask()
    {
        var orchestrator = new AiAgentOrchestrator();
        var sourceSequenceId = Guid.NewGuid();

        orchestrator.StartTask(
            Guid.NewGuid(),
            sourceSequenceId,
            "Выполни задачу по утверждённому плану.");
        orchestrator.BeginPlanning();
        orchestrator.PublishPlan(CreatePlanDraft());
        orchestrator.ApprovePlan();
        orchestrator.BeginExecution(Guid.NewGuid());

        return orchestrator;
    }

    private static AgentPlanDraft CreatePlanDraft()
        => AgentPlanDraft.Create(
            "Собрать безопасный Agent Draft.",
            "Изменить только то, что явно входит в задачу.",
            new[]
            {
                "Не менять исходную последовательность."
            },
            new[]
            {
                new AgentPlanStepDraft(
                    "Выполнить изменение",
                    "Использовать безопасные editing tools."),
                new AgentPlanStepDraft(
                    "Проверить",
                    "Сверить фактический результат read-only tools.")
            });

    private static AgentToolRegistry CreateRegistry(
        params IAgentTool[] tools)
    {
        var registry = new AgentToolRegistry();
        foreach (var tool in tools)
        {
            registry.Register(tool);
        }

        return registry;
    }

    private sealed class QueueAgentModel(
        params AgentModelDecision[] decisions) : IAgentModel
    {
        private readonly Queue<AgentModelDecision> _decisions =
            new(decisions);

        public ValueTask<AgentModelDecision> DecideAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_decisions.Count == 0)
            {
                throw new InvalidOperationException(
                    "Fake model has no remaining decision.");
            }

            return ValueTask.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class SourceTimelineTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "inspect_source_timeline",
            "Returns a non-draft sequence observation for verification tests.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    "Inspected source timeline.",
                    new { sequence_id = context.SourceSequenceId }));
        }
    }

    private sealed class CountingTool(
        string name,
        AgentToolAccess access) : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            name,
            "Test tool.",
            access,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;

            return ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    $"Executed {name}.",
                    new
                    {
                        task_id = context.TaskId,
                        draft_sequence_id = context.DraftSequenceId,
                        sequence_id = context.DraftSequenceId
                    }));
        }
    }
}
