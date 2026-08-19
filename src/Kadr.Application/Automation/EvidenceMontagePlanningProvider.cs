using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

/// <summary>
/// Safe local baseline used when a model is unavailable and as the bounded
/// candidate set supplied to an LLM. It can only select existing analyzed ranges.
/// </summary>
public sealed class EvidenceMontagePlanningProvider : IMontagePlanningProvider
{
    public Task<MontagePlan> CreatePlanAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Create(context));
    }

    public Task<MontagePlan> RevisePlanAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = context.PreviousPlan ?? throw new InvalidOperationException("Исходный план для корректировки отсутствует.");
        var correction = context.RevisionRequest.ToLowerInvariant();
        var target = previous.TargetDuration;
        if (correction.Contains("короч") || correction.Contains("быстр"))
            target = new TimelineTime(Math.Max(previous.MinimumDuration.Ticks, target.Ticks * 3 / 4));
        else if (correction.Contains("длин") || correction.Contains("подроб"))
            target = new TimelineTime(Math.Min(previous.MaximumDuration.Ticks, target.Ticks * 5 / 4));

        var request = context.Request with { TargetDuration = target };
        var generated = Create(context with { Request = request });
        var locked = previous.Items.Where(item => item.IsLocked).ToDictionary(item => item.Id);
        var unlocked = generated.Items
            .Where(item => !locked.ContainsKey(item.Id))
            .Where(item => !locked.Values.Any(lockedItem => lockedItem.SourceId == item.SourceId &&
                                                            Intersects(lockedItem.SourceRange, item.SourceRange)))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.SourceId)
            .ThenBy(item => item.SourceRange.Start)
            .ToList();
        var usedOrders = locked.Values.Select(item => item.Order).ToHashSet();
        var nextOrder = 0;
        var normalized = unlocked.Select(item =>
            {
                while (usedOrders.Contains(nextOrder)) nextOrder++;
                var updated = item with { Order = nextOrder };
                usedOrders.Add(nextOrder++);
                return updated;
            })
            .Concat(locked.Values)
            .OrderBy(item => item.Order)
            .ToImmutableArray();
        return Task.FromResult(generated with
        {
            Id = previous.Id,
            CreatedAt = previous.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Summary = $"{generated.Summary} Корректировка: {context.RevisionRequest}",
            Items = normalized
        });
    }

    private static MontagePlan Create(MontagePlanningContext context)
    {
        var request = context.Request;
        var selectedSourceIds = ResolveSourceIds(context.Project, request.Scope).ToHashSet();
        var allowedRanges = ResolveAllowedSourceRanges(context.Project, request.Scope);
        var excluded = request.Constraints.Where(item => item.Kind == SourceAnnotationKind.Excluded).ToArray();
        var required = request.Constraints.Where(item => item.Kind == SourceAnnotationKind.Required).ToArray();
        var analyzedSegments = context.Manifests.Values
            .Where(manifest => selectedSourceIds.Contains(manifest.SourceId))
            .SelectMany(manifest => manifest.Segments);
        var noteSegments = request.Constraints
            .Where(item => item.Kind == SourceAnnotationKind.Note && selectedSourceIds.Contains(item.SourceId))
            .Select(note => new AnalysisSegment(
                note.Id, note.SourceId, note.SourceRange, 0.35, 0.35, 0,
                string.Empty,
                ImmutableDictionary<string, double>.Empty.Add("user-note", 1),
                1,
                [new AnalysisEvidence(MontageEvidenceKind.UserAnnotation,
                    string.IsNullOrWhiteSpace(note.Note) ? "Заметка пользователя" : note.Note,
                    note.Id.ToString("N"))]));
        var candidates = analyzedSegments
            .Concat(noteSegments)
            .Select(segment => ClampToScope(segment, allowedRanges))
            .Where(segment => segment is not null)
            .Select(segment => segment!)
            .Where(segment => segment.SourceRange.Duration >= TimelineTime.FromSeconds(
                Math.Min(request.Profile.MinimumSegmentSeconds, 1)))
            .Where(segment => !excluded.Any(item => item.SourceId == segment.SourceId &&
                                                     Intersects(item.SourceRange, segment.SourceRange)))
            .Select(segment => new ScoredSegment(segment, Score(segment, request.Profile)))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Segment.Confidence)
            .ThenBy(item => item.Segment.SourceId)
            .ThenBy(item => item.Segment.SourceRange.Start)
            .ToList();

        var chosen = new List<MontagePlanItem>();
        foreach (var constraint in required.OrderBy(item => item.SourceId).ThenBy(item => item.SourceRange.Start))
        {
            chosen.Add(new MontagePlanItem(
                constraint.Id,
                constraint.SourceId,
                constraint.SourceRange,
                MontageRole.Development,
                chosen.Count,
                string.IsNullOrWhiteSpace(constraint.Note) ? "Обязательный момент пользователя" : constraint.Note,
                1,
                [new AnalysisEvidence(MontageEvidenceKind.UserAnnotation, constraint.Note, constraint.Id.ToString("N"))],
                IsLocked: true));
        }

        var usedDuration = chosen.Sum(item => item.SourceRange.Duration.Ticks);
        foreach (var candidate in candidates)
        {
            if (chosen.Any(item => item.SourceId == candidate.Segment.SourceId &&
                                   Intersects(item.SourceRange, candidate.Segment.SourceRange)))
                continue;
            if (usedDuration >= request.TargetDuration.Ticks && chosen.Count > 0) break;
            var range = ClampDuration(candidate.Segment.SourceRange, request.Profile.MaximumSegmentSeconds);
            chosen.Add(new MontagePlanItem(
                candidate.Segment.Id,
                candidate.Segment.SourceId,
                range,
                MontageRole.Development,
                chosen.Count,
                Describe(candidate.Segment),
                candidate.Segment.Confidence,
                candidate.Segment.Evidence));
            usedDuration += range.Duration.Ticks;
        }

        chosen = chosen
            .OrderBy(item => item.SourceId)
            .ThenBy(item => item.SourceRange.Start)
            .Select((item, index) => item with
            {
                Order = index,
                Role = RoleFor(index, chosen.Count)
            })
            .ToList();

        var sourceFingerprints = selectedSourceIds
            .Where(context.Project.Sources.ContainsKey)
            .ToImmutableDictionary(
                id => id,
                id => MontagePlanValidator.StableFingerprint(context.Project.Sources[id]));
        var inputSequence = request.Scope.SequenceId is { } sequenceId
            ? context.Project.FindSequence(sequenceId)
            : null;
        var model = context.Manifests.Values.Select(item => item.Model).FirstOrDefault() ?? "deterministic";
        var pipeline = context.Manifests.Values.Select(item => item.PipelineVersion).FirstOrDefault() ?? "analysis-v1";
        var dependencies = new AutomationDependencyStamp(
            context.Project.Id,
            inputSequence?.Id,
            inputSequence?.Revision,
            sourceFingerprints,
            pipeline,
            model,
            request.Profile.Id,
            request.Profile.Version);
        var now = DateTimeOffset.UtcNow;
        return new MontagePlan(
            Guid.NewGuid(),
            request.Id,
            request.TargetFormat == MontageTargetFormat.Shorts ? "Вертикальный монтаж — черновик ИИ" : "Монтаж — черновик ИИ",
            "План построен только из подтверждённых диапазонов анализа и обязательных меток.",
            MontagePlanStatus.Ready,
            request.TargetFormat,
            request.MinimumDuration,
            request.TargetDuration,
            request.MaximumDuration,
            request.Profile,
            dependencies,
            request.Constraints,
            chosen.ToImmutableArray(),
            ImmutableArray<string>.Empty,
            now,
            now);
    }

    private static IEnumerable<Guid> ResolveSourceIds(ProjectState project, MontageScope scope)
    {
        if (!scope.SourceIds.IsDefaultOrEmpty) return scope.SourceIds;
        if (scope.Kind is MontageScopeKind.CurrentSequence or MontageScopeKind.SelectedClips or MontageScopeKind.InOutRange)
        {
            var sequence = scope.SequenceId is { } sequenceId ? project.FindSequence(sequenceId) : project.ActiveSequence;
            var sequenceClips = sequence?.MediaClips ?? project.MediaClips;
            var clips = scope.ClipIds.IsDefaultOrEmpty
                ? sequenceClips
                : sequenceClips.Where(item => scope.ClipIds.Contains(item.Id)).ToImmutableArray();
            if (scope.TimelineRange is { } range)
                clips = clips.Where(item => Intersects(item.Range, range)).ToImmutableArray();
            return clips.Select(item => item.SourceId).Distinct();
        }
        return project.Sources.Keys;
    }

    private static IReadOnlyDictionary<Guid, ImmutableArray<TimeRange>>? ResolveAllowedSourceRanges(
        ProjectState project,
        MontageScope scope)
    {
        if (scope.Kind is MontageScopeKind.MediaLibrary or MontageScopeKind.SelectedSources)
            return null;

        var sequence = scope.SequenceId is { } id ? project.FindSequence(id) : project.ActiveSequence;
        var clips = sequence?.MediaClips ?? project.MediaClips;
        if (!scope.ClipIds.IsDefaultOrEmpty)
            clips = clips.Where(item => scope.ClipIds.Contains(item.Id)).ToImmutableArray();

        var ranges = new List<(Guid SourceId, TimeRange Range)>();
        foreach (var clip in clips)
        {
            var timelineStart = clip.Start;
            var timelineEnd = clip.End;
            if (scope.TimelineRange is { } timelineRange)
            {
                timelineStart = timelineStart >= timelineRange.Start ? timelineStart : timelineRange.Start;
                timelineEnd = timelineEnd <= timelineRange.End ? timelineEnd : timelineRange.End;
                if (timelineEnd <= timelineStart) continue;
            }
            var sourceStart = clip.SourceIn + (timelineStart - clip.Start);
            ranges.Add((clip.SourceId, new TimeRange(sourceStart, timelineEnd - timelineStart)));
        }
        return ranges
            .GroupBy(item => item.SourceId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Range).OrderBy(item => item.Start).ToImmutableArray());
    }

    private static AnalysisSegment? ClampToScope(
        AnalysisSegment segment,
        IReadOnlyDictionary<Guid, ImmutableArray<TimeRange>>? allowedRanges)
    {
        if (allowedRanges is null) return segment;
        if (!allowedRanges.TryGetValue(segment.SourceId, out var ranges)) return null;
        var intersection = ranges
            .Where(range => Intersects(range, segment.SourceRange))
            .Select(range => Intersect(range, segment.SourceRange))
            .OrderByDescending(range => range.Duration)
            .FirstOrDefault();
        return intersection.Duration <= TimelineTime.Zero
            ? null
            : segment with { SourceRange = intersection };
    }

    private static TimeRange Intersect(TimeRange left, TimeRange right)
    {
        var start = left.Start >= right.Start ? left.Start : right.Start;
        var end = left.End <= right.End ? left.End : right.End;
        return new TimeRange(start, end - start);
    }

    private static double Score(AnalysisSegment segment, GameEditingProfile profile)
    {
        var semantic = segment.Tags.Sum(tag =>
            profile.EventWeights.TryGetValue(tag.Key, out var weight) ? tag.Value * weight : tag.Value * 0.2);
        var userSignal = segment.Evidence.Any(item => item.Kind == MontageEvidenceKind.UserAnnotation) ? 0.75 : 0;
        return semantic * 0.55 + segment.MotionScore * 0.18 + segment.LoudnessScore * 0.12 +
               segment.SpeechScore * 0.1 + segment.Confidence * 0.05 + userSignal;
    }

    private static string Describe(AnalysisSegment segment)
    {
        var userNote = segment.Evidence.FirstOrDefault(item => item.Kind == MontageEvidenceKind.UserAnnotation);
        if (userNote is not null) return $"Заметка пользователя: {Trim(userNote.Summary, 100)}";
        var strongest = segment.Tags.OrderByDescending(item => item.Value).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(segment.Transcript))
            return $"Речь: {Trim(segment.Transcript, 100)}";
        return string.IsNullOrWhiteSpace(strongest.Key)
            ? "Технически заметный фрагмент"
            : $"Событие: {strongest.Key}";
    }

    private static TimeRange ClampDuration(TimeRange range, double maximumSeconds)
    {
        var maximum = TimelineTime.FromSeconds(maximumSeconds);
        return range.Duration <= maximum ? range : new TimeRange(range.Start, maximum);
    }

    private static MontageRole RoleFor(int index, int count)
    {
        if (index == 0) return MontageRole.Hook;
        if (index == count - 1) return MontageRole.Ending;
        if (index == 1) return MontageRole.Setup;
        if (index >= Math.Max(2, count - 2)) return MontageRole.Payoff;
        return MontageRole.Development;
    }

    private static string Trim(string value, int length)
        => value.Length <= length ? value : value[..length] + "…";

    private static bool Intersects(TimeRange left, TimeRange right)
        => left.Start < right.End && left.End > right.Start;

    private sealed record ScoredSegment(AnalysisSegment Segment, double Score);
}
