using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Execution;

public interface IAgentCheckpointStore
{
    AgentDraftCheckpoint? Read(Guid draftSequenceId);

    long? ReadSequenceRevision(Guid sequenceId);

    bool IsAgentDraft(Guid draftSequenceId, Guid sourceSequenceId);

    bool SetStatus(
        AgentTaskState task,
        AgentDraftExecutionStatus status);
}

public sealed record UpdateAgentCheckpointStatusCommand(
    Guid DraftSequenceId,
    Guid TaskId,
    Guid PlanId,
    int PlanVersion,
    string PlanFingerprint,
    AgentDraftExecutionStatus Status) : IEditCommand
{
    public string Description => "Update Agent Draft checkpoint";

    public ProjectState Apply(ProjectState project)
    {
        var workspace = project.EnsureSequenceContainer().SynchronizeActiveSequence();
        var draft = workspace.FindSequence(DraftSequenceId)
            ?? throw new EditRejectedException("Agent Draft was not found.");
        var checkpoint = draft.AgentCheckpoint
            ?? throw new EditRejectedException("Agent Draft checkpoint was not found.");
        if (checkpoint.TaskId != TaskId ||
            checkpoint.PlanId != PlanId ||
            checkpoint.PlanVersion != PlanVersion ||
            !string.Equals(
                checkpoint.PlanFingerprint,
                PlanFingerprint,
                StringComparison.Ordinal))
        {
            throw new EditRejectedException(
                "Agent Draft checkpoint does not match the active approved plan.");
        }

        var updated = checkpoint with
        {
            Status = Status,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return workspace with
        {
            Sequences = workspace.Sequences
                .Select(sequence => sequence.Id == DraftSequenceId
                    ? sequence with { AgentCheckpoint = updated }
                    : sequence)
                .ToImmutableArray()
        };
    }
}
