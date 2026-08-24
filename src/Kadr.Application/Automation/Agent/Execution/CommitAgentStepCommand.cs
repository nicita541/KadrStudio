using System.Collections.Immutable;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Execution;

/// <summary>
/// Applies an approved edit and appends its durable receipt to the Agent Draft
/// in one EditorSession transaction.
/// </summary>
public sealed record CommitAgentStepCommand(
    AgentToolContext Context,
    string ToolName,
    string Summary,
    IEditCommand InnerCommand) : IEditCommand
{
    public string Description => Summary;

    public ProjectState Apply(ProjectState project)
    {
        ArgumentNullException.ThrowIfNull(Context);
        ArgumentNullException.ThrowIfNull(InnerCommand);

        if (!Context.IsApprovedEditingStep)
        {
            throw new EditRejectedException(
                "Editing requires an approved plan-step execution identity.");
        }
        if (project.Id != Context.ProjectId ||
            Context.DraftSequenceId is not { } draftId ||
            draftId == Context.SourceSequenceId ||
            project.ActiveSequenceId != draftId)
        {
            throw new EditRejectedException(
                "Approved agent step is not bound to the active Agent Draft.");
        }

        var draft = project.FindSequence(draftId)
            ?? throw new EditRejectedException("Agent Draft was not found.");
        var checkpoint = draft.AgentCheckpoint
            ?? throw new EditRejectedException(
                "Agent Draft has no persistent execution checkpoint.");
        ValidateCheckpoint(checkpoint);

        var existing = checkpoint.Receipts.FirstOrDefault(receipt =>
            receipt.StepId == Context.PlanStepId!.Value);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.ToolName,
                    ToolName,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    existing.ArgumentsFingerprint,
                    Context.ArgumentsFingerprint,
                    StringComparison.Ordinal))
            {
                throw new EditRejectedException(
                    "A different action is already committed for this agent plan step.");
            }

            return project;
        }

        var edited = InnerCommand.Apply(project);
        var synchronized = edited.SynchronizeActiveSequence();
        var updatedDraft = synchronized.FindSequence(draftId)
            ?? throw new EditRejectedException("Agent Draft disappeared during the edit.");
        if (updatedDraft.Revision <= draft.Revision)
        {
            throw new EditRejectedException(
                "The approved action did not change the Agent Draft.");
        }

        var receipt = new AgentStepReceipt(
            Context.PlanStepId!.Value,
            Context.PlanStepOrder!.Value,
            ToolName.Trim(),
            Context.ArgumentsFingerprint!,
            draft.Revision,
            updatedDraft.Revision,
            string.IsNullOrWhiteSpace(Summary) ? ToolName.Trim() : Summary.Trim(),
            DateTimeOffset.UtcNow);
        var updatedCheckpoint = checkpoint with
        {
            Status = AgentDraftExecutionStatus.Executing,
            Receipts = checkpoint.Receipts.Add(receipt),
            UpdatedAt = receipt.AppliedAt
        };

        return synchronized with
        {
            Sequences = synchronized.Sequences
                .Select(sequence => sequence.Id == draftId
                    ? sequence with { AgentCheckpoint = updatedCheckpoint }
                    : sequence)
                .ToImmutableArray()
        };
    }

    private void ValidateCheckpoint(AgentDraftCheckpoint checkpoint)
    {
        if (checkpoint.TaskId != Context.TaskId ||
            checkpoint.PlanId != Context.PlanId ||
            checkpoint.PlanVersion != Context.PlanVersion ||
            checkpoint.SourceSequenceId != Context.SourceSequenceId ||
            !string.Equals(
                checkpoint.PlanFingerprint,
                Context.PlanFingerprint,
                StringComparison.Ordinal))
        {
            throw new EditRejectedException(
                "Agent Draft checkpoint does not match the approved plan.");
        }
        if (checkpoint.Status is AgentDraftExecutionStatus.Completed or
            AgentDraftExecutionStatus.Interrupted)
        {
            throw new EditRejectedException(
                $"Agent Draft checkpoint is already {checkpoint.Status.ToString().ToLowerInvariant()}.");
        }
    }
}
