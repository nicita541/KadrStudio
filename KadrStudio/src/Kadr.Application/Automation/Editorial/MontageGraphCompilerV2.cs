using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed class MontageGraphCompilerV2 : IMontageGraphCompilerV2
{
    public MontageGraphCompilationResult Compile(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        MontageGraph graph,
        ImmutableArray<DraftPatch> patches)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(graph);
        project = project.EnsureSequenceContainer();
        var sourceSequence = project.FindSequence(graph.SourceSequenceId)
            ?? throw new InvalidOperationException("Source sequence for MontageGraph was not found.");
        if (sourceSequence.Revision != graph.SourceSequenceRevision)
            throw new InvalidOperationException("MontageGraph is stale and cannot be compiled.");

        var decisions = MergeDecisions(graph.Decisions, patches);
        var scope = graph.Brief.ScopePolicy ?? graph.Brief.Profile.DefaultScopePolicy ?? EditScopePolicy.General;
        if (scope.Preservation == PreservationPolicy.ExactComplement)
            return CompileExactComplement(project, sourceSequence, graph, decisions);
        return CompileFlexibleNative(project, sourceSequence, graph, decisions);
    }

    private static MontageGraphCompilationResult CompileFlexibleNative(
        ProjectState project,
        SequenceState sourceSequence,
        MontageGraph graph,
        ImmutableArray<EditDecision> decisions)
    {
        var resolvedGraph = new MontageGraphTargetResolver().Resolve(project, graph with { Decisions = decisions });
        decisions = resolvedGraph.Decisions;
        var selectors = decisions.Where(item => item.Kind is
                EditDecisionKind.Keep or EditDecisionKind.Reorder or
                EditDecisionKind.SelectTake or EditDecisionKind.InsertBroll)
            .OrderBy(item => item.Order).ThenBy(item => item.Id).ToArray();
        var sourceView = project with
        {
            Sequence = sourceSequence.Settings,
            Tracks = sourceSequence.Tracks,
            MediaClips = sourceSequence.MediaClips,
            SubtitleClips = sourceSequence.SubtitleClips,
            TextClips = sourceSequence.TextClips,
            Transitions = sourceSequence.Transitions,
            Markers = sourceSequence.Markers,
            InPoint = sourceSequence.InPoint,
            OutPoint = sourceSequence.OutPoint
        };
        ProjectState working;
        ImmutableArray<TimeRange> removedRanges = [];
        if (selectors.Length == 0)
        {
            removedRanges = NormalizeRanges(
                decisions.Where(item => item.Kind == EditDecisionKind.Remove)
                    .Select(item => item.Target?.TimelineRange ?? item.SourceRange),
                sourceSequence.Settings.FrameRate,
                sourceSequence.Duration);
            working = sourceView;
            foreach (var range in removedRanges.OrderByDescending(item => item.Start))
                working = TimelineRangeTransformer.RippleDelete(working, range);
        }
        else
        {
            working = ConcatenateSelections(sourceView, selectors);
        }
        working = ApplySemanticParameters(working, decisions);

        var warnings = ImmutableArray.CreateBuilder<string>();
        if (decisions.Any(item => item.Kind == EditDecisionKind.Retime))
            warnings.Add("Retime requires the dedicated runtime capability and was not silently approximated.");
        if (decisions.Any(item => item.Kind is EditDecisionKind.ApplyDialogueCut or EditDecisionKind.ApplyJlCut))
            warnings.Add("Dialogue/J-L metadata requires an explicit stream-offset command and was not silently approximated.");
        var sequence = sourceSequence with
        {
            Id = Guid.NewGuid(),
            Name = DraftName(graph.Brief.Goal),
            Revision = 0,
            Status = SequenceStatus.Draft,
            ParentSequenceId = sourceSequence.Id,
            Settings = working.Sequence,
            Tracks = working.Tracks,
            MediaClips = working.MediaClips,
            SubtitleClips = working.SubtitleClips,
            TextClips = working.TextClips,
            Transitions = working.Transitions,
            Markers = working.Markers,
            InPoint = working.InPoint,
            OutPoint = working.OutPoint
        };
        var receiptRanges = removedRanges.IsDefaultOrEmpty
            ? decisions.Where(item => item.Target is not null)
                .Select(item => item.Target!.TimelineRange).ToImmutableArray()
            : removedRanges;
        return new MontageGraphCompilationResult(
            new NativeDraftCompilation(sequence, warnings.ToImmutable()),
            warnings.ToImmutable(), removedRanges,
            CreateReceipts(graph, sourceSequence, sequence, decisions, receiptRanges),
            CreateDiff(sourceSequence, sequence, removedRanges));
    }

    private static ProjectState ConcatenateSelections(
        ProjectState source,
        IReadOnlyList<EditDecision> selectors)
    {
        var media = ImmutableArray.CreateBuilder<MediaClip>();
        var subtitles = ImmutableArray.CreateBuilder<SubtitleClip>();
        var text = ImmutableArray.CreateBuilder<TextClip>();
        var markers = ImmutableArray.CreateBuilder<TimelineMarker>();
        var transitions = ImmutableArray.CreateBuilder<TimelineTransition>();
        var cursor = TimelineTime.Zero;
        foreach (var selector in selectors)
        {
            var range = selector.Target?.TimelineRange
                ?? throw new InvalidOperationException("Native Draft selections require occurrence-aware targets.");
            var clipIds = new Dictionary<Guid, Guid>();
            var linkGroups = new Dictionary<Guid, Guid>();
            foreach (var clip in source.MediaClips.Where(item => item.Range.Overlaps(range)))
            {
                var intersection = Intersection(clip.Range, range);
                var newId = Guid.NewGuid();
                clipIds[clip.Id] = newId;
                media.Add(clip with
                {
                    Id = newId,
                    LinkGroupId = MapGroup(clip.LinkGroupId, linkGroups),
                    Start = cursor + (intersection.Start - range.Start),
                    SourceIn = source.Sources[clip.SourceId].Kind == MediaKind.Image
                        ? clip.SourceIn
                        : clip.SourceIn + (intersection.Start - clip.Start),
                    Duration = intersection.Duration,
                    Audio = ClampAudio(clip.Audio, intersection.Duration)
                });
            }
            foreach (var clip in source.SubtitleClips.Where(item => item.Range.Overlaps(range)))
            {
                var intersection = Intersection(clip.Range, range);
                subtitles.Add(clip with
                {
                    Id = Guid.NewGuid(),
                    LinkGroupId = MapGroup(clip.LinkGroupId, linkGroups),
                    Start = cursor + (intersection.Start - range.Start),
                    SourceIn = clip.SourceIn + (intersection.Start - clip.Start),
                    Duration = intersection.Duration
                });
            }
            foreach (var clip in source.TextClips.Where(item => item.Range.Overlaps(range)))
            {
                var intersection = Intersection(clip.Range, range);
                text.Add(clip with
                {
                    Id = Guid.NewGuid(),
                    Start = cursor + (intersection.Start - range.Start),
                    Duration = intersection.Duration
                });
            }
            foreach (var marker in source.Markers.Where(item => item.Range.Overlaps(range)))
            {
                var intersection = Intersection(marker.Range, range);
                markers.Add(marker with
                {
                    Id = Guid.NewGuid(),
                    Start = cursor + (intersection.Start - range.Start),
                    SourceStart = marker.SourceStart + (intersection.Start - marker.Start),
                    Duration = intersection.Duration
                });
            }
            foreach (var transition in source.Transitions.Where(item =>
                         item.Range.Start >= range.Start && item.Range.End <= range.End &&
                         clipIds.ContainsKey(item.FromClipId) && clipIds.ContainsKey(item.ToClipId)))
                transitions.Add(transition with
                {
                    Id = Guid.NewGuid(),
                    FromClipId = clipIds[transition.FromClipId],
                    ToClipId = clipIds[transition.ToClipId],
                    Start = cursor + (transition.Start - range.Start)
                });
            cursor += range.Duration;
        }
        return source with
        {
            MediaClips = media.ToImmutable(),
            SubtitleClips = subtitles.ToImmutable(),
            TextClips = text.ToImmutable(),
            Markers = markers.ToImmutable(),
            Transitions = transitions.ToImmutable(),
            InPoint = null,
            OutPoint = null
        };
    }

    private static ProjectState ApplySemanticParameters(
        ProjectState project,
        ImmutableArray<EditDecision> decisions)
    {
        var overlays = decisions.Where(item => item.Kind is
            EditDecisionKind.ApplyAudioMix or EditDecisionKind.AutoReframe).ToArray();
        if (overlays.Length == 0) return project;
        return project with
        {
            MediaClips = project.MediaClips.Select(clip =>
            {
                var matching = overlays.Where(item => item.SourceId == clip.SourceId &&
                    item.SourceRange.Overlaps(new TimeRange(clip.SourceIn, clip.Duration))).ToArray();
                var result = clip;
                if (result.Audio is { } audio && matching.Any(item => item.Kind == EditDecisionKind.ApplyAudioMix))
                    result = result with { Audio = audio with { Volume = ReadDouble(matching, "volume", audio.Volume, 0, 2) } };
                if (result.Video is { } video && matching.Any(item => item.Kind == EditDecisionKind.AutoReframe))
                    result = result with
                    {
                        Video = video with
                        {
                            PositionX = ReadDouble(matching, "position_x", video.PositionX, -5, 5),
                            PositionY = ReadDouble(matching, "position_y", video.PositionY, -5, 5),
                            ScaleX = ReadDouble(matching, "scale_x", video.ScaleX, 0.01, 100),
                            ScaleY = ReadDouble(matching, "scale_y", video.ScaleY, 0.01, 100)
                        }
                    };
                return result;
            }).ToImmutableArray()
        };
    }

    private static Guid? MapGroup(Guid? group, IDictionary<Guid, Guid> groups)
    {
        if (group is not { } value) return null;
        if (!groups.TryGetValue(value, out var mapped)) groups[value] = mapped = Guid.NewGuid();
        return mapped;
    }

    private static AudioParameters? ClampAudio(AudioParameters? audio, TimelineTime duration)
        => audio is null ? null : audio with
        {
            FadeIn = audio.FadeIn > duration ? duration : audio.FadeIn,
            FadeOut = audio.FadeOut > duration ? duration : audio.FadeOut
        };

    private static TimeRange Intersection(TimeRange left, TimeRange right)
    {
        var start = left.Start > right.Start ? left.Start : right.Start;
        var end = left.End < right.End ? left.End : right.End;
        return new TimeRange(start, end - start);
    }

    private static string DraftName(string goal)
    {
        var title = string.IsNullOrWhiteSpace(goal) ? "Agent Draft" : $"Agent Draft · {goal.Trim()}";
        return title.Length <= 96 ? title : title[..96].TrimEnd();
    }

    private static MontageGraphCompilationResult CompileExactComplement(
        ProjectState project,
        SequenceState sourceSequence,
        MontageGraph graph,
        ImmutableArray<EditDecision> decisions)
    {
        if (decisions.Any(item => item.Kind != EditDecisionKind.Remove))
            throw new InvalidOperationException("Exact-complement compilation accepts Remove decisions only.");
        if (decisions.Any(item => item.Target is null))
            throw new InvalidOperationException("Exact-complement decisions require occurrence-aware timeline targets.");
        var ranges = NormalizeRanges(
            decisions.Select(item => item.Target!.TimelineRange),
            sourceSequence.Settings.FrameRate,
            sourceSequence.Duration);
        if (ranges.IsDefaultOrEmpty)
            throw new InvalidOperationException("Exact-complement graph contains no removable timeline ranges.");

        var working = project with
        {
            Sequence = sourceSequence.Settings,
            Tracks = sourceSequence.Tracks,
            MediaClips = sourceSequence.MediaClips,
            SubtitleClips = sourceSequence.SubtitleClips,
            TextClips = sourceSequence.TextClips,
            Transitions = sourceSequence.Transitions,
            Markers = sourceSequence.Markers,
            InPoint = sourceSequence.InPoint,
            OutPoint = sourceSequence.OutPoint
        };
        foreach (var range in ranges.OrderByDescending(item => item.Start))
            working = TimelineRangeTransformer.RippleDelete(working, range);

        var title = string.IsNullOrWhiteSpace(graph.Brief.Goal)
            ? "Agent Draft"
            : $"Agent Draft · {graph.Brief.Goal.Trim()}";
        if (title.Length > 96) title = title[..96].TrimEnd();
        var sequence = sourceSequence with
        {
            Id = Guid.NewGuid(),
            Name = title,
            Revision = 0,
            Status = SequenceStatus.Draft,
            ParentSequenceId = sourceSequence.Id,
            Settings = working.Sequence,
            Tracks = working.Tracks,
            MediaClips = working.MediaClips,
            SubtitleClips = working.SubtitleClips,
            TextClips = working.TextClips,
            Transitions = working.Transitions,
            Markers = working.Markers,
            InPoint = working.InPoint,
            OutPoint = working.OutPoint
        };
        var draft = new NativeDraftCompilation(sequence, []);
        return new MontageGraphCompilationResult(
            draft, [], ranges,
            CreateReceipts(graph, sourceSequence, sequence, decisions, ranges),
            CreateDiff(sourceSequence, sequence, ranges));
    }

    private static ImmutableArray<TimeRange> NormalizeRanges(
        IEnumerable<TimeRange> source,
        FrameRate frameRate,
        TimelineTime duration)
    {
        var snapped = source.Select(item =>
            {
                var start = item.Start.SnapToFrame(frameRate);
                var end = item.End.SnapToFrame(frameRate);
                if (start < TimelineTime.Zero) start = TimelineTime.Zero;
                if (end > duration) end = duration;
                return end > start ? new TimeRange(start, end - start) : (TimeRange?)null;
            })
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .OrderBy(item => item.Start)
            .ToArray();
        if (snapped.Length == 0) return [];
        var result = ImmutableArray.CreateBuilder<TimeRange>();
        var current = snapped[0];
        foreach (var next in snapped.Skip(1))
        {
            if (next.Start <= current.End)
            {
                var end = next.End > current.End ? next.End : current.End;
                current = new TimeRange(current.Start, end - current.Start);
                continue;
            }
            result.Add(current);
            current = next;
        }
        result.Add(current);
        return result.ToImmutable();
    }

    private static ImmutableArray<EditDecision> MergeDecisions(
        ImmutableArray<EditDecision> graph,
        ImmutableArray<DraftPatch> patches)
    {
        var result = graph.ToDictionary(item => item.Id);
        foreach (var decision in (patches.IsDefault ? [] : patches)
                     .OrderBy(item => item.Order)
                     .SelectMany(item => item.Decisions))
            result[decision.Id] = decision;
        return result.Values.OrderBy(item => item.Order).ThenBy(item => item.Id).ToImmutableArray();
    }

    private static ImmutableArray<DraftCommandReceipt> CreateReceipts(
        MontageGraph graph,
        SequenceState source,
        SequenceState draft,
        ImmutableArray<EditDecision> decisions,
        ImmutableArray<TimeRange> ranges)
    {
        var before = Fingerprint(source);
        var after = Fingerprint(draft);
        var evidence = graph.Fingerprint();
        return ranges.Select((range, order) => new DraftCommandReceipt(
                Guid.NewGuid(), graph.TaskId, source.Id, source.Revision, draft.Id,
                decisions.Length > order ? decisions[order].Kind.ToString() : "TimelineTransform",
                order, range, evidence, before, after, DateTimeOffset.UtcNow))
            .ToImmutableArray();
    }

    private static DraftTimelineDiff CreateDiff(
        SequenceState source,
        SequenceState draft,
        ImmutableArray<TimeRange> removedRanges)
        => new(
            source.Id, draft.Id, removedRanges,
            source.MediaClips.Length, draft.MediaClips.Length,
            source.SubtitleClips.Length, draft.SubtitleClips.Length,
            source.Duration, draft.Duration);

    private static string Fingerprint(SequenceState sequence)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(sequence))));

    private static double ReadDouble(
        IEnumerable<EditDecision> decisions,
        string key,
        double fallback,
        double minimum,
        double maximum)
    {
        var value = decisions
            .Select(item => item.Parameters.TryGetValue(key, out var raw) ? raw : null)
            .LastOrDefault(item => item is not null);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

}
