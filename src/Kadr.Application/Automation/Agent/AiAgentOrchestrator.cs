using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent;

/// <summary>
/// Small deterministic state machine for the v2.1 editorial pipeline. Model
/// reasoning and worker calls live in stage-specific services, never here.
/// </summary>
public sealed class AiAgentOrchestrator
{
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private AgentTaskState? _currentTask;
    private ImmutableArray<AgentTaskState> _history = [];

    public AiAgentOrchestrator(Func<DateTimeOffset>? utcNow = null)
        => _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public event EventHandler<AgentTaskChangedEventArgs>? TaskChanged;

    public AgentTaskState? CurrentTask
    {
        get { lock (_sync) return _currentTask; }
    }

    public ImmutableArray<AgentTaskState> History
    {
        get { lock (_sync) return _history; }
    }

    public AgentTaskState StartTask(
        Guid projectId,
        Guid sourceSequenceId,
        string userRequest,
        Guid? conversationId = null,
        long? sourceSequenceRevision = null,
        ImmutableArray<Guid> targetSourceIds = default)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        if (sourceSequenceId == Guid.Empty) throw new ArgumentException("Sequence id cannot be empty.", nameof(sourceSequenceId));
        if (string.IsNullOrWhiteSpace(userRequest)) throw new ArgumentException("Request cannot be empty.", nameof(userRequest));

