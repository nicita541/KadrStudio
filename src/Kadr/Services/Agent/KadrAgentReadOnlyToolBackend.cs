using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Real Kadr adapter for the generic Stage 3 read-only tool API.
/// Reads immutable project snapshots and delegates only focused media ranges
/// to <see cref="IAgentMediaRangeInspector"/>.
/// </summary>
public sealed class KadrAgentReadOnlyToolBackend(
    Func<ProjectState> stateProvider,
    IAgentMediaRangeInspector rangeInspector,
    Func<AgentEditorContextSnapshot>? editorContextProvider = null,
    RecurringSectionFingerprintService? recurringSectionFinder = null) : IAgentReadOnlyToolBackend
{
    private const int MaximumProjectSources = 80;
    private const int MaximumProjectSequences = 40;
    private const int MaximumTimelineClips = 100;
    private const int MaximumTimelineTextClips = 50;
    private const int MaximumTimelineMarkers = 80;
    private const int MaximumTimelineTransitions = 80;
    private const int MaximumMediaUsages = 80;
    private const int MaximumAnalysisReferences = 24;
    private const int MaximumSequenceAnalysisSlices = 6;
    private const int MaximumRangeClips = 120;
    private const int MaximumRangeTextClips = 80;
    private const int MaximumRangeMarkers = 80;
    private const int MaximumRangeTransitions = 80;
    private const int MaximumQueryCharacters = 2_000;
    private const double MaximumSequenceVisionAnalysisSeconds = 900;
    private const double MaximumSequenceOtherAnalysisSeconds = 1_800;

    public ValueTask<JsonElement> InspectEditorContextAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var active = RequireSequence(
            project,
            project.ActiveSequenceId ?? context.SourceSequenceId);
        var snapshot = editorContextProvider?.Invoke()
                       ?? new AgentEditorContextSnapshot(
                           active.Id,
                           active.Revision,
                           0,
                           null,
                           active.InPoint?.TotalSeconds,
                           active.OutPoint?.TotalSeconds);

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "editor_context",
            project_revision = project.Revision,
            active_sequence_id = snapshot.ActiveSequenceId,
            active_sequence_revision = snapshot.ActiveSequenceRevision,
            playhead_seconds = RoundIntegrity(snapshot.PlayheadSeconds),
            selected_clip_id = snapshot.SelectedClipId,
            in_point_seconds = snapshot.InPointSeconds is { } inPoint ? RoundIntegrity(inPoint) : (double?)null,
            out_point_seconds = snapshot.OutPointSeconds is { } outPoint ? RoundIntegrity(outPoint) : (double?)null,
            task_source_sequence_id = context.SourceSequenceId,
            task_draft_sequence_id = context.DraftSequenceId,
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = snapshot.SelectedClipId is null
                ? "inspect_timeline"
                : "inspect_timeline or inspect_range around the selected clip"
        }));
    }

    public ValueTask<JsonElement> InspectProjectAsync(
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);

        var sources = project.Sources.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Take(MaximumProjectSources)
            .Select(source => new
            {
                id = source.Id,
                name = Compact(source.Name, 300),
                kind = source.Kind.ToString().ToLowerInvariant(),
                duration_seconds = Round(source.Duration.TotalSeconds),
                has_audio = source.HasAudio,
                online_state = source.OnlineState.ToString().ToLowerInvariant(),
                width = source.Width,
                height = source.Height
            })
            .ToArray();

        var sequences = project.Sequences
            .OrderBy(item => item.Status)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .Take(MaximumProjectSequences)
            .Select(sequence => new
            {
                id = sequence.Id,
                name = Compact(sequence.Name, 300),
                status = sequence.Status.ToString().ToLowerInvariant(),
                target_format = sequence.TargetFormat.ToString().ToLowerInvariant(),
                duration_seconds = Round(sequence.Duration.TotalSeconds),
                revision = sequence.Revision,
                parent_sequence_id = sequence.ParentSequenceId,
                montage_plan_id = sequence.MontagePlanId,
                is_active = sequence.Id == project.ActiveSequenceId,
                is_task_source = sequence.Id == context.SourceSequenceId,
                is_task_draft = sequence.Id == context.DraftSequenceId
            })
            .ToArray();

        var data = AgentToolJson.ToElement(new
        {
            channel = "project",
            project_id = project.Id,
            name = project.Name,
            revision = project.Revision,
            project_revision = project.Revision,
            target = new { project_id = project.Id },
            active_sequence_id = project.ActiveSequenceId,
            task_source_sequence_id = context.SourceSequenceId,
            task_draft_sequence_id = context.DraftSequenceId,
            canvas = new
            {
                width = project.Sequence.CanvasWidth,
                height = project.Sequence.CanvasHeight,
                frame_rate = project.Sequence.FrameRate.ToString(),
                audio_sample_rate = project.Sequence.AudioSampleRate
            },
            source_count = project.Sources.Count,
            sources_truncated = project.Sources.Count > MaximumProjectSources,
            sources,
            sequence_count = project.Sequences.Length,
            sequences_truncated = project.Sequences.Length > MaximumProjectSequences,
            sequences,
            source_annotation_count = project.SourceAnnotations.Length,
            analysis_reference_count = project.AnalysisReferences.Length,
            montage_plan_count = project.MontagePlans.Length,
            truncated = project.Sources.Count > MaximumProjectSources ||
                        project.Sequences.Length > MaximumProjectSequences,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Use inspect_timeline or search_timeline on the relevant sequence."
        });

        return ValueTask.FromResult(data);
    }

    public ValueTask<JsonElement> InspectTimelineAsync(
        AgentToolContext context,
        Guid sequenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var sequence = RequireSequence(project, sequenceId);

        var tracks = sequence.Tracks
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Index)
            .Select(track => new
            {
                id = track.Id,
                kind = track.Kind.ToString().ToLowerInvariant(),
                index = track.Index,
                name = Compact(track.Name, 200),
                muted = track.IsMuted,
                locked = track.IsLocked,
                visible = track.IsVisible
            })
            .ToArray();

        var trackById = sequence.Tracks.ToDictionary(item => item.Id);
        var clips = sequence.MediaClips
            .OrderBy(item => item.Start)
            .ThenBy(item => item.TrackId)
            .ThenBy(item => item.Id)
            .Take(MaximumTimelineClips)
            .Select(clip =>
            {
                trackById.TryGetValue(clip.TrackId, out var track);
                project.Sources.TryGetValue(clip.SourceId, out var source);
                return new
                {
                    id = clip.Id,
                    source_id = clip.SourceId,
                    source_name = Compact(source?.Name, 300),
                    track_id = clip.TrackId,
                    track_name = track?.Name ?? string.Empty,
                    track_kind = track?.Kind.ToString().ToLowerInvariant() ?? "unknown",
                    start_seconds = Round(clip.Start.TotalSeconds),
                    end_seconds = Round(clip.End.TotalSeconds),
                    duration_seconds = Round(clip.Duration.TotalSeconds),
                    source_in_seconds = Round(clip.SourceIn.TotalSeconds),
                    source_out_seconds = Round((clip.SourceIn + clip.Duration).TotalSeconds),
                    link_group_id = clip.LinkGroupId
                };
            })
            .ToArray();

        var textClips = sequence.TextClips
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .Take(MaximumTimelineTextClips)
            .Select(clip => new
            {
                id = clip.Id,
                track_id = clip.TrackId,
                start_seconds = Round(clip.Start.TotalSeconds),
                end_seconds = Round(clip.End.TotalSeconds),
                text = Compact(clip.Text, 1_000),
                is_subtitle = clip.Style.IsSubtitle
            })
            .ToArray();

        var markers = sequence.Markers
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .Take(MaximumTimelineMarkers)
            .Select(ToMarkerObservation)
            .ToArray();

        var transitions = sequence.Transitions
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .Take(MaximumTimelineTransitions)
            .Select(transition => new
            {
                id = transition.Id,
                kind = transition.Kind.ToString().ToLowerInvariant(),
                track_id = transition.TrackId,
                from_clip_id = transition.FromClipId,
                to_clip_id = transition.ToClipId,
                start_seconds = Round(transition.Start.TotalSeconds),
                duration_seconds = Round(transition.Duration.TotalSeconds)
            })
            .ToArray();

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "timeline",
            project_revision = project.Revision,
            sequence_id = sequence.Id,
            name = Compact(sequence.Name, 300),
            status = sequence.Status.ToString().ToLowerInvariant(),
            revision = sequence.Revision,
            sequence_revision = sequence.Revision,
            target = new { sequence_id = sequence.Id },
            parent_sequence_id = sequence.ParentSequenceId,
            is_active = sequence.Id == project.ActiveSequenceId,
            is_task_source = sequence.Id == context.SourceSequenceId,
            is_task_draft = sequence.Id == context.DraftSequenceId,
            duration_seconds = Round(sequence.Duration.TotalSeconds),
            track_count = sequence.Tracks.Length,
            tracks,
            media_clip_count = sequence.MediaClips.Length,
            media_clips_truncated = sequence.MediaClips.Length > MaximumTimelineClips,
            media_clips = clips,
            text_clip_count = sequence.TextClips.Length,
            text_clips_truncated = sequence.TextClips.Length > MaximumTimelineTextClips,
            text_clips = textClips,
            marker_count = sequence.Markers.Length,
            markers_truncated = sequence.Markers.Length > MaximumTimelineMarkers,
            markers,
            transition_count = sequence.Transitions.Length,
            transitions_truncated = sequence.Transitions.Length > MaximumTimelineTransitions,
            transitions,
            truncated = sequence.MediaClips.Length > MaximumTimelineClips ||
                        sequence.TextClips.Length > MaximumTimelineTextClips ||
                        sequence.Markers.Length > MaximumTimelineMarkers ||
                        sequence.Transitions.Length > MaximumTimelineTransitions,
            artifact_reference = (string?)null,
            recommended_next_inspection = sequence.MediaClips.Length > MaximumTimelineClips
                ? "Use search_timeline with filters and cursor to inspect all clips."
                : "Use inspect_objects for full parameters or inspect_range for content evidence."
        }));
    }

    public ValueTask<JsonElement> InspectTimelineIntegrityAsync(
        AgentToolContext context,
        Guid sequenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var sequence = RequireSequence(project, sequenceId);
        var trackById = sequence.Tracks.ToDictionary(track => track.Id);

        var gaps = new List<object>();
        var overlaps = new List<object>();
        var junctions = new List<object>();
        foreach (var trackGroup in sequence.MediaClips
                     .GroupBy(clip => clip.TrackId)
                     .OrderBy(group => trackById.TryGetValue(group.Key, out var track) ? track.Index : int.MaxValue))
        {
            var ordered = trackGroup.OrderBy(clip => clip.Start).ThenBy(clip => clip.Id).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var left = ordered[index - 1];
                var right = ordered[index];
                var delta = right.Start - left.End;
                var observation = new
                {
                    track_id = trackGroup.Key,
                    track_name = trackById.TryGetValue(trackGroup.Key, out var track) ? track.Name : string.Empty,
                    left_clip_id = left.Id,
                    right_clip_id = right.Id,
                    left_end_seconds = RoundIntegrity(left.End.TotalSeconds),
                    right_start_seconds = RoundIntegrity(right.Start.TotalSeconds),
                    delta_seconds = RoundIntegrity(delta.TotalSeconds),
                    delta_frames = Math.Round(delta.TotalSeconds * project.FrameRate.FramesPerSecond, 3)
                };
                junctions.Add(observation);
                if (delta > TimelineTime.Zero) gaps.Add(observation);
                if (delta < TimelineTime.Zero) overlaps.Add(observation);
            }
        }

        var linkIssues = sequence.MediaClips
            .Where(clip => clip.LinkGroupId is not null)
            .GroupBy(clip => clip.LinkGroupId!.Value)
            .Select(group =>
            {
                var members = group.OrderBy(clip => clip.TrackId).ThenBy(clip => clip.Id).ToArray();
                var anchor = members[0];
                var startSpread = members.Max(clip => clip.Start.Ticks) - members.Min(clip => clip.Start.Ticks);
                var durationSpread = members.Max(clip => clip.Duration.Ticks) - members.Min(clip => clip.Duration.Ticks);
                return new
                {
                    link_group_id = group.Key,
                    member_count = members.Length,
                    clip_ids = members.Select(clip => clip.Id).ToArray(),
                    synchronized = members.Length >= 2 && startSpread == 0 && durationSpread == 0,
                    start_spread_seconds = RoundIntegrity(startSpread / (double)TimelineTime.TicksPerSecond),
                    duration_spread_seconds = RoundIntegrity(durationSpread / (double)TimelineTime.TicksPerSecond),
                    source_in_spread_seconds = RoundIntegrity((members.Max(clip => clip.SourceIn.Ticks) - members.Min(clip => clip.SourceIn.Ticks)) / (double)TimelineTime.TicksPerSecond),
                    anchor_start_seconds = RoundIntegrity(anchor.Start.TotalSeconds)
                };
            })
            .Where(issue => !issue.synchronized)
            .ToArray();

        var sourceCoverage = sequence.MediaClips
            .GroupBy(clip => clip.SourceId)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                source_id = group.Key,
                ranges = MergeSourceRanges(group)
                    .Select(range => new
                    {
                        start_seconds = RoundIntegrity(range.Start.TotalSeconds),
                        end_seconds = RoundIntegrity(range.End.TotalSeconds),
                        duration_seconds = RoundIntegrity((range.End - range.Start).TotalSeconds)
                    })
                    .ToArray()
            })
            .ToArray();

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "integrity",
            project_revision = project.Revision,
            sequence_id = sequence.Id,
            sequence_revision = sequence.Revision,
            target = new { sequence_id = sequence.Id },
            sequence_name = Compact(sequence.Name, 300),
            frame_rate = project.FrameRate.ToString(),
            duration_seconds = Round(sequence.Duration.TotalSeconds),
            gap_count = gaps.Count,
            gaps,
            overlap_count = overlaps.Count,
            overlaps,
            link_issue_count = linkIssues.Length,
            link_issues = linkIssues,
            junctions,
            source_coverage = sourceCoverage,
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Inspect each relevant junction with inspect_boundary before making a content-dependent decision."
        }));
    }

    public ValueTask<JsonElement> CompareSequencesAsync(
        AgentToolContext context,
        Guid sourceSequenceId,
        Guid draftSequenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var source = RequireSequence(project, sourceSequenceId);
        var draft = RequireSequence(project, draftSequenceId);
        var sourceClips = source.MediaClips.ToDictionary(clip => clip.Id);
        var draftClips = draft.MediaClips.ToDictionary(clip => clip.Id);

        var removedClipIds = sourceClips.Keys.Except(draftClips.Keys).Order().ToArray();
        var addedClipIds = draftClips.Keys.Except(sourceClips.Keys).Order().ToArray();
        var changedClipIds = sourceClips.Keys.Intersect(draftClips.Keys)
            .Where(id => sourceClips[id] != draftClips[id])
            .Order()
            .ToArray();

        var sourceText = source.TextClips.ToDictionary(clip => clip.Id);
        var draftText = draft.TextClips.ToDictionary(clip => clip.Id);
        var sourceTransitions = source.Transitions.ToDictionary(item => item.Id);
        var draftTransitions = draft.Transitions.ToDictionary(item => item.Id);
        var sourceMarkers = source.Markers.ToDictionary(item => item.Id);
        var draftMarkers = draft.Markers.ToDictionary(item => item.Id);

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "sequence_diff",
            source_sequence_id = source.Id,
            source_revision = source.Revision,
            draft_sequence_id = draft.Id,
            draft_revision = draft.Revision,
            source_duration_seconds = RoundIntegrity(source.Duration.TotalSeconds),
            draft_duration_seconds = RoundIntegrity(draft.Duration.TotalSeconds),
            duration_delta_seconds = RoundIntegrity((draft.Duration - source.Duration).TotalSeconds),
            media_clips = new
            {
                source_count = sourceClips.Count,
                draft_count = draftClips.Count,
                removed_ids = removedClipIds,
                added_ids = addedClipIds,
                changed_ids = changedClipIds
            },
            text_clips = CollectionDiff(sourceText, draftText),
            transitions = CollectionDiff(sourceTransitions, draftTransitions),
            markers = CollectionDiff(sourceMarkers, draftMarkers),
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = "The deterministic verifier will inspect the edit log; inspect timeline integrity and each changed boundary."
        }));
    }

    private static object CollectionDiff<T>(
        IReadOnlyDictionary<Guid, T> source,
        IReadOnlyDictionary<Guid, T> draft)
        where T : notnull
    {
        var removed = source.Keys.Except(draft.Keys).Order().ToArray();
        var added = draft.Keys.Except(source.Keys).Order().ToArray();
        var changed = source.Keys.Intersect(draft.Keys)
            .Where(id => !EqualityComparer<T>.Default.Equals(source[id], draft[id]))
            .Order()
            .ToArray();
        return new
        {
            source_count = source.Count,
            draft_count = draft.Count,
            removed_ids = removed,
            added_ids = added,
            changed_ids = changed
        };
    }

    public async ValueTask<JsonElement> CompareMediaRangesAsync(
        AgentToolContext context,
        IReadOnlyList<AgentRecurringSectionSample> samples,
        double minimumSimilarity,
        CancellationToken cancellationToken)
    {
        if (recurringSectionFinder is null)
        {
            throw new AgentToolRejectedException(
                "recurrence_unavailable",
                "Recurring-section measurement is not configured.");
        }

        var project = Snapshot(context);
        var fingerprints = new Dictionary<string, RecurringSectionFingerprint>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.Sources.TryGetValue(sample.MediaId, out var source))
            {
                throw new AgentToolRejectedException(
                    "media_not_found",
                    $"Media source '{sample.MediaId}' was not found.");
            }
            if (sample.EndSeconds > source.Duration.TotalSeconds + 0.001)
            {
                throw new AgentToolRejectedException(
                    "range_out_of_bounds",
                    $"Sample '{sample.Id}' exceeds media duration.");
            }

            fingerprints[sample.Id] = await recurringSectionFinder.CreateAsync(
                source,
                new TimeRange(
                    TimelineTime.FromSeconds(sample.StartSeconds),
                    TimelineTime.FromSeconds(sample.EndSeconds - sample.StartSeconds)),
                cancellationToken).ConfigureAwait(false);
        }

        var matches = new List<object>();
        for (var leftIndex = 0; leftIndex < samples.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < samples.Count; rightIndex++)
            {
                var left = samples[leftIndex];
                var right = samples[rightIndex];
                var similarity = RecurringSectionFingerprintService.Similarity(
                    fingerprints[left.Id],
                    fingerprints[right.Id]);
                if (similarity + 1e-9 < minimumSimilarity)
                {
                    continue;
                }

                matches.Add(new
                {
                    left_sample_id = left.Id,
                    right_sample_id = right.Id,
                    similarity = Math.Round(similarity, 6),
                    left_range = new { media_id = left.MediaId, start_seconds = left.StartSeconds, end_seconds = left.EndSeconds },
                    right_range = new { media_id = right.MediaId, start_seconds = right.StartSeconds, end_seconds = right.EndSeconds }
                });
            }
        }

        return AgentToolJson.ToElement(new
        {
            channel = "recurrence",
            project_revision = project.Revision,
            minimum_similarity = minimumSimilarity,
            sample_count = samples.Count,
            match_count = matches.Count,
            matches,
            labels_assigned = false,
            editing_decision_made = false,
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Use inspect_boundary and inspect_range on matching ranges before proposing any edit."
        });
    }

    private static IReadOnlyList<(TimelineTime Start, TimelineTime End)> MergeSourceRanges(
        IEnumerable<MediaClip> clips)
    {
        var ordered = clips
            .Select(clip => (Start: clip.SourceIn, End: clip.SourceIn + clip.Duration))
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
        var merged = new List<(TimelineTime Start, TimelineTime End)>();
        foreach (var range in ordered)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
                continue;
            }

            if (range.End > merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, range.End);
            }
        }

        return merged;
    }

    public ValueTask<JsonElement> InspectObjectsAsync(
        AgentToolContext context,
        Guid sequenceId,
        IReadOnlyCollection<Guid> objectIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var sequence = RequireSequence(project, sequenceId);
        var requested = objectIds.Distinct().ToHashSet();
        var objects = new List<object>();

        objects.AddRange(sequence.Tracks.Where(item => requested.Contains(item.Id)).Select(track => new
        {
            object_type = "track",
            id = track.Id,
            kind = track.Kind.ToString().ToLowerInvariant(),
            track.Index,
            track.Name,
            muted = track.IsMuted,
            locked = track.IsLocked,
            visible = track.IsVisible
        }));
        objects.AddRange(sequence.MediaClips.Where(item => requested.Contains(item.Id)).Select(clip => new
        {
            object_type = "media_clip",
            id = clip.Id,
            source_id = clip.SourceId,
            track_id = clip.TrackId,
            start_seconds = RoundIntegrity(clip.Start.TotalSeconds),
            end_seconds = RoundIntegrity(clip.End.TotalSeconds),
            source_in_seconds = RoundIntegrity(clip.SourceIn.TotalSeconds),
            duration_seconds = RoundIntegrity(clip.Duration.TotalSeconds),
            link_group_id = clip.LinkGroupId,
            video = clip.Video,
            audio = clip.Audio
        }));
        objects.AddRange(sequence.TextClips.Where(item => requested.Contains(item.Id)).Select(clip => new
        {
            object_type = "text_clip",
            id = clip.Id,
            track_id = clip.TrackId,
            start_seconds = RoundIntegrity(clip.Start.TotalSeconds),
            duration_seconds = RoundIntegrity(clip.Duration.TotalSeconds),
            clip.Text,
            style = clip.Style
        }));
        objects.AddRange(sequence.Markers.Where(item => requested.Contains(item.Id)).Select(marker => new
        {
            object_type = "marker",
            id = marker.Id,
            kind = marker.Kind.ToString().ToLowerInvariant(),
            start_seconds = RoundIntegrity(marker.Start.TotalSeconds),
            duration_seconds = RoundIntegrity(marker.Duration.TotalSeconds),
            marker.Title,
            marker.Description,
            source_id = marker.SourceId,
            source_start_seconds = RoundIntegrity(marker.SourceStart.TotalSeconds),
            marker.Confidence,
            marker.Query
        }));
        objects.AddRange(sequence.Transitions.Where(item => requested.Contains(item.Id)).Select(transition => new
        {
            object_type = "transition",
            id = transition.Id,
            kind = transition.Kind.ToString().ToLowerInvariant(),
            track_id = transition.TrackId,
            from_clip_id = transition.FromClipId,
            to_clip_id = transition.ToClipId,
            start_seconds = RoundIntegrity(transition.Start.TotalSeconds),
            duration_seconds = RoundIntegrity(transition.Duration.TotalSeconds)
        }));

        var found = objects.Select(item =>
            JsonSerializer.SerializeToElement(item).GetProperty("id").GetGuid()).ToHashSet();
        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "timeline",
            project_revision = project.Revision,
            sequence_id = sequence.Id,
            sequence_revision = sequence.Revision,
            target = new { object_ids = requested.Order().ToArray() },
            objects,
            missing_ids = requested.Except(found).Order().ToArray(),
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Use inspect_range or inspect_boundary when content evidence is required."
        }));
    }

    public ValueTask<JsonElement> SearchTimelineAsync(
        AgentToolContext context,
        AgentTimelineSearchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var sequence = RequireSequence(project, request.SequenceId);
        var clips = sequence.MediaClips
            .Where(clip => request.SourceId is null || clip.SourceId == request.SourceId)
            .Where(clip => request.TrackId is null || clip.TrackId == request.TrackId)
            .Where(clip => request.StartSeconds is null || clip.End.TotalSeconds > request.StartSeconds)
            .Where(clip => request.EndSeconds is null || clip.Start.TotalSeconds < request.EndSeconds)
            .OrderBy(clip => clip.Start)
            .ThenBy(clip => clip.TrackId)
            .ThenBy(clip => clip.Id)
            .ToArray();
        var cursor = Math.Clamp(request.Cursor, 0, clips.Length);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var page = clips.Skip(cursor).Take(pageSize).Select(clip => new
        {
            id = clip.Id,
            source_id = clip.SourceId,
            track_id = clip.TrackId,
            start_seconds = RoundIntegrity(clip.Start.TotalSeconds),
            end_seconds = RoundIntegrity(clip.End.TotalSeconds),
            source_in_seconds = RoundIntegrity(clip.SourceIn.TotalSeconds),
            source_out_seconds = RoundIntegrity((clip.SourceIn + clip.Duration).TotalSeconds),
            link_group_id = clip.LinkGroupId
        }).ToArray();
        var nextCursor = cursor + page.Length < clips.Length ? cursor + page.Length : (int?)null;

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "timeline",
            project_revision = project.Revision,
            sequence_id = sequence.Id,
            sequence_revision = sequence.Revision,
            target = new
            {
                start_seconds = request.StartSeconds,
                end_seconds = request.EndSeconds,
                source_id = request.SourceId,
                track_id = request.TrackId
            },
            cursor,
            page_size = pageSize,
            total_matches = clips.Length,
            next_cursor = nextCursor,
            clips = page,
            truncated = nextCursor is not null,
            artifact_reference = (string?)null,
            recommended_next_inspection = nextCursor is null
                ? "Use inspect_objects for complete parameters or inspect_range for evidence."
                : $"Call search_timeline again with cursor={nextCursor}."
        }));
    }

    public ValueTask<JsonElement> InspectSequenceOverviewAsync(
        AgentToolContext context,
        Guid sequenceId,
        int bucketCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        var sequence = RequireSequence(project, sequenceId);
        var duration = Math.Max(0, sequence.Duration.TotalSeconds);
        var count = Math.Clamp(bucketCount, 4, 64);
        var bucketDuration = duration > 0 ? duration / count : 0;
        var trackById = sequence.Tracks.ToDictionary(track => track.Id);
        var buckets = Enumerable.Range(0, count).Select(index =>
        {
            var start = bucketDuration * index;
            var end = index == count - 1 ? duration : bucketDuration * (index + 1);
            var clips = sequence.MediaClips
                .Where(clip => clip.End.TotalSeconds > start && clip.Start.TotalSeconds < end)
                .ToArray();
            return new
            {
                index,
                start_seconds = Round(start),
                end_seconds = Round(end),
                visual_clip_count = clips.Count(clip =>
                    trackById.TryGetValue(clip.TrackId, out var track) && track.Kind == TrackKind.Visual),
                audio_clip_count = clips.Count(clip =>
                    trackById.TryGetValue(clip.TrackId, out var track) && track.Kind == TrackKind.Audio),
                junction_count = sequence.MediaClips.Count(clip =>
                    clip.Start.TotalSeconds >= start && clip.Start.TotalSeconds < end),
                transition_count = sequence.Transitions.Count(item =>
                    item.Start.TotalSeconds >= start && item.Start.TotalSeconds < end)
            };
        }).ToArray();
        var technicalMarkers = sequence.Markers
            .Where(marker => marker.Kind is MarkerKind.Scene or MarkerKind.Silence or MarkerKind.BlackFrame or MarkerKind.Freeze)
            .OrderBy(marker => marker.Start)
            .Select(ToMarkerObservation)
            .ToArray();

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "timeline",
            project_revision = project.Revision,
            sequence_id = sequence.Id,
            sequence_revision = sequence.Revision,
            target = new { start_seconds = 0, end_seconds = Round(duration) },
            frame_rate = project.FrameRate.ToString(),
            bucket_count = count,
            buckets,
            measured_candidates = technicalMarkers,
            semantic_labels_assigned = false,
            editing_decision_made = false,
            truncated = false,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Narrow a candidate with inspect_range, then verify each exact junction with inspect_boundary."
        }));
    }

    public ValueTask<JsonElement> InspectMediaAsync(
        AgentToolContext context,
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = Snapshot(context);
        if (!project.Sources.TryGetValue(mediaId, out var source))
        {
            throw new AgentToolRejectedException(
                "media_not_found",
                $"Media '{mediaId}' does not exist in the active project.");
        }

        var stableFingerprint = MontagePlanValidator.StableFingerprint(source);
        var allReferences = project.AnalysisReferences
            .Where(item => item.SourceId == source.Id)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArray();
        var references = allReferences
            .Take(MaximumAnalysisReferences)
            .Select(reference => new
            {
                pipeline_version = reference.PipelineVersion,
                model = reference.Model,
                profile_id = reference.ProfileId,
                profile_version = reference.ProfileVersion,
                updated_at = reference.UpdatedAt,
                current_fingerprint = string.Equals(
                    reference.SourceFingerprint,
                    stableFingerprint,
                    StringComparison.Ordinal)
            })
            .ToArray();

        var annotations = project.SourceAnnotations
            .Where(item => item.SourceId == source.Id)
            .OrderBy(item => item.SourceRange.Start)
            .Take(60)
            .Select(annotation => new
            {
                id = annotation.Id,
                kind = annotation.Kind.ToString().ToLowerInvariant(),
                start_seconds = Round(annotation.SourceRange.Start.TotalSeconds),
                end_seconds = Round(annotation.SourceRange.End.TotalSeconds),
                note = Compact(annotation.Note, 1_000),
                created_at = annotation.CreatedAt
            })
            .ToArray();

        var usages = project.Sequences
            .SelectMany(sequence => sequence.MediaClips
                .Where(clip => clip.SourceId == source.Id)
                .Select(clip => new
                {
                    sequence_id = sequence.Id,
                    sequence_name = Compact(sequence.Name, 300),
                    sequence_status = sequence.Status.ToString().ToLowerInvariant(),
                    clip_id = clip.Id,
                    timeline_start_seconds = Round(clip.Start.TotalSeconds),
                    timeline_end_seconds = Round(clip.End.TotalSeconds),
                    source_in_seconds = Round(clip.SourceIn.TotalSeconds),
                    source_out_seconds = Round((clip.SourceIn + clip.Duration).TotalSeconds)
                }))
            .OrderBy(item => item.sequence_name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.timeline_start_seconds)
            .Take(MaximumMediaUsages)
            .ToArray();

        var streams = source.Streams.IsDefault
            ? System.Collections.Immutable.ImmutableArray<MediaStreamDescriptor>.Empty
            : source.Streams;

        return ValueTask.FromResult(AgentToolJson.ToElement(new
        {
            channel = "project",
            project_revision = project.Revision,
            media_id = source.Id,
            target = new { media_id = source.Id },
            name = Compact(source.Name, 300),
            kind = source.Kind.ToString().ToLowerInvariant(),
            duration_seconds = Round(source.Duration.TotalSeconds),
            online_state = source.OnlineState.ToString().ToLowerInvariant(),
            has_audio = source.HasAudio,
            width = source.Width,
            height = source.Height,
            frame_rate = source.FrameRate?.ToString(),
            variable_frame_rate = source.IsVariableFrameRate,
            video_codec = source.VideoCodec,
            audio_codec = source.AudioCodec,
            file_size_bytes = source.FileSize,
            stream_count = streams.Length,
            streams = streams
                .Select(stream => new
                {
                    index = stream.StreamIndex,
                    kind = stream.Kind.ToString().ToLowerInvariant(),
                    codec = stream.Codec,
                    format = stream.PixelOrSampleFormat,
                    width = stream.Width,
                    height = stream.Height,
                    sample_rate = stream.SampleRate,
                    channels = stream.Channels,
                    frame_rate = stream.FrameRate?.ToString(),
                    variable_frame_rate = stream.IsVariableFrameRate
                })
                .ToArray(),
            analysis_references_truncated = allReferences.Length > references.Length,
            analysis_references = references,
            annotations_truncated = project.SourceAnnotations.Count(item => item.SourceId == source.Id) > annotations.Length,
            annotations,
            usages_truncated = project.Sequences.Sum(sequence => sequence.MediaClips.Count(clip => clip.SourceId == source.Id)) > usages.Length,
            usages,
            truncated = allReferences.Length > references.Length ||
                        project.SourceAnnotations.Count(item => item.SourceId == source.Id) > annotations.Length ||
                        project.Sequences.Sum(sequence => sequence.MediaClips.Count(clip => clip.SourceId == source.Id)) > usages.Length,
            artifact_reference = (string?)null,
            recommended_next_inspection = "Use inspect_range on a focused media range."
        }));
    }

    public async ValueTask<JsonElement> InspectRangeAsync(
        AgentToolContext context,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if ((request.Query ?? string.Empty).Length > MaximumQueryCharacters)
        {
            throw new AgentToolRejectedException(
                "query_too_long",
                $"Range query exceeds {MaximumQueryCharacters} characters.");
        }

        var project = Snapshot(context);

        return request.TargetKind switch
        {
            AgentRangeTargetKind.Media =>
                await InspectMediaRangeAsync(project, request, cancellationToken).ConfigureAwait(false),
            AgentRangeTargetKind.Sequence =>
                await InspectSequenceRangeAsync(project, request, cancellationToken).ConfigureAwait(false),
            _ => throw new AgentToolRejectedException(
                "unsupported_target",
                $"Unsupported range target '{request.TargetKind}'.")
        };
    }

    private async ValueTask<JsonElement> InspectMediaRangeAsync(
        ProjectState project,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!project.Sources.TryGetValue(request.TargetId, out var source))
        {
            throw new AgentToolRejectedException(
                "media_not_found",
                $"Media '{request.TargetId}' does not exist in the active project.");
        }

        var actual = NormalizeRange(
            request.StartSeconds,
            request.EndSeconds,
            source.Duration.TotalSeconds,
            "media");

        var observation = await rangeInspector.InspectAsync(
            source,
            request with
            {
                StartSeconds = actual.Start,
                EndSeconds = actual.End
            },
            cancellationToken).ConfigureAwait(false);
        return AgentToolJson.ToElement(new
        {
            channel = EvidenceChannel(request.Detail),
            project_revision = project.Revision,
            target = new { kind = "media", media_id = source.Id },
            media_id = source.Id,
            start_seconds = Round(actual.Start),
            end_seconds = Round(actual.End),
            detail = request.Detail.ToString().ToLowerInvariant(),
            truncated = ReadBoolean(observation, "truncated"),
            artifact_reference = ReadString(observation, "artifact_reference"),
            recommended_next_inspection = ReadString(observation, "recommended_next_inspection") ??
                                          "Use inspect_boundary to verify an exact junction.",
            observation
        });
    }

    private async ValueTask<JsonElement> InspectSequenceRangeAsync(
        ProjectState project,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var sequence = RequireSequence(project, request.TargetId);
        var actual = NormalizeRange(
            request.StartSeconds,
            request.EndSeconds,
            sequence.Duration.TotalSeconds,
            "sequence");

        var start = TimelineTime.FromSeconds(actual.Start);
        var end = TimelineTime.FromSeconds(actual.End);
        var localClips = sequence.MediaClips
            .Where(clip => clip.Start < end && clip.End > start)
            .OrderBy(clip => clip.Start)
            .ThenBy(clip => clip.TrackId)
            .ThenBy(clip => clip.Id)
            .Select(clip => BuildSlice(project, sequence, clip, start, end))
            .ToArray();

        var outputClips = localClips
            .Take(MaximumRangeClips)
            .ToArray();

        var uniqueAnalysisSlices = localClips
            .Where(slice => slice.SourceExists)
            .GroupBy(slice => (
                slice.SourceId,
                StartTicks: TimelineTime.FromSeconds(slice.SourceStartSeconds).Ticks,
                EndTicks: TimelineTime.FromSeconds(slice.SourceEndSeconds).Ticks))
            .Select(group => group.First())
            .ToArray();

        var text = sequence.TextClips
            .Where(clip => clip.Start < end && clip.End > start)
            .OrderBy(clip => clip.Start)
            .Take(MaximumRangeTextClips)
            .Select(clip => new
            {
                id = clip.Id,
                start_seconds = Round(Math.Max(actual.Start, clip.Start.TotalSeconds)),
                end_seconds = Round(Math.Min(actual.End, clip.End.TotalSeconds)),
                text = Compact(clip.Text, 1_000),
                is_subtitle = clip.Style.IsSubtitle
            })
            .ToArray();

        var allLocalMarkers = sequence.Markers
            .Where(marker => marker.Start < end && marker.End > start)
            .OrderBy(marker => marker.Start)
            .ToArray();
        var markers = allLocalMarkers
            .Take(MaximumRangeMarkers)
            .Select(ToMarkerObservation)
            .ToArray();

        var allLocalTransitions = sequence.Transitions
            .Where(transition => transition.Start < end && transition.End > start)
            .OrderBy(transition => transition.Start)
            .ToArray();
        var transitions = allLocalTransitions
            .Take(MaximumRangeTransitions)
            .Select(transition => new
            {
                id = transition.Id,
                kind = transition.Kind.ToString().ToLowerInvariant(),
                start_seconds = Round(transition.Start.TotalSeconds),
                duration_seconds = Round(transition.Duration.TotalSeconds)
            })
            .ToArray();

        var analysisSeconds = uniqueAnalysisSlices.Sum(
            slice => Math.Max(0, slice.SourceEndSeconds - slice.SourceStartSeconds));
        var maximumAnalysisSeconds =
            request.Detail is AgentRangeInspectionDetail.Frames or AgentRangeInspectionDetail.All
                ? MaximumSequenceVisionAnalysisSeconds
                : MaximumSequenceOtherAnalysisSeconds;
        var analysisDeferred =
            request.Detail != AgentRangeInspectionDetail.Summary &&
            (uniqueAnalysisSlices.Length > MaximumSequenceAnalysisSlices ||
             analysisSeconds > maximumAnalysisSeconds);

        var analyses = new List<object>();
        if (request.Detail != AgentRangeInspectionDetail.Summary && !analysisDeferred)
        {
            foreach (var slice in uniqueAnalysisSlices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!project.Sources.TryGetValue(slice.SourceId, out var source))
                {
                    continue;
                }

                try
                {
                    var observation = await rangeInspector.InspectAsync(
                        source,
                        new AgentRangeInspectionRequest(
                            AgentRangeTargetKind.Media,
                            source.Id,
                            slice.SourceStartSeconds,
                            slice.SourceEndSeconds,
                            request.Detail,
                            request.Query),
                        cancellationToken).ConfigureAwait(false);

                    analyses.Add(new
                    {
                        source_id = source.Id,
                        source_name = Compact(source.Name, 300),
                        timeline_start_seconds = slice.TimelineStartSeconds,
                        timeline_end_seconds = slice.TimelineEndSeconds,
                        source_start_seconds = slice.SourceStartSeconds,
                        source_end_seconds = slice.SourceEndSeconds,
                        status = "succeeded",
                        observation
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AgentToolRejectedException exception)
                {
                    analyses.Add(new
                    {
                        source_id = source.Id,
                        source_name = Compact(source.Name, 300),
                        timeline_start_seconds = slice.TimelineStartSeconds,
                        timeline_end_seconds = slice.TimelineEndSeconds,
                        source_start_seconds = slice.SourceStartSeconds,
                        source_end_seconds = slice.SourceEndSeconds,
                        status = "rejected",
                        error_code = exception.ErrorCode,
                        message = exception.Message
                    });
                }
                catch (Exception exception)
                {
                    analyses.Add(new
                    {
                        source_id = source.Id,
                        source_name = Compact(source.Name, 300),
                        timeline_start_seconds = slice.TimelineStartSeconds,
                        timeline_end_seconds = slice.TimelineEndSeconds,
                        source_start_seconds = slice.SourceStartSeconds,
                        source_end_seconds = slice.SourceEndSeconds,
                        status = "failed",
                        message = exception.Message
                    });
                }
            }
        }

        return AgentToolJson.ToElement(new
        {
            channel = EvidenceChannel(request.Detail),
            project_revision = project.Revision,
            sequence_id = sequence.Id,
            sequence_revision = sequence.Revision,
            sequence_name = Compact(sequence.Name, 300),
            target = new { kind = "sequence", sequence_id = sequence.Id },
            start_seconds = Round(actual.Start),
            end_seconds = Round(actual.End),
            detail = request.Detail.ToString().ToLowerInvariant(),
            query = Compact(request.Query, MaximumQueryCharacters),
            requested_range = new
            {
                start_seconds = Round(request.StartSeconds),
                end_seconds = Round(request.EndSeconds)
            },
            actual_range = new
            {
                start_seconds = Round(actual.Start),
                end_seconds = Round(actual.End)
            },
            media_clip_count = localClips.Length,
            media_clips_truncated = localClips.Length > outputClips.Length,
            media_clips = outputClips.Select(slice => new
            {
                clip_id = slice.ClipId,
                source_id = slice.SourceId,
                source_name = slice.SourceName,
                source_exists = slice.SourceExists,
                track_id = slice.TrackId,
                track_name = slice.TrackName,
                track_kind = slice.TrackKind,
                timeline_start_seconds = slice.TimelineStartSeconds,
                timeline_end_seconds = slice.TimelineEndSeconds,
                source_start_seconds = slice.SourceStartSeconds,
                source_end_seconds = slice.SourceEndSeconds
            }).ToArray(),
            text_clips = text,
            markers_truncated = allLocalMarkers.Length > markers.Length,
            markers,
            transitions_truncated = allLocalTransitions.Length > transitions.Length,
            transitions,
            analysis_deferred = analysisDeferred,
            analysis_deferred_reason = analysisDeferred
                ? $"The range maps to {uniqueAnalysisSlices.Length} distinct media slices " +
                  $"covering {analysisSeconds:0.###} source seconds. Inspect one of the returned " +
                  $"source ranges directly or narrow the sequence range."
                : null,
            analyses,
            truncated = localClips.Length > outputClips.Length ||
                        allLocalMarkers.Length > markers.Length ||
                        allLocalTransitions.Length > transitions.Length ||
                        analysisDeferred,
            artifact_reference = (string?)null,
            recommended_next_inspection = analysisDeferred
                ? "Narrow the sequence range or inspect one returned media source range directly."
                : "Use inspect_boundary around each proposed exact edit point."
        });
    }

    private ProjectState Snapshot(AgentToolContext context)
    {
        var project = stateProvider()
            ?? throw new InvalidOperationException("Project state provider returned null.");

        if (project.Id != context.ProjectId)
        {
            throw new AgentToolRejectedException(
                "project_changed",
                "The project changed after this agent task started.");
        }

        return project.SynchronizeActiveSequence(incrementRevision: false);
    }

    private static SequenceState RequireSequence(
        ProjectState project,
        Guid sequenceId)
    {
        var sequence = project.FindSequence(sequenceId);
        if (sequence is null)
        {
            throw new AgentToolRejectedException(
                "sequence_not_found",
                $"Sequence '{sequenceId}' does not exist in the active project.");
        }

        return sequence;
    }

    private static (double Start, double End) NormalizeRange(
        double requestedStart,
        double requestedEnd,
        double duration,
        string targetName)
    {
        if (duration <= 0)
        {
            throw new AgentToolRejectedException(
                "empty_target",
                $"The requested {targetName} has no duration.");
        }

        var start = Math.Clamp(requestedStart, 0, duration);
        var end = Math.Clamp(requestedEnd, 0, duration);
        if (start >= duration || end <= start + 0.01)
        {
            throw new AgentToolRejectedException(
                "range_out_of_bounds",
                $"Requested range does not overlap the {targetName}.");
        }

        return (start, end);
    }

    private static SequenceSlice BuildSlice(
        ProjectState project,
        SequenceState sequence,
        MediaClip clip,
        TimelineTime rangeStart,
        TimelineTime rangeEnd)
    {
        var overlapStart = clip.Start > rangeStart ? clip.Start : rangeStart;
        var overlapEnd = clip.End < rangeEnd ? clip.End : rangeEnd;
        var offset = overlapStart - clip.Start;
        var sourceStart = clip.SourceIn + offset;
        var sourceEnd = sourceStart + (overlapEnd - overlapStart);
        project.Sources.TryGetValue(clip.SourceId, out var source);
        var track = sequence.Tracks.FirstOrDefault(item => item.Id == clip.TrackId);

        return new SequenceSlice(
            clip.Id,
            clip.SourceId,
            Compact(source?.Name, 300),
            source is not null,
            clip.TrackId,
            Compact(track?.Name, 200),
            track?.Kind.ToString().ToLowerInvariant() ?? "unknown",
            Round(overlapStart.TotalSeconds),
            Round(overlapEnd.TotalSeconds),
            Round(sourceStart.TotalSeconds),
            Round(sourceEnd.TotalSeconds));
    }

    private static object ToMarkerObservation(TimelineMarker marker)
        => new
        {
            id = marker.Id,
            kind = marker.Kind.ToString().ToLowerInvariant(),
            start_seconds = Round(marker.Start.TotalSeconds),
            end_seconds = Round(marker.End.TotalSeconds),
            title = Compact(marker.Title, 500),
            description = Compact(marker.Description, 1_000),
            source_id = marker.SourceId,
            source_start_seconds = Round(marker.SourceStart.TotalSeconds),
            confidence = Math.Round(marker.Confidence, 3),
            query = Compact(marker.Query, 800)
        };

    private static string Compact(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }

    private static double Round(double value)
        => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static double RoundIntegrity(double value)
        => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string EvidenceChannel(AgentRangeInspectionDetail detail)
        => detail switch
        {
            AgentRangeInspectionDetail.Frames => "frames",
            AgentRangeInspectionDetail.Audio => "audio",
            AgentRangeInspectionDetail.Transcript => "transcript",
            AgentRangeInspectionDetail.All => "all",
            _ => "timeline"
        };

    private static bool ReadBoolean(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed record SequenceSlice(
        Guid ClipId,
        Guid SourceId,
        string SourceName,
        bool SourceExists,
        Guid TrackId,
        string TrackName,
        string TrackKind,
        double TimelineStartSeconds,
        double TimelineEndSeconds,
        double SourceStartSeconds,
        double SourceEndSeconds);
}
