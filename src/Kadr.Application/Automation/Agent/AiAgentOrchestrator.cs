using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent;

/// <summary>
/// Owns the lifecycle of a single active AI editing task.
/// This class deliberately does not call a model or editing tools yet.
/// Stage 2 is the safe state machine that later agent/tool layers build on.
/// </summary>
public sealed class AiAgentOrchestrator
{
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;

    private AgentTaskState? _currentTask;
    private ImmutableArray<AgentTaskState> _history = ImmutableArray<AgentTaskState>.Empty;

    public AiAgentOrchestrator(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public event EventHandler<AgentTaskChangedEventArgs>? TaskChanged;

    public AgentTaskState? CurrentTask
    {
        get
        {
            lock (_sync)
            {
                return _currentTask;
            }
        }
    }

    public ImmutableArray<AgentTaskState> History
    {
        get
        {
            lock (_sync)
            {
                return _history;
            }
        }
    }

    public AgentTaskState StartTask(
        Guid projectId,
        Guid sourceSequenceId,
        string userRequest,
        Guid? conversationId = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        if (sourceSequenceId == Guid.Empty)
        {
            throw new ArgumentException("Source sequence id cannot be empty.", nameof(sourceSequenceId));
        }

        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException("User request cannot be empty.", nameof(userRequest));
        }

        AgentTaskState created;

        lock (_sync)
        {
            if (_currentTask is { IsTerminal: false })
            {
                throw new AgentTaskTransitionException(
                    "Only one AI agent task can be active at a time.");
            }

            ArchiveTerminalTaskLocked();

            var now = _utcNow();
            created = new AgentTaskState(
                Guid.NewGuid(),
                projectId,
                sourceSequenceId,
                conversationId,
                userRequest.Trim(),
                AgentTaskPhase.Understanding,
                null,
                null,
                ImmutableArray<AgentQuestion>.Empty,
                ImmutableArray<AgentJournalEntry>.Empty,
                null,
                null,
                null,
                now,
                now);

            created = AppendJournal(
                created,
                AgentJournalKind.TaskStarted,
                "Agent task started.",
                now);

            _currentTask = created;
        }

        Publish(created);
        return created;
    }

    public AgentTaskState BeginInvestigation(string? note = null)
    {
        return Mutate(current =>
        {
            RequirePhase(
                current,
                AgentTaskPhase.Understanding,
                AgentTaskPhase.Planning,
                AgentTaskPhase.WaitingForApproval,
                AgentTaskPhase.Approved);

            var now = _utcNow();
            var plan = current.Plan;

            // If an already approved task needs more investigation, approval is no longer valid.
            if (plan?.ApprovedAt is not null)
            {
                plan = plan with
                {
                    ApprovedAt = null,
                    UpdatedAt = now
                };
            }

            var updated = current with
            {
                Phase = AgentTaskPhase.Investigating,
                ResumePhase = null,
                Plan = plan,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.PhaseChanged,
                string.IsNullOrWhiteSpace(note)
                    ? "Agent started investigating the task."
                    : note.Trim(),
                now);
        });
    }

    public AgentTaskState BeginPlanning(string? note = null)
    {
        return Mutate(current =>
        {
            RequirePhase(
                current,
                AgentTaskPhase.Understanding,
                AgentTaskPhase.Investigating);

            EnsureNoOpenQuestion(current);

            var now = _utcNow();
            var updated = current with
            {
                Phase = AgentTaskPhase.Planning,
                ResumePhase = null,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.PhaseChanged,
                string.IsNullOrWhiteSpace(note)
                    ? "Agent started preparing a plan."
                    : note.Trim(),
                now);
        });
    }

    public AgentTaskState AskQuestion(string prompt, string? context = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Question cannot be empty.", nameof(prompt));
        }

