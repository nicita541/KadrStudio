using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Storage;

public sealed record ProjectCheckpointInfo(
    Guid Id,
    Guid ProjectId,
    DateTimeOffset CreatedAt,
    string Name);

public sealed record ProjectIntegrityResult(bool IsValid, string Details);

public interface IProjectStore
{
    Task SaveAsync(string path, ProjectState project, CancellationToken cancellationToken = default);
    Task<ProjectState> LoadAsync(string path, CancellationToken cancellationToken = default);
    Task<ProjectIntegrityResult> CheckIntegrityAsync(string path, CancellationToken cancellationToken = default);
    Task<ProjectCheckpointInfo> CreateCheckpointAsync(
        string path,
        ProjectState project,
        string name,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectCheckpointInfo>> GetCheckpointsAsync(
        string path,
        CancellationToken cancellationToken = default);
    Task<ProjectState> RestoreCheckpointAsync(
        string path,
        Guid checkpointId,
        CancellationToken cancellationToken = default);
    Task DeleteCheckpointAsync(
        string path,
        Guid checkpointId,
        CancellationToken cancellationToken = default);
}

public interface IRecoveryStore
{
    Task SaveAsync(ProjectState project, string reason, CancellationToken cancellationToken = default);
    Task<ProjectState?> LoadAsync(
        Guid projectId,
        Guid? recoveryId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecoveryProjectInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(
        Guid projectId,
        Guid? recoveryId = null,
        CancellationToken cancellationToken = default);
}

public sealed record RecoveryProjectInfo(
    Guid RecoveryId,
    Guid ProjectId,
    string Name,
    long Revision,
    DateTimeOffset UpdatedAt,
    string Reason);
