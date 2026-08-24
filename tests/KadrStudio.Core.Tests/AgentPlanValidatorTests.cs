using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Planning;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Core.Tests;

public sealed class AgentPlanValidatorTests
{
    [Fact]
    public void Edit_task_rejects_an_empty_edit_plan()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Timeline);
        var plan = AgentPlanDraft.Create(
            "Ничего не менять",
            "Пустой план",
            [],
            [new AgentPlanStepDraft("Проверить", "Только чтение")]);

        var result = new AgentPlanValidator(registry).Validate(task, plan, observations);

        Assert.False(result.IsValid);
        Assert.Contains("at least one exact editing action", result.Error);
    }

    [Fact]
    public void Published_edit_plan_rejects_research_or_verification_steps()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Timeline);
        var plan = AgentPlanDraft.Create(
            "Удалить диапазон",
            "Монтаж и лишний служебный шаг",
            [],
            [
                new AgentPlanStepDraft(
                    "Удалить",
                    "Удалить доказанный диапазон",
                    "ripple_delete_range",
                    [1],
                    AgentToolJson.ToElement(new { start_seconds = 5, end_seconds = 15 }),
                    AgentEvidenceRequirement.Timeline),
                new AgentPlanStepDraft(
                    "Проверить",
                    "Проверка должна запускаться workflow автоматически")
            ]);

        var result = new AgentPlanValidator(registry).Validate(task, plan, observations);

        Assert.False(result.IsValid);
        Assert.Contains("only editing actions", result.Error);
    }

    [Fact]
    public void Editing_arguments_must_match_the_complete_tool_schema()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Timeline);
        var plan = CreatePlan(
            [1],
            AgentEvidenceRequirement.Timeline,
            AgentToolJson.ToElement(new { start_seconds = 5 }));

        var result = new AgentPlanValidator(registry).Validate(task, plan, observations);

        Assert.False(result.IsValid);
        Assert.Contains("end_seconds", result.Error);
    }

    [Fact]
    public void Ripple_delete_end_must_be_after_start_before_plan_approval()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Timeline);
        var plan = CreatePlan(
            [1],
            AgentEvidenceRequirement.Timeline,
            AgentToolJson.ToElement(new { start_seconds = 15, end_seconds = 5 }));

        var result = new AgentPlanValidator(registry).Validate(task, plan, observations);

        Assert.False(result.IsValid);
        Assert.Contains("end greater than start", result.Error);
    }

    [Fact]
    public void Evidence_from_an_old_source_revision_is_rejected()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Frames,
            evidenceRevision: 4,
            sourceRevision: 5);
        var plan = CreatePlan([1], AgentEvidenceRequirement.Frames);

        var result = new AgentPlanValidator(registry).Validate(task, plan, observations);

        Assert.False(result.IsValid);
        Assert.Contains("outdated source revision", result.Error);
    }

    [Fact]
    public void Persisted_typed_evidence_remains_valid_after_observation_memory_is_trimmed()
    {
        var (task, registry, _) = CreateContext(
            AgentEvidenceCapabilities.Frames);

        var result = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1], AgentEvidenceRequirement.Frames),
            []);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public void All_evidence_requires_frames_audio_and_transcript_capabilities()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Frames,
            AgentEvidenceCapabilities.Audio);
        var incomplete = CreatePlan([1, 2], AgentEvidenceRequirement.All);

        var rejected = new AgentPlanValidator(registry).Validate(
            task,
            incomplete,
            observations);

        Assert.False(rejected.IsValid);
        Assert.Contains("all evidence", rejected.Error);

        var completeEvidence = new AgentEvidenceRecord(
            Guid.NewGuid(),
            3,
            AgentEvidenceChannel.Transcript,
            "inspect_range",
            task.SourceSequenceId,
            task.SourceSequenceRevision,
            5,
            15,
            "Transcript evidence",
            ["Speech"],
            null,
            DateTimeOffset.UtcNow,
            AgentEvidenceCapabilities.Transcript);
        task = task with { EvidenceLedger = task.Evidence.Add(completeEvidence) };
        observations = observations.Add(new AgentModelObservation(
            3,
            "inspect_range",
            AgentToolResultStatus.Succeeded,
            "Transcript evidence",
            AgentToolJson.EmptyObject(),
            null,
            AgentEvidenceCapabilities.Transcript));

        var accepted = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1, 2, 3], AgentEvidenceRequirement.All),
            observations);

        Assert.True(accepted.IsValid, accepted.Error);
    }

    [Fact]
    public void Content_discovery_ripple_coordinates_require_referenced_boundary_evidence()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Frames);
        task = task with
        {
            Brief = task.Brief! with
            {
                InvestigationStrategy = AgentInvestigationStrategy.ContentDiscovery
            }
        };

        var rejected = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1], AgentEvidenceRequirement.Frames),
            observations);

        Assert.False(rejected.IsValid);
        Assert.Contains("inspect_boundary", rejected.Error);

        var now = DateTimeOffset.UtcNow;
        var broadBoundary = new AgentEvidenceRecord(
            Guid.NewGuid(), 2, AgentEvidenceChannel.Frames, "inspect_boundary",
            task.SourceSequenceId, task.SourceSequenceRevision, 0, 60,
            "Broad boundary probe", ["Coarse measurement"], null, now,
            AgentEvidenceCapabilities.Frames);
        var broadTask = task with { EvidenceLedger = task.Evidence.Add(broadBoundary) };
        var broadObservations = observations.Add(new AgentModelObservation(
            broadBoundary.Sequence,
            broadBoundary.ToolName,
            AgentToolResultStatus.Succeeded,
            broadBoundary.Summary,
            AgentToolJson.EmptyObject(),
            null,
            broadBoundary.Capabilities));
        var broadResult = new AgentPlanValidator(registry).Validate(
            broadTask,
            CreatePlan([1, 2], AgentEvidenceRequirement.Frames),
            broadObservations);

        Assert.False(broadResult.IsValid);
        Assert.Contains("internal start boundary", broadResult.Error);

        var boundaries = ImmutableArray.Create(
            new AgentEvidenceRecord(
                Guid.NewGuid(), 2, AgentEvidenceChannel.Frames, "inspect_boundary",
                task.SourceSequenceId, task.SourceSequenceRevision, 4, 6,
                "Start boundary", ["Measured start"], null, now,
                AgentEvidenceCapabilities.Frames),
            new AgentEvidenceRecord(
                Guid.NewGuid(), 3, AgentEvidenceChannel.Frames, "inspect_boundary",
                task.SourceSequenceId, task.SourceSequenceRevision, 14, 16,
                "End boundary", ["Measured end"], null, now,
                AgentEvidenceCapabilities.Frames));
        task = task with { EvidenceLedger = task.Evidence.AddRange(boundaries) };
        observations = observations.AddRange(boundaries.Select(item =>
            new AgentModelObservation(
                item.Sequence,
                item.ToolName,
                AgentToolResultStatus.Succeeded,
                item.Summary,
                AgentToolJson.EmptyObject(),
                null,
                item.Capabilities)));

        var accepted = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1, 2, 3], AgentEvidenceRequirement.Frames),
            observations);

        Assert.True(accepted.IsValid, accepted.Error);
    }

    [Fact]
    public void Content_discovery_rejects_timeline_only_evidence()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Timeline);
        task = task with
        {
            Brief = task.Brief! with
            {
                InvestigationStrategy = AgentInvestigationStrategy.ContentDiscovery
            }
        };

        var result = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1], AgentEvidenceRequirement.Timeline),
            observations);

        Assert.False(result.IsValid);
        Assert.Contains("timeline geometry alone", result.Error);
    }

    [Fact]
    public void Content_discovery_cannot_downgrade_from_available_multichannel_evidence()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Frames,
            AgentEvidenceCapabilities.Audio,
            AgentEvidenceCapabilities.Transcript);
        task = task with
        {
            Brief = task.Brief! with
            {
                InvestigationStrategy = AgentInvestigationStrategy.ContentDiscovery
            }
        };

        var result = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1, 2, 3], AgentEvidenceRequirement.Frames),
            observations);

        Assert.False(result.IsValid);
        Assert.Equal("plan_evidence_required", result.ErrorCode);
        Assert.Contains("must require all channels", result.Error);
    }

    [Fact]
    public void Content_discovery_evidence_must_cover_the_complete_deleted_range()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Frames);
        task = task with
        {
            Brief = task.Brief! with
            {
                InvestigationStrategy = AgentInvestigationStrategy.ContentDiscovery
            },
            EvidenceLedger = task.Evidence
                .Select(item => item with { StartSeconds = 10, EndSeconds = 15 })
                .ToImmutableArray()
        };
        var now = DateTimeOffset.UtcNow;
        var boundaries = ImmutableArray.Create(
            new AgentEvidenceRecord(
                Guid.NewGuid(), 2, AgentEvidenceChannel.Frames, "inspect_boundary",
                task.SourceSequenceId, task.SourceSequenceRevision, 4, 6,
                "Start boundary", ["Measured start"], null, now,
                AgentEvidenceCapabilities.Frames, BoundarySeconds: 5),
            new AgentEvidenceRecord(
                Guid.NewGuid(), 3, AgentEvidenceChannel.Frames, "inspect_boundary",
                task.SourceSequenceId, task.SourceSequenceRevision, 14, 16,
                "End boundary", ["Measured end"], null, now,
                AgentEvidenceCapabilities.Frames, BoundarySeconds: 15));
        task = task with { EvidenceLedger = task.Evidence.AddRange(boundaries) };
        observations = observations.AddRange(boundaries.Select(item =>
            new AgentModelObservation(
                item.Sequence,
                item.ToolName,
                AgentToolResultStatus.Succeeded,
                item.Summary,
                AgentToolJson.EmptyObject(),
                null,
                item.Capabilities)));

        var result = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1, 2, 3], AgentEvidenceRequirement.Frames),
            observations);

        Assert.False(result.IsValid);
        Assert.Contains("complete source deletion range", result.Error);
    }

    [Fact]
    public void Content_discovery_rejects_boundary_evidence_from_another_target()
    {
        var (task, registry, observations) = CreateContext(
            AgentEvidenceCapabilities.Frames);
        task = task with
        {
            Brief = task.Brief! with
            {
                InvestigationStrategy = AgentInvestigationStrategy.ContentDiscovery
            }
        };
        var now = DateTimeOffset.UtcNow;
        var otherTarget = Guid.NewGuid();
        var boundaries = ImmutableArray.Create(
            new AgentEvidenceRecord(
                Guid.NewGuid(), 2, AgentEvidenceChannel.Frames, "inspect_boundary",
                otherTarget, task.SourceSequenceRevision, 4, 6,
                "Wrong-target start", ["Measured elsewhere"], null, now,
                AgentEvidenceCapabilities.Frames),
            new AgentEvidenceRecord(
                Guid.NewGuid(), 3, AgentEvidenceChannel.Frames, "inspect_boundary",
                otherTarget, task.SourceSequenceRevision, 14, 16,
                "Wrong-target end", ["Measured elsewhere"], null, now,
                AgentEvidenceCapabilities.Frames));
        task = task with { EvidenceLedger = task.Evidence.AddRange(boundaries) };
        observations = observations.AddRange(boundaries.Select(item =>
            new AgentModelObservation(
                item.Sequence,
                item.ToolName,
                AgentToolResultStatus.Succeeded,
                item.Summary,
                AgentToolJson.EmptyObject(),
                null,
                item.Capabilities)));

        var result = new AgentPlanValidator(registry).Validate(
            task,
            CreatePlan([1, 2, 3], AgentEvidenceRequirement.Frames),
            observations);

        Assert.False(result.IsValid);
        Assert.Contains("internal start boundary", result.Error);
    }

    private static (AgentTaskState Task, AgentToolRegistry Registry,
        ImmutableArray<AgentModelObservation> Observations) CreateContext(
        params AgentEvidenceCapabilities[] capabilities)
        => CreateContext(capabilities, 5, 5);

    private static (AgentTaskState Task, AgentToolRegistry Registry,
        ImmutableArray<AgentModelObservation> Observations) CreateContext(
        AgentEvidenceCapabilities capability,
        long evidenceRevision,
        long sourceRevision)
        => CreateContext([capability], evidenceRevision, sourceRevision);

    private static (AgentTaskState Task, AgentToolRegistry Registry,
        ImmutableArray<AgentModelObservation> Observations) CreateContext(
        AgentEvidenceCapabilities[] capabilities,
        long evidenceRevision,
        long sourceRevision)
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Удалить подтверждённый фрагмент.",
            sourceSequenceRevision: sourceRevision);
        orchestrator.SetTaskBrief(AgentTaskBrief.Create(
            AgentTaskKind.Edit,
            "Удалить фрагмент",
            "Активная последовательность"));
        var task = orchestrator.CurrentTask!;
        var evidence = capabilities.Select((capability, index) => new AgentEvidenceRecord(
            Guid.NewGuid(),
            index + 1,
            AgentEvidenceChannel.Timeline,
            "inspect_range",
            task.SourceSequenceId,
            evidenceRevision,
            5,
            15,
            "Evidence",
            ["Fact"],
            null,
            DateTimeOffset.UtcNow,
            capability)).ToImmutableArray();
        task = task with { EvidenceLedger = evidence };
        var observations = capabilities.Select((capability, index) => new AgentModelObservation(
            index + 1,
            "inspect_range",
            AgentToolResultStatus.Succeeded,
            "Evidence",
            AgentToolJson.EmptyObject(),
            null,
            capability)).ToImmutableArray();

        var registry = new AgentToolRegistry();
        registry.Register(new SchemaEditingTool());
        return (task, registry, observations);
    }

    private static AgentPlanDraft CreatePlan(
        ImmutableArray<int> observationSequences,
        AgentEvidenceRequirement requirement,
        JsonElement? arguments = null)
        => AgentPlanDraft.Create(
            "Удалить диапазон",
            "Точный монтажный шаг",
            [],
            [new AgentPlanStepDraft(
                "Удалить",
                "Удалить доказанный диапазон",
                "ripple_delete_range",
                observationSequences,
                arguments ?? AgentToolJson.ToElement(new
                {
                    start_seconds = 5,
                    end_seconds = 15
                }),
                requirement)]);

    private sealed class SchemaEditingTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "ripple_delete_range",
            "Test editing schema",
            AgentToolAccess.Editing,
            AgentToolJson.ParseObject(
                """
                {"type":"object","properties":{"start_seconds":{"type":"number","minimum":0},"end_seconds":{"type":"number","minimum":0}},"required":["start_seconds","end_seconds"],"additionalProperties":false}
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