        AgentTaskState created;
        lock (_sync)
        {
            if (_currentTask is { IsTerminal: false })
                throw new AgentTaskTransitionException("Only one Kadr AI Editor task can be active.");
            ArchiveTerminalLocked();
            var now = _utcNow();
            created = Append(new AgentTaskState(
                Guid.NewGuid(), projectId, sourceSequenceId, conversationId,
                userRequest.Trim(), AgentTaskPhase.Indexing, null, null, null,
                now, now, sourceSequenceRevision,
                TargetSourceIds: targetSourceIds.IsDefault ? [] : targetSourceIds.Distinct().ToImmutableArray()),
                AgentJournalKind.TaskStarted, "Kadr AI Editor task started.", now);
            _currentTask = created;
        }
        Publish(created);
        return created;
    }

    public AgentTaskState RestoreTask(AgentTaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Id == Guid.Empty || state.ProjectId == Guid.Empty || state.SourceSequenceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(state.UserRequest))
            throw new ArgumentException("Persisted task is invalid.", nameof(state));
        lock (_sync)
        {
            if (_currentTask is { IsTerminal: false })
                throw new AgentTaskTransitionException("Stop the active task before recovery.");
            ArchiveTerminalLocked();
            _currentTask = state with
            {
                Journal = state.SafeJournal,
                Checkpoint = state.Checkpoint?.Normalize(),
                TargetSourceIds = state.SafeTargetSourceIds
            };
        }
        Publish(CurrentTask!);
        return CurrentTask!;
    }

    public AgentTaskState BeginEditorialStage(AgentTaskPhase stage, string? note = null)
    {
        if (stage is not (AgentTaskPhase.Indexing or AgentTaskPhase.Directing or AgentTaskPhase.Retrieving or
            AgentTaskPhase.RoughCut or AgentTaskPhase.BoundaryRefining or AgentTaskPhase.Compiling or
            AgentTaskPhase.Verifying))
            throw new ArgumentOutOfRangeException(nameof(stage));
        return Mutate(current =>
        {
            EnsureActive(current);
            var now = _utcNow();
            return Append(current with { Phase = stage, UpdatedAt = now },
                AgentJournalKind.PhaseChanged,
                string.IsNullOrWhiteSpace(note) ? $"Entered {stage}." : note.Trim(), now);
        });
    }

    public AgentTaskState SaveCheckpoint(EditorialTaskCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Mutate(current =>
        {
            EnsureActive(current);
            var normalized = checkpoint.Normalize();
            var now = _utcNow();
            return Append(current with { Checkpoint = normalized, UpdatedAt = now },
                AgentJournalKind.CheckpointSaved, $"Checkpoint saved for {normalized.Stage}.", now);
        });
    }

    public AgentTaskState RecordProgress(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Progress cannot be empty.", nameof(message));
        return Mutate(current =>
        {
            EnsureActive(current);
            var now = _utcNow();
            return Append(current with { UpdatedAt = now }, AgentJournalKind.Progress, message.Trim(), now);
        });
    }

    public AgentTaskState ReadyForCompiledDraftReview(Guid draftSequenceId, string summary)
    {
        if (draftSequenceId == Guid.Empty) throw new ArgumentException("Draft id cannot be empty.", nameof(draftSequenceId));
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("Summary cannot be empty.", nameof(summary));
        return Mutate(current =>
        {
            EnsureActive(current);
            if (draftSequenceId == current.SourceSequenceId)
                throw new AgentTaskTransitionException("Agent Draft cannot replace the source sequence.");
            var now = _utcNow();
            return Append(current with
            {
                Phase = AgentTaskPhase.ReviewingDraft,
                DraftSequenceId = draftSequenceId,
                CompletionSummary = summary.Trim(),
                RevisionFeedback = string.Empty,
                UpdatedAt = now
            }, AgentJournalKind.DraftReviewReady, summary.Trim(), now);
        });
    }

    public AgentTaskState AcceptDraft() => Mutate(current =>
    {
        Require(current, AgentTaskPhase.ReviewingDraft);
        var now = _utcNow();
        return Append(current with { Phase = AgentTaskPhase.Accepted, UpdatedAt = now },
            AgentJournalKind.DraftAccepted, "User accepted the Agent Draft.", now);
    });

    public AgentTaskState DiscardDraft() => Mutate(current =>
    {
        Require(current, AgentTaskPhase.ReviewingDraft);
        var now = _utcNow();
        return Append(current with { Phase = AgentTaskPhase.Discarded, UpdatedAt = now },
            AgentJournalKind.DraftDiscarded, "User discarded the Agent Draft.", now);
    });

    public AgentTaskState ReviseDraft(string feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback)) throw new ArgumentException("Feedback cannot be empty.", nameof(feedback));
        return Mutate(current =>
        {
            Require(current, AgentTaskPhase.ReviewingDraft);
            var now = _utcNow();
            return Append(current with
            {
                Phase = AgentTaskPhase.Directing,
                DraftSequenceId = null,
                CompletionSummary = null,
                RevisionFeedback = feedback.Trim(),
                UpdatedAt = now
            }, AgentJournalKind.PhaseChanged, "A revised Draft was requested.", now);
        });
    }

    public AgentTaskState Fail(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("Error cannot be empty.", nameof(error));
        return Mutate(current =>
        {
            EnsureActive(current);
            var now = _utcNow();
            return Append(current with
            {
                Phase = AgentTaskPhase.Failed,
                FailureMessage = error.Trim(),
                UpdatedAt = now
            }, AgentJournalKind.TaskFailed, error.Trim(), now);
        });
    }

    public AgentTaskState Cancel(string? reason = null) => Mutate(current =>
    {
        EnsureActive(current);
        var now = _utcNow();
        var message = string.IsNullOrWhiteSpace(reason) ? "Task cancelled by user." : reason.Trim();
        return Append(current with { Phase = AgentTaskPhase.Cancelled, UpdatedAt = now },
            AgentJournalKind.TaskCancelled, message, now);
    });

    private AgentTaskState Mutate(Func<AgentTaskState, AgentTaskState> mutation)
    {
        AgentTaskState updated;
        lock (_sync)
        {
            var current = _currentTask ?? throw new AgentTaskTransitionException("There is no active task.");
            updated = mutation(current);
            _currentTask = updated;
        }
        Publish(updated);
        return updated;
    }

    private void ArchiveTerminalLocked()
    {
        if (_currentTask is null) return;
        if (!_currentTask.IsTerminal) throw new AgentTaskTransitionException("Only a terminal task can be archived.");
        _history = _history.Add(_currentTask);
        _currentTask = null;
    }

    private static void EnsureActive(AgentTaskState state)
    {
        if (state.IsTerminal) throw new AgentTaskTransitionException("The task is already terminal.");
    }

    private static void Require(AgentTaskState state, AgentTaskPhase phase)
    {
        if (state.Phase != phase)
            throw new AgentTaskTransitionException($"Operation is not allowed in phase '{state.Phase}'.");
    }

    private static AgentTaskState Append(AgentTaskState state, AgentJournalKind kind, string message, DateTimeOffset now)
        => state with
        {
            Journal = state.SafeJournal.Add(new AgentJournalEntry(Guid.NewGuid(), now, kind, message)),
            UpdatedAt = now
        };

    private void Publish(AgentTaskState state) => TaskChanged?.Invoke(this, new AgentTaskChangedEventArgs(state));
}
