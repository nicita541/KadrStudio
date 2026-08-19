using System.Collections.Immutable;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Automation;

public sealed class MontagePlanValidator : IMontagePlanValidator
{
    public MontagePlanValidationResult Validate(ProjectState project, MontagePlan plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<ValidationError>();
        var warnings = ImmutableArray.CreateBuilder<string>();

        if (plan.Id == Guid.Empty || plan.Dependencies.ProjectId != project.Id)
            errors.Add(new("montage.identity", "План относится к другому проекту или не имеет ID.", plan.Id));
        if (plan.Items.IsDefaultOrEmpty)
            errors.Add(new("montage.empty", "План монтажа не содержит фрагментов.", plan.Id));
        if (plan.MinimumDuration <= TimelineTime.Zero || plan.MaximumDuration < plan.MinimumDuration ||
            plan.TargetDuration < plan.MinimumDuration || plan.TargetDuration > plan.MaximumDuration)
            errors.Add(new("montage.duration-settings", "Целевая длительность плана некорректна.", plan.Id));
        if (!plan.ProfileSnapshot.Id.Equals(plan.Dependencies.ProfileId, StringComparison.OrdinalIgnoreCase) ||
            plan.ProfileSnapshot.Version != plan.Dependencies.ProfileVersion)
            errors.Add(new("montage.profile", "Версия профиля плана не совпадает с зависимостями.", plan.Id));

        if (plan.Dependencies.InputSequenceId is { } sequenceId)
        {
            var sequence = project.FindSequence(sequenceId);
            if (sequence is null || sequence.Revision != plan.Dependencies.InputSequenceRevision)
                errors.Add(new("montage.sequence-stale", "Входной вариант монтажа изменился после создания плана.", sequenceId));
        }

        foreach (var dependency in plan.Dependencies.SourceFingerprints)
        {
            if (!project.Sources.TryGetValue(dependency.Key, out var source))
            {
                errors.Add(new("montage.source-missing", "Один из исходников плана удалён.", dependency.Key));
                continue;
            }
            if (source.OnlineState != MediaOnlineState.Online)
                errors.Add(new("montage.source-offline", $"Исходник «{source.Name}» недоступен.", source.Id));
            if (!StableFingerprint(source).Equals(dependency.Value, StringComparison.Ordinal))
                errors.Add(new("montage.source-stale", $"Исходник «{source.Name}» изменился после анализа.", source.Id));
        }

        foreach (var annotation in project.SourceAnnotations.Where(item =>
                     plan.Dependencies.SourceFingerprints.ContainsKey(item.SourceId)))
        {
            if (!plan.Constraints.Any(constraint =>
                    constraint.Id == annotation.Id && constraint.SourceId == annotation.SourceId &&
                    constraint.Kind == annotation.Kind && constraint.SourceRange == annotation.SourceRange &&
                    string.Equals(constraint.Note, annotation.Note, StringComparison.Ordinal)))
                errors.Add(new(
                    "montage.annotations-stale",
                    "Указания пользователя изменились после создания плана; обновите план перед сборкой.",
                    annotation.Id));
        }

        var orders = new HashSet<int>();
        var itemIds = new HashSet<Guid>();
        foreach (var item in plan.Items)
        {
            if (!orders.Add(item.Order))
                errors.Add(new("montage.order", "Порядок пунктов плана должен быть уникальным.", item.Id));
            if (item.Id == Guid.Empty || !itemIds.Add(item.Id))
                errors.Add(new("montage.item-id", "Идентификаторы пунктов плана должны быть уникальными.", item.Id));
            if (!project.Sources.TryGetValue(item.SourceId, out var source))
            {
                errors.Add(new("montage.item-source", "Пункт плана ссылается на неизвестный исходник.", item.Id));
                continue;
            }
            if (source.Kind is not (MediaKind.Video or MediaKind.Image))
                errors.Add(new("montage.item-kind", "В rough cut можно добавлять только видео и изображения.", item.Id));
            if (item.SourceRange.Start < TimelineTime.Zero || item.SourceRange.Duration <= TimelineTime.Zero ||
                item.SourceRange.End > source.Duration)
                errors.Add(new("montage.item-range", "Пункт плана выходит за границы исходника.", item.Id));
            if (item.Confidence is < 0 or > 1 || item.Volume is < 0 or > 2)
                errors.Add(new("montage.item-parameters", "Параметры пункта плана некорректны.", item.Id));

            var isRequired = plan.Constraints.Any(constraint =>
                constraint.Kind == SourceAnnotationKind.Required && constraint.SourceId == item.SourceId &&
                Contains(item.SourceRange, constraint.SourceRange));
            if (item.Evidence.IsDefaultOrEmpty && !isRequired)
                errors.Add(new("montage.item-evidence", "Автоматически выбранный фрагмент не имеет доказательства.", item.Id));

            foreach (var excluded in plan.Constraints.Where(constraint =>
                         constraint.Kind == SourceAnnotationKind.Excluded && constraint.SourceId == item.SourceId))
                if (Intersects(item.SourceRange, excluded.SourceRange))
                    errors.Add(new("montage.excluded", "План использует запрещённый пользователем диапазон.", item.Id));
        }

        foreach (var required in plan.Constraints.Where(item => item.Kind == SourceAnnotationKind.Required && item.IsHard))
        {
            if (!plan.Items.Any(item => item.SourceId == required.SourceId && Contains(item.SourceRange, required.SourceRange)))
                errors.Add(new("montage.required", "Обязательный диапазон отсутствует в плане.", required.Id));
        }

        if (plan.PresetSnapshot?.Recipe == AutomationRecipeKind.MergeEpisodes)
            ValidateEpisodeMerge(project, plan, errors);

        if (plan.Duration > plan.MaximumDuration)
            errors.Add(new("montage.too-long", "План длиннее максимальной длительности; обязательные фрагменты не были скрыто удалены.", plan.Id));
        else if (plan.Duration < plan.MinimumDuration)
            warnings.Add("План короче выбранной минимальной длительности.");
        if (plan.Items.Any(item => item.Confidence < 0.6))
            warnings.Add("В плане есть фрагменты с низкой уверенностью — проверьте их перед сборкой.");

        return new MontagePlanValidationResult(
            errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors),
            warnings.ToImmutable());
    }

    public static string StableFingerprint(MediaSource source)
        => !string.IsNullOrWhiteSpace(source.VerifiedFingerprint) ? source.VerifiedFingerprint :
            !string.IsNullOrWhiteSpace(source.FastFingerprint) ? source.FastFingerprint :
            !string.IsNullOrWhiteSpace(source.Fingerprint) ? source.Fingerprint :
            $"{source.FileSize:x}-{source.LastWriteUtcTicks:x}";

    private static bool Contains(TimeRange outer, TimeRange inner)
        => outer.Start <= inner.Start && outer.End >= inner.End;

    private static bool Intersects(TimeRange left, TimeRange right)
        => left.Start < right.End && left.End > right.Start;

    private static void ValidateEpisodeMerge(
        ProjectState project,
        MontagePlan plan,
        ICollection<ValidationError> errors)
    {
        var pending = plan.Decisions.IsDefault
            ? []
            : plan.Decisions.Where(item => !item.IsResolved).ToArray();
        if (pending.Length > 0 || plan.Status == MontagePlanStatus.NeedsInput)
            errors.Add(new(
                "montage.needs-input",
                "Перед созданием черновика ответьте на все вопросы ИИ-плана.",
                plan.Id));

        var openings = plan.Items.Where(item => item.Role == MontageRole.Opening).ToArray();
        if (openings.Length != 1 || plan.Items.OrderBy(item => item.Order).FirstOrDefault()?.Role != MontageRole.Opening)
            errors.Add(new(
                "anime.opening",
                "Объединённый монтаж должен начинаться ровно с одного подтверждённого опенинга.",
                plan.Id));
        if (plan.Items.Any(item => item.TransitionAfter is not null))
            errors.Add(new(
                "anime.transitions",
                "Серии в этом сценарии соединяются прямыми склейками без переходов.",
                plan.Id));

        var structural = plan.StructuralSegments.IsDefault
            ? ImmutableArray<StructuralSegment>.Empty
            : plan.StructuralSegments;
        foreach (var segment in structural.Where(item =>
                     item.Kind == StructuralSegmentKind.Opening ||
                     item.Disposition == StructuralSegmentDisposition.Remove))
        {
            if (!segment.HasConfirmedBoundaries)
                errors.Add(new(
                    "anime.boundary",
                    "Удаляемый блок не имеет подтверждённых покадровых границ.",
                    segment.Id));
            foreach (var item in plan.Items.Where(item =>
                         item.SourceId == segment.SourceId && item.Role != MontageRole.Opening &&
                         Intersects(item.SourceRange, segment.SourceRange)))
                errors.Add(new(
                    "anime.removed-overlap",
                    "Сюжетный диапазон пересекается с подтверждённым служебным блоком.",
                    item.Id));
        }

        foreach (var sourceId in plan.Dependencies.SourceFingerprints.Keys)
        {
            if (!project.Sources.ContainsKey(sourceId)) continue;
            if (!plan.Items.Any(item => item.SourceId == sourceId && item.Role != MontageRole.Opening))
                errors.Add(new(
                    "anime.episode-missing",
                    $"Серия «{project.Sources[sourceId].Name}» не представлена в сюжетной части плана.",
                    sourceId));
        }
    }
}
