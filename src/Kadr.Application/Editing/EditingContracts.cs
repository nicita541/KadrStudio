using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Editing;

public interface IEditCommand
{
    string Description { get; }
    ProjectState Apply(ProjectState project);
}

public sealed record EditTransaction(
    string Description,
    IReadOnlyList<IEditCommand> Commands,
    bool CreateCheckpoint = false,
    string? CheckpointName = null)
{
    public EditTransaction(string description, params IEditCommand[] commands)
        : this(description, commands, false, null)
    {
    }
}

public sealed record EditResult(
    bool Changed,
    ProjectState State,
    string Description,
    long Revision,
    ProjectChangeSet Changes);

public sealed class ProjectStateChangedEventArgs(
    ProjectState previous,
    ProjectState current,
    string description,
    bool isUndoOrRedo,
    ProjectChangeSet changes) : EventArgs
{
    public ProjectState Previous { get; } = previous;
    public ProjectState Current { get; } = current;
    public string Description { get; } = description;
    public bool IsUndoOrRedo { get; } = isUndoOrRedo;
    public ProjectChangeSet Changes { get; } = changes;
}

public interface IEditorSession
{
    ProjectState State { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    EditResult Execute(EditTransaction transaction);
    bool Undo();
    bool Redo();
    bool RollbackLatestTransaction();
    void ReplaceState(ProjectState state, string reason, bool clearHistory = true);
    event EventHandler<ProjectStateChangedEventArgs>? StateChanged;
}

public sealed class EditRejectedException(string message, IReadOnlyList<ValidationError>? errors = null)
    : InvalidOperationException(message)
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors ?? Array.Empty<ValidationError>();
}