        return Mutate(current =>
        {
            RequirePhase(
                current,
                AgentTaskPhase.Understanding,
                AgentTaskPhase.Investigating,
                AgentTaskPhase.Planning,
                AgentTaskPhase.WaitingForApproval,
                AgentTaskPhase.Approved,
                AgentTaskPhase.Executing,
                AgentTaskPhase.Verifying);

            EnsureNoOpenQuestion(current);

            var now = _utcNow();
            var question = new AgentQuestion(
                Guid.NewGuid(),
                prompt.Trim(),
                string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
                now);

            var updated = current with
            {
                Phase = AgentTaskPhase.WaitingForUserInput,
                ResumePhase = current.Phase,
                Questions = current.Questions.Add(question),
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.QuestionAsked,
                question.Prompt,
                now);
        });
    }

    public AgentTaskState AnswerQuestion(Guid questionId, string answer)
    {
        if (questionId == Guid.Empty)
        {
            throw new ArgumentException("Question id cannot be empty.", nameof(questionId));
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Answer cannot be empty.", nameof(answer));
        }

        return Mutate(current =>
        {
            RequirePhase(current, AgentTaskPhase.WaitingForUserInput);

            var index = FindQuestionIndex(current.Questions, questionId);
            if (index < 0)
            {
                throw new AgentTaskTransitionException(
                    $"Question '{questionId}' does not belong to the active task.");
            }

            var question = current.Questions[index];
            if (question.IsAnswered)
            {
                throw new AgentTaskTransitionException(
                    $"Question '{questionId}' has already been answered.");
            }

            var now = _utcNow();
            var answered = question with
            {
                Answer = answer.Trim(),
                AnsweredAt = now
            };

            var questions = current.Questions.SetItem(index, answered);
            var resumePhase = current.ResumePhase ?? AgentTaskPhase.Investigating;

            var updated = current with
            {
                Phase = resumePhase,
                ResumePhase = null,
                Questions = questions,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.QuestionAnswered,
                $"Question answered: {question.Prompt}",
                now);
        });
    }

    public AgentTaskState PublishPlan(AgentPlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidatePlanDraft(draft);

        return Mutate(current =>
        {
            RequirePhase(
                current,
                AgentTaskPhase.Understanding,
                AgentTaskPhase.Investigating,
                AgentTaskPhase.Planning);

            EnsureNoOpenQuestion(current);

            var now = _utcNow();
            var plan = CreatePlan(draft, now);

            var updated = current with
            {
                Phase = AgentTaskPhase.WaitingForApproval,
                ResumePhase = null,
                Plan = plan,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.PlanPublished,
                $"Plan v{plan.Version} is ready for approval.",
                now);
        });
    }

    public AgentTaskState RevisePlan(
        AgentPlanDraft draft,
        AgentPlanRevisionSource source,
        string? revisionNote = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidatePlanDraft(draft);

        return Mutate(current =>
        {
            RequirePhase(
                current,
                AgentTaskPhase.Planning,
                AgentTaskPhase.WaitingForApproval,
                AgentTaskPhase.Approved);

            if (current.Plan is null)
            {
                throw new AgentTaskTransitionException(
                    "A plan must exist before it can be revised.");
            }

            EnsureNoOpenQuestion(current);

            var now = _utcNow();
            var plan = ReviseExistingPlan(current.Plan, draft, source, now);

            var updated = current with
            {
                Phase = AgentTaskPhase.WaitingForApproval,
                ResumePhase = null,
                Plan = plan,
                UpdatedAt = now
            };

            var message = string.IsNullOrWhiteSpace(revisionNote)
                ? $"Plan revised to v{plan.Version} by {source}."
                : $"Plan revised to v{plan.Version} by {source}: {revisionNote.Trim()}";

            return AppendJournal(
                updated,
                AgentJournalKind.PlanRevised,
                message,
                now);
        });
    }

    public AgentTaskState ApprovePlan()
    {
        return Mutate(current =>
        {
            RequirePhase(current, AgentTaskPhase.WaitingForApproval);
            EnsureNoOpenQuestion(current);

            if (current.Plan is null)
            {
                throw new AgentTaskTransitionException(
                    "There is no plan to approve.");
            }

            var now = _utcNow();
            var plan = current.Plan with
            {
                ApprovedAt = now,
                UpdatedAt = now
            };

            var updated = current with
            {
                Phase = AgentTaskPhase.Approved,
                Plan = plan,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.PlanApproved,
                $"Plan v{plan.Version} approved.",
                now);
        });
    }

    public AgentTaskState BeginExecution(Guid draftSequenceId)
    {
        if (draftSequenceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Draft sequence id cannot be empty.",
                nameof(draftSequenceId));
        }

        return Mutate(current =>
        {
            RequirePhase(current, AgentTaskPhase.Approved);
            EnsureNoOpenQuestion(current);

            if (current.Plan?.ApprovedAt is null)
            {
                throw new AgentTaskTransitionException(
                    "Execution requires an approved plan.");
            }

            if (draftSequenceId == current.SourceSequenceId)
            {
                throw new AgentTaskTransitionException(
                    "The agent must execute on a draft sequence, not on the source sequence.");
            }

            var now = _utcNow();
            var updated = current with
            {
                Phase = AgentTaskPhase.Executing,
                DraftSequenceId = draftSequenceId,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.ExecutionStarted,
                "Agent execution started on a separate draft sequence.",
                now);
        });
    }

    public AgentTaskState RecordProgress(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Progress message cannot be empty.",
                nameof(message));
        }

        return Mutate(current =>
        {
            if (current.IsTerminal)
            {
                throw new AgentTaskTransitionException(
                    "A terminal agent task cannot record progress.");
            }

            var now = _utcNow();
            var updated = current with { UpdatedAt = now };

            return AppendJournal(
                updated,
                AgentJournalKind.Progress,
                message.Trim(),
                now);
        });
    }

    public AgentTaskState BeginVerification(string? note = null)
    {
        return Mutate(current =>
        {
            RequirePhase(current, AgentTaskPhase.Executing);
            EnsureNoOpenQuestion(current);

            var now = _utcNow();
            var updated = current with
            {
                Phase = AgentTaskPhase.Verifying,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.VerificationStarted,
                string.IsNullOrWhiteSpace(note)
                    ? "Agent started verifying the draft."
                    : note.Trim(),
                now);
        });
    }

    public AgentTaskState Complete(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException(
                "Completion summary cannot be empty.",
                nameof(summary));
        }

        return Mutate(current =>
        {
            RequirePhase(current, AgentTaskPhase.Verifying);
            EnsureNoOpenQuestion(current);

            var now = _utcNow();
            var updated = current with
            {
                Phase = AgentTaskPhase.Completed,
                CompletionSummary = summary.Trim(),
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.TaskCompleted,
                summary.Trim(),
                now);
        });
    }

    public AgentTaskState Fail(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException(
                "Failure message cannot be empty.",
                nameof(error));
        }

        return Mutate(current =>
        {
            if (current.IsTerminal)
            {
                throw new AgentTaskTransitionException(
                    "The active task is already terminal.");
            }

            var now = _utcNow();
            var updated = current with
            {
                Phase = AgentTaskPhase.Failed,
                ResumePhase = null,
                FailureMessage = error.Trim(),
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.TaskFailed,
                error.Trim(),
                now);
        });
    }

    public AgentTaskState Stop(string? reason = null)
    {
        return Mutate(current =>
        {
            if (current.IsTerminal)
            {
                throw new AgentTaskTransitionException(
                    "The active task is already terminal.");
            }

            var now = _utcNow();
            var message = string.IsNullOrWhiteSpace(reason)
                ? "Agent task stopped by user."
                : reason.Trim();

            var updated = current with
            {
                Phase = AgentTaskPhase.Stopped,
                ResumePhase = null,
                UpdatedAt = now
            };

            return AppendJournal(
                updated,
                AgentJournalKind.TaskStopped,
                message,
                now);
        });
    }

    private AgentTaskState Mutate(Func<AgentTaskState, AgentTaskState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        AgentTaskState updated;

        lock (_sync)
        {
            var current = _currentTask
                ?? throw new AgentTaskTransitionException(
                    "There is no active AI agent task.");

            updated = mutation(current);
            _currentTask = updated;
        }

        Publish(updated);
        return updated;
    }

    private void ArchiveTerminalTaskLocked()
    {
        if (_currentTask is null)
        {
            return;
        }

        if (!_currentTask.IsTerminal)
        {
            throw new AgentTaskTransitionException(
                "Only a terminal task can be archived.");
        }

        _history = _history.Add(_currentTask);
        _currentTask = null;
    }

    private void Publish(AgentTaskState state)
    {
        TaskChanged?.Invoke(this, new AgentTaskChangedEventArgs(state));
    }

    private static AgentTaskState AppendJournal(
        AgentTaskState state,
        AgentJournalKind kind,
        string message,
        DateTimeOffset now)
    {
        var entry = new AgentJournalEntry(
            Guid.NewGuid(),
            now,
            kind,
            message);

        return state with
        {
            Journal = state.Journal.Add(entry),
            UpdatedAt = now
        };
    }

    private static void RequirePhase(
        AgentTaskState state,
        params AgentTaskPhase[] allowed)
    {
        if (allowed.Contains(state.Phase))
        {
            return;
        }

        throw new AgentTaskTransitionException(
            $"Operation is not allowed while agent task is in phase '{state.Phase}'.");
    }

    private static void EnsureNoOpenQuestion(AgentTaskState state)
    {
        if (state.HasOpenQuestion)
        {
            throw new AgentTaskTransitionException(
                "The active user question must be answered before continuing.");
        }
    }

    private static int FindQuestionIndex(
        ImmutableArray<AgentQuestion> questions,
        Guid questionId)
    {
        for (var index = 0; index < questions.Length; index++)
        {
            if (questions[index].Id == questionId)
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidatePlanDraft(AgentPlanDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Objective))
        {
            throw new ArgumentException(
                "Plan objective cannot be empty.",
                nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.Summary))
        {
            throw new ArgumentException(
                "Plan summary cannot be empty.",
                nameof(draft));
        }

        if (draft.Steps.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Plan must contain at least one step.",
                nameof(draft));
        }

        if (draft.Steps.Any(step =>
                string.IsNullOrWhiteSpace(step.Title) ||
                string.IsNullOrWhiteSpace(step.Description)))
        {
            throw new ArgumentException(
                "Every plan step must have a title and description.",
                nameof(draft));
        }

        if (!draft.Constraints.IsDefault &&
            draft.Constraints.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Plan constraints cannot contain empty values.",
                nameof(draft));
        }
    }

    private static AgentPlan CreatePlan(
        AgentPlanDraft draft,
        DateTimeOffset now)
    {
        return new AgentPlan(
            Guid.NewGuid(),
            1,
            draft.Objective.Trim(),
            draft.Summary.Trim(),
            NormalizeConstraints(draft.Constraints),
            BuildSteps(draft.Steps),
            now,
            now,
            null,
            AgentPlanRevisionSource.Agent);
    }

    private static AgentPlan ReviseExistingPlan(
        AgentPlan existing,
        AgentPlanDraft draft,
        AgentPlanRevisionSource source,
        DateTimeOffset now)
    {
        return existing with
        {
            Version = checked(existing.Version + 1),
            Objective = draft.Objective.Trim(),
            Summary = draft.Summary.Trim(),
            Constraints = NormalizeConstraints(draft.Constraints),
            Steps = BuildSteps(draft.Steps),
            UpdatedAt = now,
            ApprovedAt = null,
            LastRevisionSource = source
        };
    }

    private static ImmutableArray<string> NormalizeConstraints(
        ImmutableArray<string> constraints)
    {
        if (constraints.IsDefaultOrEmpty)
        {
            return ImmutableArray<string>.Empty;
        }

        return constraints
            .Select(value => value.Trim())
            .ToImmutableArray();
    }

    private static ImmutableArray<AgentPlanStep> BuildSteps(
        ImmutableArray<AgentPlanStepDraft> drafts)
    {
        var builder = ImmutableArray.CreateBuilder<AgentPlanStep>(drafts.Length);

        for (var index = 0; index < drafts.Length; index++)
        {
            var draft = drafts[index];
            builder.Add(new AgentPlanStep(
                Guid.NewGuid(),
                index + 1,
                draft.Title.Trim(),
                draft.Description.Trim()));
        }

        return builder.MoveToImmutable();
    }
}
