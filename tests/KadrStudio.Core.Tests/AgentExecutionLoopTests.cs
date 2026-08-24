using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Execution;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class AgentExecutionLoopTests
{
    [Fact]
    public async Task Interpreted_plan_is_executed_once_by_runner_then_verified_automatically()
    {
        var fixture = CreateExecutingFixture();

        var completed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal(1, fixture.Edit.ExecutionCount);
        Assert.All(fixture.VerificationTools, tool => Assert.Equal(1, tool.ExecutionCount));
        Assert.Equal(0, fixture.Model.DecisionCalls);
        Assert.Single(fixture.Store.Checkpoint!.Receipts);
        Assert.Equal(AgentDraftExecutionStatus.Completed, fixture.Store.Checkpoint.Status);
    }

    [Fact]
    public async Task Model_cannot_turn_a_valid_deterministic_result_into_failure()
    {
        var fixture = CreateExecutingFixture(
            new ReporterModel(false, "Модель сомневается, но политика уже проверила результат."));

        var completed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal("Модель сомневается, но политика уже проверила результат.", completed.CompletionSummary);
        Assert.Equal(1, fixture.Model.ReportCalls);
        Assert.Equal(0, fixture.Model.DecisionCalls);
    }

    [Fact]
    public async Task Restart_skips_a_step_that_already_has_a_matching_receipt()
    {
        var fixture = CreateExecutingFixture();
        var step = Assert.Single(fixture.Orchestrator.CurrentTask!.Plan!.Steps
            .Where(item => item.ExpectedEditingTool is not null));
        fixture.Store.SeedReceipt(step);

        var completed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Completed, completed.Phase);
        Assert.Equal(0, fixture.Edit.ExecutionCount);
        Assert.Single(fixture.Store.Checkpoint!.Receipts);
        Assert.All(fixture.VerificationTools, tool => Assert.Equal(1, tool.ExecutionCount));
    }

    [Fact]
    public async Task Negative_deterministic_verification_cannot_be_overridden_by_model()
    {
        var fixture = CreateExecutingFixture(invalidIntegrity: true);

        var failed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Contains("overlapping", failed.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Model.ReportCalls);
        Assert.Equal(0, fixture.Model.DecisionCalls);
    }

    [Fact]
    public async Task Execution_requires_a_persistent_checkpoint()
    {
        var fixture = CreateExecutingFixture(withCheckpoint: false);

        var failed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Contains("checkpoint", failed.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Edit.ExecutionCount);
    }

    [Fact]
    public async Task Execution_does_not_accept_plan_replacement_or_missing_approved_tool()
    {
        var fixture = CreateExecutingFixture(registerEditingTool: false);

        var failed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Contains("approved editing tool", failed.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Model.DecisionCalls);
    }

    [Fact]
    public async Task Receipt_arguments_must_exactly_match_the_approved_step()
    {
        var fixture = CreateExecutingFixture();
        var step = Assert.Single(fixture.Orchestrator.CurrentTask!.Plan!.Steps
            .Where(item => item.ExpectedEditingTool is not null));
        fixture.Store.SeedReceipt(step, "WRONG-ARGUMENTS-FINGERPRINT");

        var failed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Contains("does not match the plan", failed.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Edit.ExecutionCount);
    }

    [Fact]
    public async Task Production_verification_requires_all_deterministic_tools()
    {
        var fixture = CreateExecutingFixture(registerComparisonTool: false);

        var failed = await fixture.CreateLoop().RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, failed.Phase);
        Assert.Contains("compare_sequences", failed.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Edit.ExecutionCount);
        Assert.Equal(0, fixture.Model.ReportCalls);
    }

    private static Fixture CreateExecutingFixture(
        ReporterModel? model = null,
        bool invalidIntegrity = false,
        bool withCheckpoint = true,
        bool registerEditingTool = true,
        bool registerComparisonTool = true)
    {
        const long sourceRevision = 7;
        var sourceSequenceId = Guid.NewGuid();
        var draftSequenceId = Guid.NewGuid();
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            sourceSequenceId,
            "Выполни задачу по утверждённому плану.",
            sourceSequenceRevision: sourceRevision);
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Выполнить одно изменение",
            "Активная последовательность"));
        orchestrator.BeginPlanning();
        orchestrator.PublishPlan(CreatePlanDraft());
        orchestrator.ApprovePlan();
        orchestrator.BeginExecution(draftSequenceId);

        var task = orchestrator.CurrentTask!;
        var plan = task.Plan!;
        var checkpoint = withCheckpoint
            ? new AgentDraftCheckpoint(
                task.Id,
                plan.Id,
                plan.Version,
                AgentPlanFingerprint.Create(plan),
                sourceSequenceId,
                sourceRevision,
                AgentDraftExecutionStatus.Executing,
                [],
                DateTimeOffset.UtcNow)
            : null;
        var store = new TestCheckpointStore(
            draftSequenceId,
            sourceSequenceId,
            sourceRevision,
            checkpoint);
        var edit = new CheckpointEditingTool(store);
        var editLog = new VerificationTool("inspect_agent_edits", store);
        var integrity = new VerificationTool(
            "inspect_timeline_integrity",
            store,
            invalidIntegrity);
        var compare = new VerificationTool("compare_sequences", store);
        var registry = new AgentToolRegistry();
        if (registerEditingTool)
        {
            registry.Register(edit);
        }
        registry.Register(editLog);
        registry.Register(integrity);
        if (registerComparisonTool)
        {
            registry.Register(compare);
        }

        return new Fixture(
            orchestrator,
            registry,
            store,
            edit,
            [editLog, integrity, compare],
            model ?? new ReporterModel(true, "Agent Draft детерминированно проверен."));
    }

    private static AgentPlanDraft CreatePlanDraft()
        => AgentPlanDraft.Create(
            "Собрать безопасный Agent Draft.",
            "Изменить только то, что явно входит в задачу.",
            ["Не менять исходную последовательность."],
            [
                new AgentPlanStepDraft(
                    "Выполнить изменение",
                    "Использовать безопасный editing tool.",
                    "fake_edit",
                    ExpectedEditingArguments: AgentToolJson.EmptyObject()),
                new AgentPlanStepDraft(
                    "Проверить",
                    "Сверить фактический результат read-only tools.")
            ]);

    private sealed record Fixture(
        AiAgentOrchestrator Orchestrator,
        AgentToolRegistry Registry,
        TestCheckpointStore Store,
        CheckpointEditingTool Edit,
        ImmutableArray<VerificationTool> VerificationTools,
        ReporterModel Model)
    {
        public AgentExecutionLoop CreateLoop()
            => new(
                Orchestrator,
                Registry,
                new AgentToolExecutor(Registry),
                Model,
                checkpointStore: Store);
    }

    private sealed class ReporterModel(
        bool accepted,
        string summary) : IAgentModel, IAgentVerificationReporter
    {
        public int DecisionCalls { get; private set; }
        public int ReportCalls { get; private set; }

        public ValueTask<AgentModelDecision> DecideAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
        {
            DecisionCalls++;
            throw new InvalidOperationException(
                "Execution must not ask the model to select editing tools or verification outcomes.");
        }

        public ValueTask<AgentVerificationReport> ReportVerificationAsync(
            AgentVerificationReportRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportCalls++;
            return ValueTask.FromResult(new AgentVerificationReport(
                accepted,
                summary,
                accepted ? [] : ["Model-only objection"]));
        }
    }

    private sealed class CheckpointEditingTool(
        TestCheckpointStore store) : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "fake_edit",
            "Commits one approved test edit.",
            AgentToolAccess.Editing,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!store.Commit(context, Descriptor.Name))
            {
                throw new AgentToolRejectedException(
                    "Agent Draft has no persistent execution checkpoint.",
                    "checkpoint_required");
            }

            ExecutionCount++;
            return ValueTask.FromResult(AgentToolExecutionOutput.From(
                "Committed approved edit.",
                new { sequence_id = context.DraftSequenceId }));
        }
    }

    private sealed class VerificationTool(
        string name,
        TestCheckpointStore store,
        bool invalidIntegrity = false) : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            name,
            "Returns deterministic test facts.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return ValueTask.FromResult(name switch
            {
                "inspect_agent_edits" => AgentToolExecutionOutput.From(
                    "Read persistent receipts.",
                    new
                    {
                        edit_count = store.Checkpoint?.Receipts.Length ?? 0,
                        edits = store.Checkpoint?.Receipts
                            .Select(receipt => new { toolName = receipt.ToolName })
                            .ToArray() ?? []
                    },
                    AgentEvidenceCapabilities.EditLog),
                "inspect_timeline_integrity" => AgentToolExecutionOutput.From(
                    "Inspected Agent Draft integrity.",
                    new
                    {
                        sequence_id = context.DraftSequenceId,
                        overlap_count = invalidIntegrity ? 1 : 0,
                        link_issue_count = 0
                    },
                    AgentEvidenceCapabilities.Integrity),
                "compare_sequences" => AgentToolExecutionOutput.From(
                    "Compared source and Agent Draft.",
                    new
                    {
                        source_sequence_id = context.SourceSequenceId,
                        draft_sequence_id = context.DraftSequenceId,
                        source_revision = store.SourceRevision,
                        draft_duration_seconds = 60d
                    },
                    AgentEvidenceCapabilities.SequenceDiff),
                _ => throw new InvalidOperationException(name)
            });
        }
    }

    private sealed class TestCheckpointStore(
        Guid draftSequenceId,
        Guid sourceSequenceId,
        long sourceRevision,
        AgentDraftCheckpoint? checkpoint) : IAgentCheckpointStore
    {
        public long SourceRevision { get; } = sourceRevision;
        public AgentDraftCheckpoint? Checkpoint { get; private set; } = checkpoint;

        public AgentDraftCheckpoint? Read(Guid requestedDraftSequenceId)
            => requestedDraftSequenceId == draftSequenceId ? Checkpoint : null;

        public long? ReadSequenceRevision(Guid sequenceId)
            => sequenceId == sourceSequenceId ? SourceRevision : null;

        public bool IsAgentDraft(Guid requestedDraftSequenceId, Guid requestedSourceSequenceId)
            => requestedDraftSequenceId == draftSequenceId &&
               requestedSourceSequenceId == sourceSequenceId;

        public bool SetStatus(AgentTaskState task, AgentDraftExecutionStatus status)
        {
            if (task.DraftSequenceId != draftSequenceId || Checkpoint is null)
            {
                return false;
            }

            Checkpoint = Checkpoint with
            {
                Status = status,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return true;
        }

        public bool Commit(AgentToolContext context, string toolName)
        {
            if (Checkpoint is null ||
                context.PlanStepId is not { } stepId ||
                context.PlanStepOrder is not { } order ||
                string.IsNullOrWhiteSpace(context.ArgumentsFingerprint))
            {
                return false;
            }
            if (Checkpoint.Receipts.Any(receipt => receipt.StepId == stepId))
            {
                return true;
            }

            var before = Checkpoint.Receipts.IsEmpty
                ? 0
                : Checkpoint.Receipts.Max(receipt => receipt.AfterDraftRevision);
            var receipt = new AgentStepReceipt(
                stepId,
                order,
                toolName,
                context.ArgumentsFingerprint,
                before,
                before + 1,
                "Committed approved edit.",
                DateTimeOffset.UtcNow);
            Checkpoint = Checkpoint with
            {
                Receipts = Checkpoint.Receipts.Add(receipt),
                UpdatedAt = receipt.AppliedAt
            };
            return true;
        }

        public void SeedReceipt(
            AgentPlanStep step,
            string? argumentsFingerprint = null)
        {
            var arguments = Assert.IsType<JsonElement>(step.ExpectedEditingArguments);
            var receipt = new AgentStepReceipt(
                step.Id,
                step.Order,
                step.ExpectedEditingTool!,
                argumentsFingerprint ?? AgentPlanFingerprint.CreateArguments(
                    step.ExpectedEditingTool!,
                    arguments),
                0,
                1,
                "Previously committed edit.",
                DateTimeOffset.UtcNow);
            Checkpoint = Assert.IsType<AgentDraftCheckpoint>(Checkpoint) with
            {
                Receipts = [receipt]
            };
        }
    }
}
