using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Execution;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Agent;

public sealed class KadrAgentCheckpointStore(
    Func<ProjectState> stateProvider,
    Func<string, IEditCommand, bool> commandApplier) : IAgentCheckpointStore
{
    public AgentDraftCheckpoint? Read(Guid draftSequenceId)
        => stateProvider().FindSequence(draftSequenceId)?.AgentCheckpoint;

    public long? ReadSequenceRevision(Guid sequenceId)
        => stateProvider().FindSequence(sequenceId)?.Revision;

    public bool IsAgentDraft(Guid draftSequenceId, Guid sourceSequenceId)
    {
        var draft = stateProvider().FindSequence(draftSequenceId);
        return draft is
        {
            Status: SequenceStatus.Draft,
            ParentSequenceId: not null
        } && draft.ParentSequenceId == sourceSequenceId;
    }

    public bool SetStatus(
        AgentTaskState task,
        AgentDraftExecutionStatus status)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.DraftSequenceId is not { } draftId || task.Plan is null)
        {
            return false;
        }

        return commandApplier(
            $"Agent Draft: {status}",
            new UpdateAgentCheckpointStatusCommand(
                draftId,
                task.Id,
                task.Plan.Id,
                task.Plan.Version,
                AgentPlanFingerprint.Create(task.Plan),
                status));
    }
}
