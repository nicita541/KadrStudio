using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed class DeterministicDraftQualityAnalyzer : IDraftQualityAnalyzer
{
    public DraftQualityReport Analyze(
        ProjectState sourceProject,
        Guid taskId,
        SequenceState draft,
        MontageGraph graph,
        ImmutableArray<DraftQualityIssue> criticIssues)
    {
        var issues = ImmutableArray.CreateBuilder<DraftQualityIssue>();
        issues.AddRange(criticIssues.IsDefault ? [] : criticIssues);
        if (draft.Status != SequenceStatus.Draft)
            issues.Add(new DraftQualityIssue("draft.status", "Compiled sequence is not an Agent Draft.", null, true));
        if (draft.ParentSequenceId != graph.SourceSequenceId)
            issues.Add(new DraftQualityIssue("draft.parent", "Agent Draft parent differs from the directed source sequence.", null, true));
        if (draft.MediaClips.IsDefaultOrEmpty)
            issues.Add(new DraftQualityIssue("draft.empty", "Agent Draft contains no media.", null, true));
        var scope = graph.Brief.ScopePolicy ?? graph.Brief.Profile.DefaultScopePolicy ?? EditScopePolicy.General;
        var removals = graph.Decisions.Where(item => item.Kind == EditDecisionKind.Remove).ToArray();
        if (scope.Preservation == PreservationPolicy.ExactComplement)
            VerifyExactComplement(sourceProject, draft, graph, removals, issues);
        foreach (var decision in removals.Where(item => item.Confidence is >= 0.65 and < 0.85))
            issues.Add(new DraftQualityIssue(
                "draft.boundary_needs_review",
                $"{decision.SegmentRole} boundaries are plausible but require mandatory A/B review.",
                decision.Target?.TimelineRange ?? decision.SourceRange,
                false));

        foreach (var decision in graph.Decisions.Where(item => item.Kind is
                     EditDecisionKind.Retime or
                     EditDecisionKind.ApplyDialogueCut or
                     EditDecisionKind.ApplyJlCut))
        {
            issues.Add(new DraftQualityIssue(
                "draft.capability_unavailable",
                $"The compiled Draft does not yet contain the requested {decision.Kind} operation.",
                decision.SourceRange,
                true));
        }

        foreach (var clip in draft.MediaClips)
        {
            if (!sourceProject.Sources.TryGetValue(clip.SourceId, out var source))
            {
                issues.Add(new DraftQualityIssue("draft.source_missing", "Draft references a missing source.", clip.Range, true));
                continue;
            }
            if (source.OnlineState != MediaOnlineState.Online)
                issues.Add(new DraftQualityIssue("draft.source_offline", $"Source '{source.Name}' is offline.", clip.Range, true));
            if (clip.SourceIn < TimelineTime.Zero || clip.Duration <= TimelineTime.Zero ||
                source.Kind != MediaKind.Image && clip.SourceIn + clip.Duration > source.Duration)
                issues.Add(new DraftQualityIssue("draft.source_range", "Draft clip exceeds source bounds.", clip.Range, true));
        }

        foreach (var track in draft.Tracks)
        {
            var overlaps = draft.MediaClips
                .Where(item => item.TrackId == track.Id)
                .OrderBy(item => item.Start)
                .Zip(draft.MediaClips.Where(item => item.TrackId == track.Id).OrderBy(item => item.Start).Skip(1))
                .Where(pair => pair.First.End > pair.Second.Start)
                .ToArray();
            if (overlaps.Length > 0)
                issues.Add(new DraftQualityIssue("draft.overlap", $"Track '{track.Name}' contains unintended overlaps.", null, true));
        }

        var blocking = issues.Count(item => item.IsBlocking);
        var status = blocking > 0
            ? DraftQualityStatus.Failed
            : issues.Count > 0 ? DraftQualityStatus.NeedsReview : DraftQualityStatus.Passed;
        if (status == DraftQualityStatus.Passed && removals.Any(item => item.Confidence < 0.85))
            status = DraftQualityStatus.NeedsReview;
        var sourceSequence = sourceProject.FindSequence(graph.SourceSequenceId);
        var sourceDuration = sourceSequence?.Duration ?? TimelineTime.Zero;
        var sourceClipCount = sourceSequence?.MediaClips.Length ?? 0;
        var metrics = ImmutableDictionary<string, double>.Empty
            .Add("duration_seconds", draft.Duration.TotalSeconds)
            .Add("source_duration_seconds", sourceDuration.TotalSeconds)
            .Add("duration_delta_seconds", draft.Duration.TotalSeconds - sourceDuration.TotalSeconds)
            .Add("clip_count", draft.MediaClips.Length)
            .Add("source_clip_count", sourceClipCount)
            .Add("clip_count_delta", draft.MediaClips.Length - sourceClipCount)
            .Add("remove_count", removals.Length)
            .Add("removed_duration_seconds", removals
                .Select(item => item.Target?.TimelineRange ?? item.SourceRange)
                .Distinct()
                .Sum(item => item.Duration.TotalSeconds))
            .Add("cut_count", Math.Max(0, draft.MediaClips.Count(item => item.Video is not null) - 1))
            .Add("blocking_issue_count", blocking)
            .Add("source_preserved", 1);
        return new DraftQualityReport(
            Guid.NewGuid(), taskId, draft.Id, status,
            issues.ToImmutable(), metrics, DateTimeOffset.UtcNow);
    }

    private static void VerifyExactComplement(
        ProjectState sourceProject,
        SequenceState draft,
        MontageGraph graph,
        IReadOnlyList<EditDecision> removals,
        ICollection<DraftQualityIssue> issues)
    {
        if (graph.Brief.Profile.Kind == MontageProfileKind.AnimeEpisode)
        {
            var invalidPairs = removals
                .GroupBy(item => item.SourceId)
                .Any(group =>
                    group.Count(item => item.SegmentRole == SegmentRole.Opening) == 0 ||
                    group.Count(item => item.SegmentRole == SegmentRole.Opening) !=
                    group.Count(item => item.SegmentRole == SegmentRole.Ending));
            if (removals.Count == 0 || invalidPairs ||
                graph.Decisions.Count(item => item.Kind != EditDecisionKind.Remove) != 0)
                issues.Add(new DraftQualityIssue(
                    "exact.graph_shape",
                    "Anime exact-complement Draft must contain one matched Opening/Ending pair per selected episode occurrence.",
                    null, true));
        }
        if (removals.Any(item => item.Target is null))
        {
            issues.Add(new DraftQualityIssue(
                "exact.target", "Exact-complement removal lacks a timeline occurrence target.", null, true));
            return;
        }
        if (removals.Any(item => item.SegmentRole is not (SegmentRole.Opening or SegmentRole.Ending)))
            issues.Add(new DraftQualityIssue(
                "exact.role", "Exact-complement removal targets content other than Opening/Ending.", null, true));

        var source = sourceProject.FindSequence(graph.SourceSequenceId);
        if (source is null)
        {
            issues.Add(new DraftQualityIssue("exact.source", "Directed source sequence is missing.", null, true));
            return;
        }
        var ranges = NormalizeRanges(
            removals.Select(item => item.Target!.TimelineRange), source.Settings.FrameRate, source.Duration);
        var expected = sourceProject with
        {
            Sequence = source.Settings,
            Tracks = source.Tracks,
            MediaClips = source.MediaClips,
            SubtitleClips = source.SubtitleClips,
            TextClips = source.TextClips,
            Transitions = source.Transitions,
            Markers = source.Markers,
            InPoint = source.InPoint,
            OutPoint = source.OutPoint
        };
        foreach (var range in ranges.OrderByDescending(item => item.Start))
            expected = TimelineRangeTransformer.RippleDelete(expected, range);

        if (draft.Settings != expected.Sequence || !draft.Tracks.SequenceEqual(expected.Tracks))
            issues.Add(new DraftQualityIssue(
                "exact.sequence", "Sequence settings or tracks changed outside the requested removals.", null, true));
        if (!MediaShape(draft.MediaClips).SequenceEqual(MediaShape(expected.MediaClips)))
            issues.Add(new DraftQualityIssue(
                "exact.media", "Video/audio complement differs from a pure ripple-delete transformation.", null, true));
        if (!SubtitleShape(draft.SubtitleClips).SequenceEqual(SubtitleShape(expected.SubtitleClips)))
            issues.Add(new DraftQualityIssue(
                "exact.subtitles", "Subtitle stream mappings are not the exact complement of the source.", null, true));
        if (!LinkTopology(draft.MediaClips, draft.SubtitleClips)
                .SequenceEqual(LinkTopology(expected.MediaClips, expected.SubtitleClips)))
            issues.Add(new DraftQualityIssue(
                "exact.links", "Video, audio, and subtitle link-group topology changed outside the removals.", null, true));
        if (!draft.TextClips.SequenceEqual(expected.TextClips) ||
            !draft.Markers.SequenceEqual(expected.Markers) ||
            !TransitionShape(draft.Transitions).SequenceEqual(TransitionShape(expected.Transitions)))
            issues.Add(new DraftQualityIssue(
                "exact.metadata", "Titles, markers, or transitions changed beyond the requested removals.", null, true));
        if (draft.InPoint != expected.InPoint || draft.OutPoint != expected.OutPoint || draft.Duration != expected.Duration)
            issues.Add(new DraftQualityIssue(
                "exact.duration", "Draft duration or In/Out mapping differs from the exact complement.", null, true));

        var expectedDuration = source.Duration - ranges.Aggregate(TimelineTime.Zero, (sum, item) => sum + item.Duration);
        if (draft.Duration != expectedDuration)
            issues.Add(new DraftQualityIssue(
                "exact.duration_delta", "Draft duration does not equal source duration minus normalized removals.", null, true));
        var frame = source.Settings.FrameRate.FrameDuration;
        foreach (var range in ranges)
            if (Distance(range.Start, range.Start.SnapToFrame(source.Settings.FrameRate)) > frame ||
                Distance(range.End, range.End.SnapToFrame(source.Settings.FrameRate)) > frame)
                issues.Add(new DraftQualityIssue(
                    "exact.frame_boundary", "A removal boundary is farther than one frame from a frame boundary.", range, true));
    }

    private static ImmutableArray<TimeRange> NormalizeRanges(
        IEnumerable<TimeRange> ranges,
        FrameRate frameRate,
        TimelineTime duration)
    {
        var ordered = ranges.Select(item => new TimeRange(
                item.Start.SnapToFrame(frameRate),
                item.End.SnapToFrame(frameRate) - item.Start.SnapToFrame(frameRate)))
            .Where(item => item.Duration > TimelineTime.Zero && item.Start < duration)
            .OrderBy(item => item.Start)
            .ToArray();
        var output = ImmutableArray.CreateBuilder<TimeRange>();
        foreach (var item in ordered)
        {
            var clippedEnd = item.End > duration ? duration : item.End;
            var clipped = new TimeRange(item.Start, clippedEnd - item.Start);
            if (output.Count == 0 || clipped.Start > output[^1].End)
            {
                output.Add(clipped);
                continue;
            }
            var previous = output[^1];
            var end = clipped.End > previous.End ? clipped.End : previous.End;
            output[^1] = new TimeRange(previous.Start, end - previous.Start);
        }
        return output.ToImmutable();
    }

    private static IEnumerable<MediaClip> MediaShape(IEnumerable<MediaClip> clips)
        => clips.OrderBy(item => item.TrackId).ThenBy(item => item.Start).ThenBy(item => item.SourceIn)
            .Select(item => item with { Id = Guid.Empty, LinkGroupId = null });

    private static IEnumerable<SubtitleClip> SubtitleShape(IEnumerable<SubtitleClip> clips)
        => clips.OrderBy(item => item.TrackId).ThenBy(item => item.Start).ThenBy(item => item.SourceIn)
            .Select(item => item with { Id = Guid.Empty, LinkGroupId = null });

    private static IEnumerable<TimelineTransition> TransitionShape(IEnumerable<TimelineTransition> transitions)
        => transitions.OrderBy(item => item.TrackId).ThenBy(item => item.Start)
            .Select(item => item with { Id = Guid.Empty, FromClipId = Guid.Empty, ToClipId = Guid.Empty });

    private static IEnumerable<string> LinkTopology(
        IEnumerable<MediaClip> media,
        IEnumerable<SubtitleClip> subtitles)
    {
        var members = media.Where(item => item.LinkGroupId.HasValue)
            .Select(item => new
            {
                Group = item.LinkGroupId!.Value,
                Shape = $"M|{item.TrackId:N}|{item.SourceId:N}|{item.Start.Ticks}|{item.SourceIn.Ticks}|{item.Duration.Ticks}|{item.StreamIndex}"
            })
            .Concat(subtitles.Where(item => item.LinkGroupId.HasValue)
                .Select(item => new
                {
                    Group = item.LinkGroupId!.Value,
                    Shape = $"S|{item.TrackId:N}|{item.SourceId:N}|{item.Start.Ticks}|{item.SourceIn.Ticks}|{item.Duration.Ticks}|{item.StreamIndex}"
                }));
        return members.GroupBy(item => item.Group)
            .Select(group => string.Join(';', group.Select(item => item.Shape).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal);
    }

    private static TimelineTime Distance(TimelineTime left, TimelineTime right)
        => left >= right ? left - right : right - left;
}
