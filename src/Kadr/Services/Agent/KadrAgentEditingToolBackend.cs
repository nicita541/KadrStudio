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

    public ValueTask<JsonElement> SplitClipsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> clipIds,
        double positionSeconds,
        bool includeLinked,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var position = TimelineTime.FromSeconds(positionSeconds);
        var requested = clipIds.Distinct().Select(id =>
            before.MediaClips.FirstOrDefault(clip => clip.Id == id)
            ?? throw new AgentToolRejectedException("clip_not_found", $"Draft clip '{id}' was not found.")).ToArray();
        if (requested.Any(clip => position <= clip.Start || position >= clip.End))
            throw new AgentToolRejectedException("split_outside_clip", "The split position must be inside every requested clip.");

        var representatives = includeLinked
            ? requested.GroupBy(clip => clip.LinkGroupId ?? clip.Id).Select(group => group.First()).ToArray()
            : requested;
        var commands = representatives.Select(clip =>
            (IEditCommand)new SplitSelectedMediaClipCommand(clip.Id, position, IncludeLinked: includeLinked)).ToArray();
        Apply(
            context,
            "split_clips",
            reason,
            $"Agent: разрезать {requested.Length} выбранных клип(ов) в {positionSeconds:0.###} с",
            new EditBatchCommand("Разрезать выбранные клипы", commands));
        var after = RequireDraft(context);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            requested_clip_ids = requested.Select(clip => clip.Id).ToArray(),
            position_seconds = Round(positionSeconds),
            include_linked = includeLinked,
            media_clip_count_before = before.MediaClips.Length,
            media_clip_count_after = after.MediaClips.Length
        }));
    }

    public ValueTask<JsonElement> SetClipVideoAsync(
        AgentToolContext context,
        Guid clipId,
        VideoParameters parameters,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var clip = before.MediaClips.FirstOrDefault(item => item.Id == clipId)
                   ?? throw new AgentToolRejectedException("clip_not_found", $"Draft clip '{clipId}' was not found.");
        var track = before.Tracks.First(item => item.Id == clip.TrackId);
        if (track.Kind != TrackKind.Visual)
            throw new AgentToolRejectedException("video_track_required", "set_clip_video requires a visual clip.");
        Apply(context, "set_clip_video", reason, $"Agent: изменить параметры видео {clipId}",
            new UpsertMediaClipCommand(clip with { Video = parameters }));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            clip_id = clipId,
            video = parameters
        }));
    }

    public ValueTask<JsonElement> SetClipAudioAsync(
        AgentToolContext context,
        Guid clipId,
        AudioParameters parameters,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = RequireDraft(context);
        var clip = before.MediaClips.FirstOrDefault(item => item.Id == clipId)
                   ?? throw new AgentToolRejectedException("clip_not_found", $"Draft clip '{clipId}' was not found.");
        var track = before.Tracks.First(item => item.Id == clip.TrackId);
        if (track.Kind != TrackKind.Audio)
            throw new AgentToolRejectedException("audio_track_required", "set_clip_audio requires an audio clip.");
        var normalized = parameters with
        {
            Volume = Math.Clamp(parameters.Volume, 0, 4),
            Pan = Math.Clamp(parameters.Pan, -1, 1),
            FadeIn = parameters.FadeIn <= clip.Duration ? parameters.FadeIn : clip.Duration,
            FadeOut = parameters.FadeOut <= clip.Duration ? parameters.FadeOut : clip.Duration
        };
        Apply(context, "set_clip_audio", reason, $"Agent: изменить параметры аудио {clipId}",
            new UpsertMediaClipCommand(clip with { Audio = normalized }));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            clip_id = clipId,
            audio = normalized
        }));
    }

    public ValueTask<JsonElement> UpdateClipVideoAsync(
        AgentToolContext context,
        Guid clipId,
        AgentVideoParametersPatch patch,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var clip = draft.MediaClips.FirstOrDefault(item => item.Id == clipId)
                   ?? throw new AgentToolRejectedException("clip_not_found", $"Draft clip '{clipId}' was not found.");
        var track = draft.Tracks.First(item => item.Id == clip.TrackId);
        if (track.Kind != TrackKind.Visual)
            throw new AgentToolRejectedException("video_track_required", "set_clip_video requires a visual clip.");
        var current = clip.Video ?? new VideoParameters();
        var updated = current with
        {
            Brightness = patch.Brightness ?? current.Brightness,
            Contrast = patch.Contrast ?? current.Contrast,
            Saturation = patch.Saturation ?? current.Saturation,
            Temperature = patch.Temperature ?? current.Temperature,
            PositionX = patch.PositionX ?? current.PositionX,
            PositionY = patch.PositionY ?? current.PositionY,
            ScaleX = patch.ScaleX ?? current.ScaleX,
            ScaleY = patch.ScaleY ?? current.ScaleY,
            Rotation = patch.Rotation ?? current.Rotation,
            CropLeft = patch.CropLeft ?? current.CropLeft,
            CropTop = patch.CropTop ?? current.CropTop,
            CropRight = patch.CropRight ?? current.CropRight,
            CropBottom = patch.CropBottom ?? current.CropBottom,
            Opacity = patch.Opacity ?? current.Opacity
        };
        Apply(context, "set_clip_video", reason, $"Agent: изменить параметры видео {clipId}",
            new UpsertMediaClipCommand(clip with { Video = updated }));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            clip_id = clipId,
            video = updated
        }));
    }

    public ValueTask<JsonElement> UpdateClipAudioAsync(
        AgentToolContext context,
        Guid clipId,
        AgentAudioParametersPatch patch,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var clip = draft.MediaClips.FirstOrDefault(item => item.Id == clipId)
                   ?? throw new AgentToolRejectedException("clip_not_found", $"Draft clip '{clipId}' was not found.");
        var track = draft.Tracks.First(item => item.Id == clip.TrackId);
        if (track.Kind != TrackKind.Audio)
            throw new AgentToolRejectedException("audio_track_required", "set_clip_audio requires an audio clip.");
        var current = clip.Audio ?? new AudioParameters();
        var updated = current with
        {
            Volume = patch.Volume ?? current.Volume,
            IsMuted = patch.Muted ?? current.IsMuted,
            Pan = patch.Pan ?? current.Pan,
            FadeIn = patch.FadeInSeconds is { } fadeIn ? TimelineTime.FromSeconds(fadeIn) : current.FadeIn,
            FadeOut = patch.FadeOutSeconds is { } fadeOut ? TimelineTime.FromSeconds(fadeOut) : current.FadeOut,
            Bass = patch.Bass ?? current.Bass,
            Mid = patch.Mid ?? current.Mid,
            Treble = patch.Treble ?? current.Treble
        };
        updated = updated with
        {
            FadeIn = updated.FadeIn <= clip.Duration ? updated.FadeIn : clip.Duration,
            FadeOut = updated.FadeOut <= clip.Duration ? updated.FadeOut : clip.Duration
        };
        Apply(context, "set_clip_audio", reason, $"Agent: изменить параметры аудио {clipId}",
            new UpsertMediaClipCommand(clip with { Audio = updated }));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            clip_id = clipId,
            audio = updated
        }));
    }

    public ValueTask<JsonElement> InsertSourceRangeAsync(
        AgentToolContext context,
        Guid sourceId,
        Guid targetTrackId,
        double sourceStartSeconds,
        double sourceEndSeconds,
        double timelineStartSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var state = stateProvider();
        if (!state.Sources.TryGetValue(sourceId, out var source))
            throw new AgentToolRejectedException("media_not_found", $"Media source '{sourceId}' was not found.");
        var track = draft.Tracks.FirstOrDefault(item => item.Id == targetTrackId)
                    ?? throw new AgentToolRejectedException("track_not_found", $"Draft track '{targetTrackId}' was not found.");
        if (track.Kind == TrackKind.Text ||
            track.Kind == TrackKind.Audio && !source.HasAudio ||
            track.Kind == TrackKind.Visual && source.Kind == MediaKind.Audio)
            throw new AgentToolRejectedException("track_media_mismatch", "The source is not compatible with the target track.");
        var sourceStart = TimelineTime.FromSeconds(sourceStartSeconds);
        var duration = TimelineTime.FromSeconds(sourceEndSeconds - sourceStartSeconds);
        if (sourceStart < TimelineTime.Zero || sourceStart + duration > source.Duration)
            throw new AgentToolRejectedException("source_range_out_of_bounds", "The requested source range is outside the media duration.");
        var start = TimelineTime.FromSeconds(timelineStartSeconds);
        var end = start + duration;
        if (draft.MediaClips.Any(clip => clip.TrackId == targetTrackId && clip.Start < end && clip.End > start))
            throw new AgentToolRejectedException("timeline_overlap", "The inserted range would overlap another clip on the target track.");
        var clip = new MediaClip(
            Guid.NewGuid(),
            sourceId,
            targetTrackId,
            start,
            sourceStart,
            duration,
            Audio: track.Kind == TrackKind.Audio ? new AudioParameters() : null);
        Apply(context, "insert_source_range", reason, "Agent: вставить диапазон исходника",
            new AddMediaClipsCommand([clip]));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            clip_id = clip.Id,
            source_id = sourceId,
            target_track_id = targetTrackId,
            source_start_seconds = Round(sourceStartSeconds),
            source_end_seconds = Round(sourceEndSeconds),
            timeline_start_seconds = Round(timelineStartSeconds)
        }));
    }

    public ValueTask<JsonElement> UnlinkClipsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> clipIds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var representatives = clipIds.Distinct()
            .Select(id => draft.MediaClips.FirstOrDefault(clip => clip.Id == id)
                          ?? throw new AgentToolRejectedException("clip_not_found", $"Draft clip '{id}' was not found."))
            .Where(clip => clip.LinkGroupId.HasValue)
            .GroupBy(clip => clip.LinkGroupId!.Value)
            .Select(group => group.First().Id)
            .ToArray();
        if (representatives.Length == 0)
            throw new AgentToolRejectedException("linked_clip_required", "No requested clip belongs to a link group.");
        Apply(context, "unlink_clips", reason, "Agent: разорвать связи клипов",
            new EditBatchCommand(
                "Разорвать связи клипов",
                representatives.Select(id => (IEditCommand)new UnlinkMediaClipCommand(id)).ToArray()));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            clip_ids = clipIds.Distinct().ToArray()
        }));
    }

    public ValueTask<JsonElement> DeleteTimelineObjectsAsync(
        AgentToolContext context,
        IReadOnlyCollection<Guid> textClipIds,
        IReadOnlyCollection<Guid> transitionIds,
        IReadOnlyCollection<Guid> markerIds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var commands = new List<IEditCommand>();
        if (textClipIds.Count > 0) commands.Add(new DeleteTextClipsCommand(textClipIds.ToHashSet()));
        if (transitionIds.Count > 0) commands.Add(new DeleteTransitionsCommand(transitionIds.ToHashSet()));
        if (markerIds.Count > 0)
            commands.Add(new ReplaceMarkersCommand(draft.Markers.Where(marker => !markerIds.Contains(marker.Id)).ToArray()));
        if (commands.Count == 0)
            throw new AgentToolRejectedException("objects_required", "At least one timeline object id is required.");
        Apply(context, "delete_timeline_objects", reason, "Agent: удалить объекты таймлайна",
            new EditBatchCommand("Удалить объекты таймлайна", commands));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            text_clip_ids = textClipIds,
            transition_ids = transitionIds,
            marker_ids = markerIds
        }));
    }

    public ValueTask<JsonElement> AddMarkerAsync(
        AgentToolContext context,
        double startSeconds,
        double durationSeconds,
        string title,
        string description,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var marker = new TimelineMarker(
            Guid.NewGuid(), MarkerKind.Note, TimelineTime.FromSeconds(startSeconds),
            TimelineTime.FromSeconds(durationSeconds), title.Trim(), description.Trim());
        if (marker.Start < TimelineTime.Zero || marker.End > draft.Duration)
            throw new AgentToolRejectedException("marker_out_of_bounds", "Marker must stay inside the Agent Draft.");
        Apply(context, "add_marker", reason, "Agent: добавить маркер",
            new ReplaceMarkersCommand(draft.Markers.Add(marker)));
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            marker_id = marker.Id,
            start_seconds = startSeconds,
            duration_seconds = durationSeconds,
            title = marker.Title
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

    public ValueTask<JsonElement> UpdateTextAsync(
        AgentToolContext context,
        Guid textClipId,
        double? startSeconds,
        double? durationSeconds,
        string? text,
        bool? subtitle,
        double? fontSize,
        double? x,
        double? y,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var current = draft.TextClips.FirstOrDefault(item => item.Id == textClipId)
            ?? throw new AgentToolRejectedException(
                "text_clip_not_found",
                $"Draft text clip '{textClipId}' was not found.");

        var updated = current with
        {
            Start = startSeconds is { } start
                ? TimelineTime.FromSeconds(start)
                : current.Start,
            Duration = durationSeconds is { } duration
                ? TimelineTime.FromSeconds(duration)
                : current.Duration,
            Text = text ?? current.Text,
            Style = current.Style with
            {
                IsSubtitle = subtitle ?? current.Style.IsSubtitle,
                FontSize = fontSize ?? current.Style.FontSize,
                X = x ?? current.Style.X,
                Y = y ?? current.Style.Y
            }
        };

        if (updated.Start < TimelineTime.Zero ||
            updated.Duration <= TimelineTime.Zero ||
            updated.End > draft.Duration)
        {
            throw new AgentToolRejectedException(
                "text_outside_draft",
                "Updated text must have a positive duration and stay inside the Agent Draft.");
        }

        Apply(
            context,
            "update_text",
            reason,
            $"Agent: изменить текст {textClipId}",
            new UpsertTextClipCommand(updated));

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            text_clip_id = updated.Id,
            start_seconds = Round(updated.Start.TotalSeconds),
            duration_seconds = Round(updated.Duration.TotalSeconds),
            text = updated.Text,
            subtitle = updated.Style.IsSubtitle,
            font_size = updated.Style.FontSize,
            x = updated.Style.X,
            y = updated.Style.Y
        }));
    }

    public ValueTask<JsonElement> UpdateMarkerAsync(
        AgentToolContext context,
        Guid markerId,
        double? startSeconds,
        double? durationSeconds,
        string? title,
        string? description,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var current = draft.Markers.FirstOrDefault(item => item.Id == markerId)
            ?? throw new AgentToolRejectedException(
                "marker_not_found",
                $"Draft marker '{markerId}' was not found.");
        if (current.Kind != MarkerKind.Note)
        {
            throw new AgentToolRejectedException(
                "neutral_marker_required",
                "Only neutral note markers can be updated by the agent.");
        }

        var updated = current with
        {
            Start = startSeconds is { } start
                ? TimelineTime.FromSeconds(start)
                : current.Start,
            Duration = durationSeconds is { } duration
                ? TimelineTime.FromSeconds(duration)
                : current.Duration,
            Title = title ?? current.Title,
            Description = description ?? current.Description
        };
        if (updated.Start < TimelineTime.Zero ||
            updated.Duration < TimelineTime.Zero ||
            updated.End > draft.Duration)
        {
            throw new AgentToolRejectedException(
                "marker_out_of_bounds",
                "Updated marker must stay inside the Agent Draft.");
        }

        Apply(
            context,
            "update_marker",
            reason,
            $"Agent: изменить маркер {markerId}",
            new ReplaceMarkersCommand(
                draft.Markers.Select(item => item.Id == markerId ? updated : item).ToArray()));

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = RequireDraft(context).Id,
            marker_id = updated.Id,
            start_seconds = Round(updated.Start.TotalSeconds),
            duration_seconds = Round(updated.Duration.TotalSeconds),
            title = updated.Title,
            description = updated.Description
        }));
    }

    public ValueTask<JsonElement> UpdateTransitionAsync(
        AgentToolContext context,
        Guid transitionId,
        string? kind,
        double? durationSeconds,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = RequireDraft(context);
        var current = draft.Transitions.FirstOrDefault(item => item.Id == transitionId)
            ?? throw new AgentToolRejectedException(
                "transition_not_found",
                $"Draft transition '{transitionId}' was not found.");
        var from = draft.MediaClips.FirstOrDefault(item => item.Id == current.FromClipId)
            ?? throw new AgentToolRejectedException(
                "transition_clip_not_found",
                "Transition outgoing clip was not found in the Agent Draft.");
        var to = draft.MediaClips.FirstOrDefault(item => item.Id == current.ToClipId)
            ?? throw new AgentToolRejectedException(
                "transition_clip_not_found",
                "Transition incoming clip was not found in the Agent Draft.");
        if (from.TrackId != current.TrackId || to.TrackId != current.TrackId || from.End != to.Start)
        {
            throw new AgentToolRejectedException(
                "transition_boundary_changed",
                "Transition clips are no longer adjacent on the same track.");
        }

        var updatedKind = kind is null ? current.Kind : ParseTransitionKind(kind);
        var updatedDuration = durationSeconds is { } duration
            ? TimelineTime.FromSeconds(duration)
            : current.Duration;
        if (updatedDuration <= TimelineTime.Zero)
        {
            throw new AgentToolRejectedException(
                "invalid_transition_duration",
                "Transition duration must be positive.");
        }

        var beforeHalf = new TimelineTime(updatedDuration.Ticks / 2);
        var updated = current with
        {
            Kind = updatedKind,
            Start = from.End - beforeHalf,
            Duration = updatedDuration
        };

        Apply(
            context,
            "update_transition",
            reason,
            $"Agent: изменить переход {transitionId}",
            new UpsertTransitionCommand(updated));

        var after = RequireDraft(context);
        var normalized = after.Transitions.Single(item => item.Id == transitionId);
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            sequence_id = after.Id,
            transition_id = normalized.Id,
            kind = FormatTransitionKind(normalized.Kind),
            from_clip_id = normalized.FromClipId,
            to_clip_id = normalized.ToClipId,
            start_seconds = Round(normalized.Start.TotalSeconds),
            duration_seconds = Round(normalized.Duration.TotalSeconds)
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

    private static string FormatTransitionKind(TransitionKind value)
        => value switch
        {
            TransitionKind.CrossDissolve => "cross_dissolve",
            TransitionKind.DipToBlack => "dip_to_black",
            TransitionKind.DipToWhite => "dip_to_white",
            TransitionKind.Wipe => "wipe",
            TransitionKind.Slide => "slide",
            TransitionKind.ConstantPowerAudio => "constant_power_audio",
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
