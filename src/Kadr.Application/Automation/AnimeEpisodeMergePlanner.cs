using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

public sealed class AnimeEpisodeMergePlanner
{
    private static readonly Regex[] EpisodePatterns =
    [
        new(@"(?i)\bS\d{1,2}E(?<number>\d{1,4})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?i)\b(?:episode|ep|серия|эпизод)[\s._-]*(?<number>\d{1,4})\b", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        new(@"(?<!\d)(?<number>\d{1,3})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant)
    ];

    public MontagePlan CreatePlan(
        ProjectState project,
        MontageRequest request,
        ImmutableDictionary<Guid, MediaAnalysisManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (request.Preset?.Recipe != AutomationRecipeKind.MergeEpisodes)
            throw new InvalidOperationException("Для объединения серий нужен соответствующий сценарий монтажа.");

        var sourceIds = request.Scope.SourceIds
            .Where(project.Sources.ContainsKey)
            .Distinct()
            .ToImmutableArray();
        if (sourceIds.IsDefaultOrEmpty)
            throw new InvalidOperationException("Для объединения не выбраны видеоисходники.");

        var structural = manifests.Values
            .Where(item => sourceIds.Contains(item.SourceId))
            .SelectMany(item => item.StructuralSegments.IsDefault ? [] : item.StructuralSegments)
            .OrderBy(item => item.SourceId)
            .ThenBy(item => item.SourceRange.Start)
            .ToImmutableArray();
        var decisions = CreateDecisions(project, request.Preset, sourceIds, structural);
        var now = DateTimeOffset.UtcNow;
        var dependencies = new AutomationDependencyStamp(
            project.Id,
            project.ActiveSequenceId,
            project.ActiveSequence?.Revision,
            sourceIds.ToImmutableDictionary(id => id, id => MontagePlanValidator.StableFingerprint(project.Sources[id])),
            manifests.Values.Select(item => item.PipelineVersion).FirstOrDefault() ?? string.Empty,
            string.Join(", ", manifests.Values.Select(item => item.Model).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct()),
            request.Profile.Id,
            request.Profile.Version);
        var shell = new MontagePlan(
            Guid.NewGuid(),
            request.Id,
            "Аниме — объединённые серии",
            "ИИ определил структуру серий и подготовил монтаж без повторяющихся служебных блоков.",
            MontagePlanStatus.Draft,
            MontageTargetFormat.Source,
            TimelineTime.FromSeconds(1),
            TimelineTime.FromSeconds(1),
            TimelineTime.FromSeconds(1),
            request.Profile,
            dependencies,
            request.Constraints,
            [],
            [],
            now,
            now,
            request.Preset,
            decisions,
            structural);
        return Rebuild(project, shell);
    }

    public MontagePlan ResolveDecision(
        ProjectState project,
        MontagePlan plan,
        Guid decisionId,
        string answer,
        TimelineTime? resolvedTime = null)
    {
        var decision = plan.Decisions.FirstOrDefault(item => item.Id == decisionId)
            ?? throw new InvalidOperationException("Вопрос плана не найден.");
        if (decision.IsResolved)
            return plan;
        if (!decision.Options.IsDefaultOrEmpty && decision.Options.All(item => item.Id != answer))
            throw new InvalidOperationException("Выбран недопустимый вариант ответа.");
        if (decision.Kind is MontageDecisionKind.SegmentStart or MontageDecisionKind.SegmentEnd && resolvedTime is null)
            throw new InvalidOperationException("Для уточнения границы требуется точное время кадра.");

        var structural = plan.StructuralSegments.IsDefault
            ? ImmutableArray<StructuralSegment>.Empty
            : plan.StructuralSegments;
        if (decision.SegmentId is { } segmentId)
        {
            structural = structural.Select(segment => segment.Id == segmentId
                ? ApplyDecision(segment, decision, answer, resolvedTime)
                : segment).ToImmutableArray();
        }

        var updated = decision with
        {
            Status = MontageDecisionStatus.Resolved,
            Answer = answer,
            ResolvedTime = resolvedTime
        };
        return Rebuild(project, plan with
        {
            StructuralSegments = structural,
            Decisions = plan.Decisions.Select(item => item.Id == decisionId ? updated : item).ToImmutableArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static StructuralSegment ApplyDecision(
        StructuralSegment segment,
        MontageDecision decision,
        string answer,
        TimelineTime? resolvedTime)
    {
        if (decision.Kind == MontageDecisionKind.SegmentClassification)
        {
            return segment with
            {
                Disposition = answer == "remove"
                    ? StructuralSegmentDisposition.Remove
                    : StructuralSegmentDisposition.Retain
            };
        }

        if (resolvedTime is not { } time)
            return segment;
        var evidence = ImmutableArray.Create(new AnalysisEvidence(
            MontageEvidenceKind.UserAnnotation,
            "Граница подтверждена пользователем покадрово.",
            time.ToString()));
        if (decision.Kind == MontageDecisionKind.SegmentStart)
        {
            if (time < TimelineTime.Zero || time >= segment.SourceRange.End)
                throw new InvalidOperationException("Начальная граница выходит за диапазон блока.");
            return segment with
            {
                SourceRange = new TimeRange(time, segment.SourceRange.End - time),
                StartBoundary = new ResolvedBoundary(
                    segment.StartBoundary.ProposedTime, time, BoundaryResolutionStatus.UserConfirmed,
                    BoundaryPrecision.ExactPresentationTimestamp, 1, evidence)
            };
        }

        if (time <= segment.SourceRange.Start)
            throw new InvalidOperationException("Конечная граница выходит за диапазон блока.");
        return segment with
        {
            SourceRange = new TimeRange(segment.SourceRange.Start, time - segment.SourceRange.Start),
            EndBoundary = new ResolvedBoundary(
                segment.EndBoundary.ProposedTime, time, BoundaryResolutionStatus.UserConfirmed,
                BoundaryPrecision.ExactPresentationTimestamp, 1, evidence)
        };
    }

    private static MontagePlan Rebuild(ProjectState project, MontagePlan plan)
    {
        var orderDecision = plan.Decisions.FirstOrDefault(item => item.Kind == MontageDecisionKind.SourceOrder);
        var sourceOrder = ParseGuidList(orderDecision?.Answer)
            .Where(project.Sources.ContainsKey)
            .ToImmutableArray();
        if (sourceOrder.IsDefaultOrEmpty)
            sourceOrder = plan.Dependencies.SourceFingerprints.Keys.Order().ToImmutableArray();

        var openingDecision = plan.Decisions.FirstOrDefault(item => item.Kind == MontageDecisionKind.OpeningSelection);
        var openingId = ParseDecisionGuid(openingDecision?.Answer);
        var structural = plan.StructuralSegments.IsDefault
            ? ImmutableArray<StructuralSegment>.Empty
            : plan.StructuralSegments;
        var opening = openingId is { } id
            ? structural.FirstOrDefault(item => item.Id == id && item.Kind == StructuralSegmentKind.Opening)
            : null;
        var items = ImmutableArray.CreateBuilder<MontagePlanItem>();
        var oldItems = plan.Items.IsDefault ? ImmutableArray<MontagePlanItem>.Empty : plan.Items;
        if (opening is not null)
            items.Add(CreateItem(opening.SourceId, opening.SourceRange, MontageRole.Opening,
                "Единственный опенинг выбран ИИ по совокупности смысловых и технических доказательств.",
                opening.Confidence, opening.Evidence, oldItems));

        foreach (var sourceId in sourceOrder)
        {
            if (!project.Sources.TryGetValue(sourceId, out var source)) continue;
            var removals = structural
                .Where(item => item.SourceId == sourceId &&
                               (item.Kind == StructuralSegmentKind.Opening ||
                                item.Disposition == StructuralSegmentDisposition.Remove))
                .Select(item => item.SourceRange)
                .OrderBy(item => item.Start)
                .ToArray();
            foreach (var retained in Complement(source.Duration, removals))
            {
                items.Add(CreateItem(
                    sourceId,
                    retained,
                    MontageRole.Development,
                    $"Сюжет серии «{source.Name}» вне подтверждённых служебных блоков.",
                    1,
                    [new AnalysisEvidence(MontageEvidenceKind.Technical,
                        "Диапазон сохранён как дополнение к подтверждённым исключениям.", "anime:retained")],
                    oldItems));
            }
        }

        var normalized = items.Select((item, index) => item with { Order = index }).ToImmutableArray();
        var duration = normalized.IsDefaultOrEmpty
            ? TimelineTime.FromSeconds(1)
            : new TimelineTime(normalized.Sum(item => item.SourceRange.Duration.Ticks));
        var pending = plan.Decisions.Any(item => !item.IsResolved);
        var warnings = pending
            ? ImmutableArray.Create("Ответьте на вопросы о неоднозначных фрагментах перед созданием черновика.")
            : ImmutableArray<string>.Empty;
        return plan with
        {
            Status = pending ? MontagePlanStatus.NeedsInput : MontagePlanStatus.Ready,
            MinimumDuration = TimelineTime.FromSeconds(1),
            TargetDuration = duration,
            MaximumDuration = duration,
            Items = normalized,
            Warnings = warnings,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static MontagePlanItem CreateItem(
        Guid sourceId,
        TimeRange range,
        MontageRole role,
        string reason,
        double confidence,
        ImmutableArray<AnalysisEvidence> evidence,
        ImmutableArray<MontagePlanItem> previous)
    {
        var old = previous.FirstOrDefault(item =>
            item.SourceId == sourceId && item.SourceRange == range && item.Role == role);
        return new MontagePlanItem(
            old?.Id ?? Guid.NewGuid(), sourceId, range, role, 0, reason,
            Math.Clamp(confidence, 0, 1), evidence.IsDefaultOrEmpty
                ? [new AnalysisEvidence(MontageEvidenceKind.Technical, reason, "anime:plan")]
                : evidence,
            old?.IsLocked ?? false,
            TransitionAfter: null,
            IncludeSubtitles: false);
    }

    private static ImmutableArray<TimeRange> Complement(
        TimelineTime duration,
        IReadOnlyList<TimeRange> ranges)
    {
        var retained = ImmutableArray.CreateBuilder<TimeRange>();
        var cursor = TimelineTime.Zero;
        foreach (var range in ranges)
        {
            var start = range.Start >= cursor ? range.Start : cursor;
            var end = range.End <= duration ? range.End : duration;
            if (start > cursor)
                retained.Add(new TimeRange(cursor, start - cursor));
            if (end > cursor) cursor = end;
            if (cursor >= duration) break;
        }
        if (cursor < duration)
            retained.Add(new TimeRange(cursor, duration - cursor));
        return retained.Where(item => item.Duration.TotalSeconds >= 0.05).ToImmutableArray();
    }

    private static ImmutableArray<MontageDecision> CreateDecisions(
        ProjectState project,
        AutomationPreset preset,
        ImmutableArray<Guid> sourceIds,
        ImmutableArray<StructuralSegment> structural)
    {
        var decisions = ImmutableArray.CreateBuilder<MontageDecision>();
        var ordered = InferOrder(project, sourceIds, out var orderCertain);
        var selected = sourceIds;
        var orderOptions = ImmutableArray.Create(
            new MontageDecisionOption(FormatGuidList(ordered), "Порядок, предложенный ИИ",
                string.Join(" → ", ordered.Select(id => project.Sources[id].Name))),
            new MontageDecisionOption(FormatGuidList(selected), "Порядок выбора",
                string.Join(" → ", selected.Select(id => project.Sources[id].Name))));
        decisions.Add(new MontageDecision(
            Guid.NewGuid(), MontageDecisionKind.SourceOrder,
            "Проверьте порядок серий.", orderOptions,
            orderCertain || ordered.SequenceEqual(selected) ? MontageDecisionStatus.Resolved : MontageDecisionStatus.Pending,
            FormatGuidList(ordered)));

        var openings = structural.Where(item => item.Kind == StructuralSegmentKind.Opening)
            .OrderByDescending(OpeningScore)
            .ThenBy(item => ordered.IndexOf(item.SourceId))
            .ToArray();
        if (openings.Length == 0)
        {
            decisions.Add(new MontageDecision(
                Guid.NewGuid(), MontageDecisionKind.OpeningSelection,
                "ИИ не нашёл подтверждённый опенинг. Уточните его границы в исходнике и повторите подготовку.",
                []));
        }
        else
        {
            var options = openings.Select(item => new MontageDecisionOption(
                item.Id.ToString("N"),
                project.Sources[item.SourceId].Name,
                $"{FormatTime(item.SourceRange.Start)}–{FormatTime(item.SourceRange.End)} · {item.Confidence:P0}"))
                .ToImmutableArray();
            var certain = openings.Length == 1 || OpeningScore(openings[0]) - OpeningScore(openings[1]) >= 0.08;
            decisions.Add(new MontageDecision(
                Guid.NewGuid(), MontageDecisionKind.OpeningSelection,
                "Какой найденный опенинг оставить единственным в начале?", options,
                certain ? MontageDecisionStatus.Resolved : MontageDecisionStatus.Pending,
                openings[0].Id.ToString("N"), SegmentId: openings[0].Id));
        }

        foreach (var segment in structural)
        {
            var mayBeRemoved = segment.Kind is not (StructuralSegmentKind.Story or StructuralSegmentKind.PostCreditsStory);
            if (segment.Disposition == StructuralSegmentDisposition.NeedsInput || segment.Confidence < preset.RequiredConfidence)
            {
                decisions.Add(new MontageDecision(
                    Guid.NewGuid(), MontageDecisionKind.SegmentClassification,
                    $"Фрагмент {FormatTime(segment.SourceRange.Start)}–{FormatTime(segment.SourceRange.End)}: сохранить как сюжет или удалить как служебный блок?",
                    [
                        new MontageDecisionOption("retain", "Сохранить", "Фрагмент останется внутри серии."),
                        new MontageDecisionOption("remove", "Удалить", "Фрагмент будет исключён из объединённого монтажа.")
                    ],
                    SourceId: segment.SourceId,
                    SegmentId: segment.Id));
            }
            if (!mayBeRemoved) continue;
            if (!segment.StartBoundary.IsConfirmed)
                decisions.Add(BoundaryDecision(segment, isStart: true));
            if (!segment.EndBoundary.IsConfirmed)
                decisions.Add(BoundaryDecision(segment, isStart: false));
        }
        return decisions.ToImmutable();
    }

    private static MontageDecision BoundaryDecision(StructuralSegment segment, bool isStart)
    {
        var boundary = isStart ? segment.StartBoundary : segment.EndBoundary;
        return new MontageDecision(
            Guid.NewGuid(),
            isStart ? MontageDecisionKind.SegmentStart : MontageDecisionKind.SegmentEnd,
            $"Уточните {(isStart ? "начало" : "конец")} блока {segment.Kind} покадрово.",
            [],
            SourceId: segment.SourceId,
            SegmentId: segment.Id,
            SuggestedTime: boundary.ResolvedTime);
    }

    private static ImmutableArray<Guid> InferOrder(
        ProjectState project,
        ImmutableArray<Guid> sourceIds,
        out bool certain)
    {
        var numbered = sourceIds.Select(id => (Id: id, Number: TryEpisodeNumber(project.Sources[id].Name))).ToArray();
        certain = numbered.All(item => item.Number.HasValue) &&
                  numbered.Select(item => item.Number!.Value).Distinct().Count() == numbered.Length;
        return certain
            ? numbered.OrderBy(item => item.Number).Select(item => item.Id).ToImmutableArray()
            : sourceIds.OrderBy(id => project.Sources[id].Name, StringComparer.CurrentCultureIgnoreCase).ToImmutableArray();
    }

    private static int? TryEpisodeNumber(string name)
    {
        foreach (var pattern in EpisodePatterns)
        {
            var matches = pattern.Matches(Path.GetFileNameWithoutExtension(name));
            foreach (Match match in matches.Cast<Match>().Reverse())
            {
                if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    continue;
                if (number is 720 or 1080 or 2160) continue;
                return number;
            }
        }
        return null;
    }

    private static double OpeningScore(StructuralSegment segment)
        => segment.Confidence + (segment.HasConfirmedBoundaries ? 0.1 : 0);

    private static string FormatGuidList(IEnumerable<Guid> values)
        => string.Join(",", values.Select(item => item.ToString("N")));

    private static IEnumerable<Guid> ParseGuidList(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => Guid.TryParseExact(item, "N", out var id) ? id : Guid.Empty)
            .Where(item => item != Guid.Empty);

    private static Guid? ParseDecisionGuid(string? value)
        => Guid.TryParseExact(value, "N", out var id) ? id : null;

    private static string FormatTime(TimelineTime time)
        => TimeSpan.FromSeconds(time.TotalSeconds).ToString(time.TotalSeconds >= 3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.InvariantCulture);
}
