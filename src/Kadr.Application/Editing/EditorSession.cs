using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Editing;

public sealed class EditorSession : IEditorSession
{
    private const int MaximumUndoEntries = 500;
    private readonly IProjectValidator _validator;
    private readonly LinkedList<HistoryEntry> _undo = [];
    private readonly Stack<HistoryEntry> _redo = [];
    private ProjectState _state;

    public EditorSession(ProjectState initialState, IProjectValidator? validator = null)
    {
        _validator = validator ?? new ProjectValidator();
        EnsureValid(initialState);
        _state = initialState;
    }

    public ProjectState State => _state;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler<ProjectStateChangedEventArgs>? StateChanged;

    public EditResult Execute(EditTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Commands.Count == 0)
            return new EditResult(false, _state, transaction.Description, _state.Revision);

        var before = _state;
        var candidate = before;
        try
        {
            foreach (var command in transaction.Commands)
                candidate = command.Apply(candidate);
        }
        catch (EditRejectedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EditRejectedException(
                $"Транзакция «{transaction.Description}» не применена: {exception.Message}");
        }

        if (ReferenceEquals(candidate, before) || candidate == before)
            return new EditResult(false, before, transaction.Description, before.Revision);

        candidate = candidate with
        {
            Revision = checked(before.Revision + 1),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        EnsureValid(candidate);

        _state = candidate;
        _undo.AddLast(new HistoryEntry(before, candidate, transaction.Description));
        while (_undo.Count > MaximumUndoEntries) _undo.RemoveFirst();
        _redo.Clear();
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(before, candidate, transaction.Description, false));
        return new EditResult(true, candidate, transaction.Description, candidate.Revision);
    }

    public bool Undo()
    {
        if (_undo.Last is null) return false;
        var entry = _undo.Last.Value;
        _undo.RemoveLast();
        var previous = _state;
        _state = entry.Before;
        _redo.Push(entry);
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(previous, _state, $"Отмена: {entry.Description}", true));
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var entry = _redo.Pop();
        var previous = _state;
        _state = entry.After;
        _undo.AddLast(entry);
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(previous, _state, $"Повтор: {entry.Description}", true));
        return true;
    }

    public void ReplaceState(ProjectState state, string reason, bool clearHistory = true)
    {
        EnsureValid(state);
        var previous = _state;
        _state = state;
        if (clearHistory)
        {
            _undo.Clear();
            _redo.Clear();
        }
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(previous, state, reason, false));
    }

    private void EnsureValid(ProjectState state)
    {
        var validation = _validator.Validate(state);
        if (!validation.IsValid)
            throw new EditRejectedException(
                "Проект не прошёл проверку целостности: " +
                string.Join("; ", validation.Errors.Select(item => item.Message)),
                validation.Errors);
    }

    private sealed record HistoryEntry(ProjectState Before, ProjectState After, string Description);
}
