using System.Collections.Immutable;
using KadrStudio.Application.Automation.Agent.Recovery;
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
    public Guid SessionId { get; } = Guid.NewGuid();
    public long StateVersion { get; private set; }
    public long EditVersion { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler<ProjectStateChangedEventArgs>? StateChanged;

    public EditResult Execute(EditTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Commands.Count == 0)
            return new EditResult(false, _state, transaction.Description, _state.Revision, ProjectChangeSet.Empty);

        var before = _state;
        var candidate = before;
        try
        {
            foreach (var command in transaction.Commands)
                candidate = TrackEditGuard.Apply(command, candidate);
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
            return new EditResult(false, before, transaction.Description, before.Revision, ProjectChangeSet.Empty);

        candidate = InvalidateChangedRenditions(before, candidate);
        candidate = candidate with
        {
            Revision = checked(before.Revision + 1),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (transaction.SynchronizeActiveSequence)
            candidate = candidate.SynchronizeActiveSequence();
        EnsureValid(candidate);
        var changes = ProjectChangeSet.Between(before, candidate);

        AdvanceVersions(before, candidate);
        _state = candidate;
        if (transaction.RecordInHistory)
        {
            _undo.AddLast(new HistoryEntry(before, candidate, transaction.Description));
            while (_undo.Count > MaximumUndoEntries) _undo.RemoveFirst();
            _redo.Clear();
        }
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(before, candidate, transaction.Description, false, changes));
        return new EditResult(true, candidate, transaction.Description, candidate.Revision, changes);
    }

    public bool Undo()
    {
        if (_undo.Last is null) return false;
        var entry = _undo.Last.Value;
        _undo.RemoveLast();
        var previous = _state;
        _state = entry.Before with { AiConversation = AgentTaskReferences.Reconcile(previous.AiConversation, entry.Before) };
        AdvanceVersions(previous, _state, forceEdit: true);
        _redo.Push(entry);
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(
            previous, _state, $"Отмена: {entry.Description}", true, ProjectChangeSet.Between(previous, _state)));
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var entry = _redo.Pop();
        var previous = _state;
        _state = entry.After with { AiConversation = AgentTaskReferences.Reconcile(previous.AiConversation, entry.After) };
        AdvanceVersions(previous, _state, forceEdit: true);
        _undo.AddLast(entry);
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(
            previous, _state, $"Повтор: {entry.Description}", true, ProjectChangeSet.Between(previous, _state)));
        return true;
    }

    public bool RollbackLatestTransaction()
    {
        if (_undo.Last is null) return false;
        var entry = _undo.Last.Value;
        _undo.RemoveLast();
        var previous = _state;
        _state = entry.Before with { AiConversation = AgentTaskReferences.Reconcile(previous.AiConversation, entry.Before) };
        AdvanceVersions(previous, _state, forceEdit: true);
        _redo.Clear();
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(
            previous, _state, $"Rollback: {entry.Description}", true, ProjectChangeSet.Between(previous, _state)));
        return true;
    }

    public void ReplaceState(ProjectState state, string reason, bool clearHistory = true)
    {
        EnsureValid(state);
        var previous = _state;
        _state = state;
        AdvanceVersions(previous, state, forceEdit: true);
        if (clearHistory)
        {
            _undo.Clear();
            _redo.Clear();
        }
        StateChanged?.Invoke(this, new ProjectStateChangedEventArgs(
            previous, state, reason, false, ProjectChangeSet.Between(previous, state)));
    }

    public static bool HasSameEditableState(ProjectState current, ProjectState captured)
        => current with
        {
            AiConversation = captured.AiConversation,
            Revision = captured.Revision,
            UpdatedAt = captured.UpdatedAt
        } == captured;

    private void AdvanceVersions(ProjectState previous, ProjectState current, bool forceEdit = false)
    {
        StateVersion = checked(StateVersion + 1);
        if (forceEdit || !HasSameEditableState(current, previous))
            EditVersion = checked(EditVersion + 1);
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

    private static ProjectState InvalidateChangedRenditions(ProjectState before, ProjectState candidate)
    {
        if (before.RenditionGroups.IsDefaultOrEmpty || candidate.RenditionGroups.IsDefaultOrEmpty)
            return candidate;
        var changed = false;
        var tracks = candidate.Tracks;
        var groups = candidate.RenditionGroups.Select(group =>
        {
            var previous = before.RenditionGroups.FirstOrDefault(item => item.Id == group.Id);
            if (previous is null || group.IsStale) return group;
            var beforeClips = before.MediaClips.Where(item => item.TrackId == group.OriginalTrackId).OrderBy(item => item.Start);
            var afterClips = candidate.MediaClips.Where(item => item.TrackId == group.OriginalTrackId).OrderBy(item => item.Start);
            if (beforeClips.SequenceEqual(afterClips)) return group;
            changed = true;
            tracks = tracks.Select(track => track.Id switch
            {
                var id when id == group.OriginalTrackId => track with { IsVisible = true },
                var id when id == group.UpscaledTrackId => track with { IsVisible = false },
                _ => track
            }).ToImmutableArray();
            return group with
            {
                IsStale = true,
                ActiveRendition = TrackRenditionKind.Original,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }).ToImmutableArray();
        return changed ? candidate with { Tracks = tracks, RenditionGroups = groups } : candidate;
    }

    private sealed record HistoryEntry(ProjectState Before, ProjectState After, string Description);
}
