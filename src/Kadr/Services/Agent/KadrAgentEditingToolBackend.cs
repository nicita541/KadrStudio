using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.Editing;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Applies only validated agent edits to the active Agent Draft. The backend
/// never accepts the source sequence as an editing target.
/// </summary>
public sealed class KadrAgentEditingToolBackend(
    Func<ProjectState> stateProvider,
    Func<string, IEditCommand, bool> commandApplier) : IAgentEditingToolBackend
{
    private readonly object _gate = new();
    private readonly List<AgentAppliedEdit> _editLog = [];
    private Guid? _taskId;
    private int _nextEditSequence = 1;

    public void Reset(Guid taskId)
    {
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("Task id cannot be empty.", nameof(taskId));
        }

        lock (_gate)
        {
            _taskId = taskId;
            _editLog.Clear();
            _nextEditSequence = 1;
        }
    }

    public ValueTask<JsonElement> RippleDeleteRangeAsync(
        AgentToolContext context,
        double startSeconds,
        double endSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var range = new TimeRange(
            TimelineTime.FromSeconds(startSeconds),
            TimelineTime.FromSeconds(endSeconds - startSeconds));

        if (range.End > before.Duration)
        {
            throw new AgentToolRejectedException(
                "range_outside_draft",
                "Requested removal extends beyond the Agent Draft.");
        }

        Apply(
            context,
            "ripple_delete_range",
            reason,
            $"Agent: удалить диапазон {startSeconds:0.###}-{endSeconds:0.###} с",
            new RippleDeleteRangeCommand(range));

        var after = RequireDraft(context);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            before_duration_seconds = Round(before.Duration.TotalSeconds),
            after_duration_seconds = Round(after.Duration.TotalSeconds),
            removed_duration_seconds = Round(range.Duration.TotalSeconds),
            range = new
            {
                start_seconds = Round(startSeconds),
                end_seconds = Round(endSeconds)
            }
        }));
    }

    public ValueTask<JsonElement> RippleDeleteRangesAsync(
        AgentToolContext context,
        IReadOnlyList<AgentTimelineRange> ranges,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ranges is null || ranges.Count == 0)
        {
            throw new AgentToolRejectedException(
                "ranges_required",
                "At least one removal range is required.");
        }

        var before = RequireDraft(context);
        var ordered = ranges
            .Select(item => new AgentTimelineRange(
                item.StartSeconds,
                item.EndSeconds))
            .OrderBy(item => item.StartSeconds)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            if (!double.IsFinite(item.StartSeconds) ||
                !double.IsFinite(item.EndSeconds) ||
                item.StartSeconds < 0 ||
                item.EndSeconds <= item.StartSeconds)
            {
                throw new AgentToolRejectedException(
                    "invalid_range",
                    "Every removal range must be finite, non-negative and have end greater than start.");
            }

            if (TimelineTime.FromSeconds(item.EndSeconds) > before.Duration)
            {
                throw new AgentToolRejectedException(
                    "range_outside_draft",
                    "A requested removal range extends beyond the Agent Draft.");
            }

            if (index > 0 && item.StartSeconds < ordered[index - 1].EndSeconds)
            {
                throw new AgentToolRejectedException(
                    "overlapping_ranges",
                    "ripple_delete_ranges requires non-overlapping ranges measured on the same draft state.");
            }
        }

        var commands = ordered
            .OrderByDescending(item => item.StartSeconds)
            .Select(item => (IEditCommand)new RippleDeleteRangeCommand(
                new TimeRange(
                    TimelineTime.FromSeconds(item.StartSeconds),
                    TimelineTime.FromSeconds(item.EndSeconds - item.StartSeconds))))
            .ToArray();

        var description = $"Agent: удалить {ordered.Length} диапазон(ов) со сдвигом";
        RequireDraft(context);
        if (!commandApplier(
                description,
                new EditBatchCommand(description, commands)))
        {
            throw new AgentToolRejectedException(
                "no_change",
                "The requested range removals did not change the Agent Draft.");
        }

        var after = RequireDraft(context);
        lock (_gate)
        {
            _editLog.Add(new AgentAppliedEdit(
                _nextEditSequence++,
                "ripple_delete_ranges",
                reason.Trim(),
                description,
                DateTimeOffset.UtcNow));
        }

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            before_duration_seconds = Round(before.Duration.TotalSeconds),
            after_duration_seconds = Round(after.Duration.TotalSeconds),
            removed_duration_seconds = Round(
                ordered.Sum(item => item.EndSeconds - item.StartSeconds)),
            ranges = ordered.Select(item => new
            {
                start_seconds = Round(item.StartSeconds),
                end_seconds = Round(item.EndSeconds)
            }).ToArray()
        }));
    }

    public ValueTask<JsonElement> SplitTimelineAsync(
        AgentToolContext context,
        double positionSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var position = TimelineTime.FromSeconds(positionSeconds);

        if (position <= TimelineTime.Zero || position >= before.Duration)
        {
            throw new AgentToolRejectedException(
                "split_outside_draft",
                "Split position must be inside the Agent Draft.");
        }

        var crossingCount = before.MediaClips.Count(
            clip => position > clip.Start && position < clip.End);

        Apply(
            context,
            "split_timeline_at",
            reason,
            $"Agent: разрезать таймлайн в {positionSeconds:0.###} с",
            new SplitMediaClipsCommand(position));

        var after = RequireDraft(context);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            position_seconds = Round(positionSeconds),
            split_clip_count = crossingCount,
            media_clip_count = after.MediaClips.Length
        }));
    }

    public ValueTask<JsonElement> DeleteClipsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> clipIds,
        bool includeLinked,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var ids = clipIds.ToHashSet();

        var missing = ids.Where(id => before.MediaClips.All(clip => clip.Id != id)).ToArray();
        if (missing.Length > 0)
        {
            throw new AgentToolRejectedException(
                "clip_not_found",
                $"Draft clip(s) not found: {string.Join(", ", missing)}.");
        }

        Apply(
            context,
            "delete_clips",
            reason,
            $"Agent: удалить {ids.Count} клип(ов)",
            new DeleteMediaClipsCommand(ids, includeLinked));

        var after = RequireDraft(context);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            requested_clip_ids = ids,
            include_linked = includeLinked,
            before_clip_count = before.MediaClips.Length,
            after_clip_count = after.MediaClips.Length
        }));
    }

    public ValueTask<JsonElement> TrimClipAsync(
        AgentToolContext context,
        Guid clipId,
        string edge,
        double edgeSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var clip = before.MediaClips.FirstOrDefault(item => item.Id == clipId)
            ?? throw new AgentToolRejectedException(
                "clip_not_found",
                $"Draft clip '{clipId}' was not found.");

        var trimEdge = edge switch
        {
            "left" => TrimEdge.Left,
            "right" => TrimEdge.Right,
            _ => throw new AgentToolRejectedException(
                "invalid_trim_edge",
                "Trim edge must be left or right.")
        };

        Apply(
            context,
            "trim_clip",
            reason,
            $"Agent: подрезать клип {clipId}",
            new TrimMediaClipCommand(
                clipId,
                trimEdge,
                TimelineTime.FromSeconds(edgeSeconds)));

        var after = RequireDraft(context);
        var updated = after.MediaClips.FirstOrDefault(item => item.Id == clipId)
            ?? throw new AgentToolRejectedException(
                "clip_missing_after_edit",
                "Trimmed clip disappeared unexpectedly.");

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            clip_id = clipId,
            edge,
            previous = ClipTiming(clip),
            current = ClipTiming(updated)
        }));
    }

    public ValueTask<JsonElement> MoveClipAsync(
        AgentToolContext context,
        Guid clipId,
        Guid targetTrackId,
        double startSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var clip = before.MediaClips.FirstOrDefault(item => item.Id == clipId)
            ?? throw new AgentToolRejectedException(
                "clip_not_found",
                $"Draft clip '{clipId}' was not found.");

        var targetTrack = before.Tracks.FirstOrDefault(item => item.Id == targetTrackId)
            ?? throw new AgentToolRejectedException(
                "track_not_found",
                $"Draft track '{targetTrackId}' was not found.");

        var sourceTrack = before.Tracks.FirstOrDefault(item => item.Id == clip.TrackId)
            ?? throw new AgentToolRejectedException(
                "track_not_found",
                "Source track was not found.");

        if (sourceTrack.Kind != targetTrack.Kind)
        {
            throw new AgentToolRejectedException(
                "incompatible_track",
                "A clip can only move between tracks of the same kind.");
        }

        Apply(
            context,
            "move_clip",
            reason,
            $"Agent: переместить клип {clipId}",
            new MoveMediaClipCommand(
                clipId,
                targetTrackId,
                TimelineTime.FromSeconds(startSeconds)));

        var after = RequireDraft(context);
        var updated = after.MediaClips.First(item => item.Id == clipId);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            clip_id = clipId,
            previous_track_id = clip.TrackId,
            current_track_id = updated.TrackId,
            previous_start_seconds = Round(clip.Start.TotalSeconds),
            current_start_seconds = Round(updated.Start.TotalSeconds)
        }));
    }

    public ValueTask<JsonElement> SetClipVolumeAsync(
        AgentToolContext context,
        Guid clipId,
        double volume,
        bool muted,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var clip = before.MediaClips.FirstOrDefault(item => item.Id == clipId)
            ?? throw new AgentToolRejectedException(
                "clip_not_found",
                $"Draft clip '{clipId}' was not found.");

        var track = before.Tracks.FirstOrDefault(item => item.Id == clip.TrackId)
            ?? throw new AgentToolRejectedException(
                "track_not_found",
                "Clip track was not found.");

        if (track.Kind != TrackKind.Audio)
        {
            throw new AgentToolRejectedException(
                "audio_track_required",
                "set_clip_volume requires a clip on an audio track.");
        }

        var audio = (clip.Audio ?? new AudioParameters()) with
        {
            Volume = Math.Clamp(volume, 0, 4),
            IsMuted = muted
        };

        Apply(
            context,
            "set_clip_volume",
            reason,
            $"Agent: изменить громкость клипа {clipId}",
            new UpsertMediaClipCommand(clip with { Audio = audio }));

        var after = RequireDraft(context);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            clip_id = clipId,
            volume = Math.Round(audio.Volume, 3),
            muted = audio.IsMuted
        }));
    }

    public ValueTask<JsonElement> AddTextAsync(
        AgentToolContext context,
        double startSeconds,
        double durationSeconds,
        string text,
        bool subtitle,
        double fontSize,
        double x,
        double y,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var textStart = TimelineTime.FromSeconds(startSeconds);
        var textEnd = textStart + TimelineTime.FromSeconds(durationSeconds);
        if (textStart < TimelineTime.Zero || textEnd > before.Duration)
        {
            throw new AgentToolRejectedException(
                "text_outside_draft",
                "Text must stay inside the current Agent Draft duration.");
        }
        var track = before.Tracks
            .Where(item => item.Kind == TrackKind.Text)
            .OrderBy(item => item.Index)
            .FirstOrDefault()
            ?? throw new AgentToolRejectedException(
                "text_track_not_found",
                "Agent Draft has no text track.");

        var clip = new TextClip(
            Guid.NewGuid(),
            track.Id,
            TimelineTime.FromSeconds(startSeconds),
            TimelineTime.FromSeconds(durationSeconds),
            text,
            new TextStyle(
                FontSize: fontSize,
                X: x,
                Y: y,
                IsSubtitle: subtitle));

        Apply(
            context,
            "add_text",
            reason,
            "Agent: добавить текст",
            new AddTextClipsCommand([clip]));

        var after = RequireDraft(context);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            text_clip_id = clip.Id,
            track_id = track.Id,
            start_seconds = Round(startSeconds),
            duration_seconds = Round(durationSeconds),
            subtitle,
            text
        }));
    }

    public ValueTask<JsonElement> AddTransitionAsync(
        AgentToolContext context,
        Guid fromClipId,
        string kind,
        double durationSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var fromClip = before.MediaClips.FirstOrDefault(item => item.Id == fromClipId)
            ?? throw new AgentToolRejectedException(
                "clip_not_found",
                $"Draft clip '{fromClipId}' was not found.");
        var track = before.Tracks.FirstOrDefault(item => item.Id == fromClip.TrackId)
            ?? throw new AgentToolRejectedException(
                "track_not_found",
                "Transition track was not found.");

        var transitionKind = ParseTransitionKind(kind);
        if (track.Kind == TrackKind.Audio &&
            transitionKind != TransitionKind.ConstantPowerAudio)
        {
            throw new AgentToolRejectedException(
                "transition_kind_mismatch",
                "Audio tracks support only constant_power_audio.");
        }

        if (track.Kind == TrackKind.Visual &&
            transitionKind == TransitionKind.ConstantPowerAudio)
        {
            throw new AgentToolRejectedException(
                "transition_kind_mismatch",
                "constant_power_audio cannot be placed on a visual track.");
        }

        var transitionId = Guid.NewGuid();
        var companionId = track.Kind == TrackKind.Visual
            ? Guid.NewGuid()
            : (Guid?)null;

        Apply(
            context,
            "add_transition",
            reason,
            $"Agent: добавить переход после клипа {fromClipId}",
            new CreateTransitionAtEditCommand(
                transitionId,
                fromClipId,
                transitionKind,
                TimelineTime.FromSeconds(durationSeconds),
                companionId));

        var after = RequireDraft(context);
        var transition = after.Transitions.FirstOrDefault(item => item.Id == transitionId)
            ?? throw new AgentToolRejectedException(
                "transition_missing_after_edit",
                "Transition was not created.");

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            transition_id = transition.Id,
            kind = kind,
            from_clip_id = transition.FromClipId,
            to_clip_id = transition.ToClipId,
            start_seconds = Round(transition.Start.TotalSeconds),
            duration_seconds = Round(transition.Duration.TotalSeconds)
        }));
    }

    public ValueTask<JsonElement> InspectEditLogAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);

        AgentAppliedEdit[] edits;
        lock (_gate)
        {
            edits = _editLog.ToArray();
        }

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            task_id = context.TaskId,
            source_sequence_id = context.SourceSequenceId,
            draft_sequence_id = draft.Id,
            draft_revision = draft.Revision,
            draft_duration_seconds = Round(draft.Duration.TotalSeconds),
            edit_count = edits.Length,
            edits
        }));
    }

    private SequenceState RequireDraft(AgentToolContext context)
    {
        lock (_gate)
        {
            if (_taskId != context.TaskId)
            {
                throw new AgentToolRejectedException(
                    "editing_backend_task_mismatch",
                    "Editing backend is not initialized for this agent task.");
            }
        }

        if (context.DraftSequenceId is not { } draftId ||
            draftId == context.SourceSequenceId)
        {
            throw new AgentToolRejectedException(
                "draft_required",
                "Editing requires a separate Agent Draft.");
        }

        var state = stateProvider();
        if (state.Id != context.ProjectId)
        {
            throw new AgentToolRejectedException(
                "project_changed",
                "The active project changed while the agent was working.");
        }

        if (state.ActiveSequenceId != draftId)
        {
            throw new AgentToolRejectedException(
                "draft_not_active",
                "The Agent Draft is no longer the active timeline.");
        }

        var draft = state.FindSequence(draftId)
            ?? throw new AgentToolRejectedException(
                "draft_not_found",
                "Agent Draft sequence was not found.");

        if (draft.Status != SequenceStatus.Draft ||
            draft.ParentSequenceId != context.SourceSequenceId)
        {
            throw new AgentToolRejectedException(
                "invalid_agent_draft",
                "The active sequence is not the protected draft for this task.");
        }

        return draft;
    }

    private void Apply(
        AgentToolContext context,
        string toolName,
        string reason,
        string description,
        IEditCommand command)
    {
        RequireDraft(context);

        if (!commandApplier(description, command))
        {
            throw new AgentToolRejectedException(
                "no_change",
                "The requested edit did not change the Agent Draft.");
        }

        var draft = RequireDraft(context);
        lock (_gate)
        {
            _editLog.Add(new AgentAppliedEdit(
                _nextEditSequence++,
                toolName,
                reason.Trim(),
                description,
                DateTimeOffset.UtcNow));
        }

        if (draft.Id != context.DraftSequenceId)
        {
            throw new AgentToolRejectedException(
                "draft_changed",
                "Agent Draft identity changed during the edit.");
        }
    }

    private static TransitionKind ParseTransitionKind(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "cross_dissolve" => TransitionKind.CrossDissolve,
            "dip_to_black" => TransitionKind.DipToBlack,
            "dip_to_white" => TransitionKind.DipToWhite,
            "wipe" => TransitionKind.Wipe,
            "slide" => TransitionKind.Slide,
            "constant_power_audio" => TransitionKind.ConstantPowerAudio,
            _ => throw new AgentToolRejectedException(
                "unknown_transition_kind",
                $"Unknown transition kind '{value}'.")
        };

    private static object ClipTiming(MediaClip clip)
        => new
        {
            start_seconds = Round(clip.Start.TotalSeconds),
            end_seconds = Round(clip.End.TotalSeconds),
            duration_seconds = Round(clip.Duration.TotalSeconds),
            source_in_seconds = Round(clip.SourceIn.TotalSeconds)
        };

    private static double Round(double value)
        => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
