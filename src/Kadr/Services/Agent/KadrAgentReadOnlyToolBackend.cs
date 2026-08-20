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
    IAgentMediaRangeInspector rangeInspector) : IAgentReadOnlyToolBackend
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
            project_id = project.Id,
            name = project.Name,
            revision = project.Revision,
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
            montage_plan_count = project.MontagePlans.Length
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
            sequence_id = sequence.Id,
            name = Compact(sequence.Name, 300),
            status = sequence.Status.ToString().ToLowerInvariant(),
            revision = sequence.Revision,
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
            transitions
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
            media_id = source.Id,
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
            usages
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

        return await rangeInspector.InspectAsync(
            source,
            request with
            {
                StartSeconds = actual.Start,
                EndSeconds = actual.End
            },
            cancellationToken).ConfigureAwait(false);
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
            sequence_id = sequence.Id,
            sequence_name = Compact(sequence.Name, 300),
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
            analyses
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
