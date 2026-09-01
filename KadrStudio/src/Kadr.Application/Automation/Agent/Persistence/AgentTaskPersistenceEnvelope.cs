namespace KadrStudio.Application.Automation.Agent.Persistence;

public sealed record AgentTaskPersistenceEnvelope(int FormatVersion, AgentTaskState Task)
{
    public const int CurrentFormatVersion = 4;

    public static AgentTaskPersistenceEnvelope Create(AgentTaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new AgentTaskPersistenceEnvelope(CurrentFormatVersion, task with
        {
            Journal = task.SafeJournal,
            Checkpoint = task.Checkpoint?.Normalize(),
            TargetSourceIds = task.SafeTargetSourceIds
        });
    }
}
