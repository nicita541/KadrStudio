using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

public sealed record InitializeSequenceWorkspaceCommand(string OriginalName = "Исходный монтаж") : IEditCommand
{
    public string Description => "Инициализировать варианты монтажа";
    public ProjectState Apply(ProjectState project) => project.EnsureSequenceContainer(OriginalName);
}

public sealed record UpsertMontagePlanCommand(MontagePlan Plan) : IEditCommand
{
    public string Description => "Сохранить план ИИ-монтажа";

    public ProjectState Apply(ProjectState project)
    {
        if (Plan.Dependencies.ProjectId != project.Id)
            throw new EditRejectedException("План монтажа относится к другому проекту.");
        var plans = project.MontagePlans.Any(item => item.Id == Plan.Id)
            ? project.MontagePlans.Select(item => item.Id == Plan.Id ? Plan : item).ToImmutableArray()
            : project.MontagePlans.Add(Plan);
        return project with { MontagePlans = plans };
    }
}

public sealed record ReplaceAiConversationCommand(AiConversation Conversation) : IEditCommand
{
    public string Description => "Обновить диалог ИИ";

    public ProjectState Apply(ProjectState project)
    {
        ArgumentNullException.ThrowIfNull(Conversation);
        return project with { AiConversation = Conversation };
    }
}

public sealed record DeleteMontagePlanCommand(Guid PlanId) : IEditCommand
{
    public string Description => "Удалить план ИИ-монтажа";

    public ProjectState Apply(ProjectState project)
    {
        if (project.Sequences.Any(item => item.MontagePlanId == PlanId))
            throw new EditRejectedException("Нельзя удалить план, пока существует связанный вариант монтажа.");
        return project with { MontagePlans = project.MontagePlans.Where(item => item.Id != PlanId).ToImmutableArray() };
    }
}

public sealed record UpsertSourceAnnotationCommand(SourceAnnotation Annotation) : IEditCommand
{
    public string Description => "Изменить указание для ИИ";

    public ProjectState Apply(ProjectState project)
    {
        if (!project.Sources.TryGetValue(Annotation.SourceId, out var source))
            throw new EditRejectedException("Исходник для указания ИИ не найден.");
        if (Annotation.SourceRange.Start < TimelineTime.Zero || Annotation.SourceRange.Duration <= TimelineTime.Zero ||
            Annotation.SourceRange.End > source.Duration)
            throw new EditRejectedException("Диапазон указания ИИ выходит за границы исходника.");
        var annotations = project.SourceAnnotations.Any(item => item.Id == Annotation.Id)
            ? project.SourceAnnotations.Select(item => item.Id == Annotation.Id ? Annotation : item).ToImmutableArray()
            : project.SourceAnnotations.Add(Annotation);
        return project with { SourceAnnotations = annotations };
    }
}

public sealed record DeleteSourceAnnotationCommand(Guid AnnotationId) : IEditCommand
{
    public string Description => "Удалить указание для ИИ";
    public ProjectState Apply(ProjectState project)
        => project with
        {
            SourceAnnotations = project.SourceAnnotations.Where(item => item.Id != AnnotationId).ToImmutableArray()
        };
}

public sealed record ReplaceAnalysisReferencesCommand(
    IReadOnlyList<MediaAnalysisReference> References) : IEditCommand
{
    public string Description => "Обновить индекс анализа";

    public ProjectState Apply(ProjectState project)
    {
        var sourceIds = References.Select(item => item.SourceId).ToHashSet();
        if (sourceIds.Any(id => !project.Sources.ContainsKey(id)))
            throw new EditRejectedException("Индекс анализа содержит неизвестный исходник.");
        return project with
        {
            AnalysisReferences = project.AnalysisReferences
                .Where(item => !sourceIds.Contains(item.SourceId))
                .Concat(References)
                .OrderBy(item => item.SourceId)
                .ToImmutableArray()
        };
    }
}

public sealed record CreateSequenceCommand(SequenceState Sequence, bool Activate = true) : IEditCommand
{
    public string Description => "Создать вариант монтажа";

    public ProjectState Apply(ProjectState project)
    {
        var workspace = project.EnsureSequenceContainer();
        if (Sequence.Id == Guid.Empty || workspace.FindSequence(Sequence.Id) is not null)
            throw new EditRejectedException("Идентификатор нового варианта монтажа некорректен или уже используется.");
        if (Sequence.ParentSequenceId is { } parentId && workspace.FindSequence(parentId) is null)
            throw new EditRejectedException("Родительский вариант монтажа не найден.");
        if (Sequence.MontagePlanId is { } planId && workspace.FindMontagePlan(planId) is null)
            throw new EditRejectedException("План нового варианта монтажа не найден.");
        var result = workspace with { Sequences = workspace.Sequences.Add(Sequence) };
        return Activate ? result.ActivateSequence(Sequence.Id) : result;
    }
}

public sealed record ActivateSequenceCommand(Guid SequenceId) : IEditCommand
{
    public string Description => "Переключить вариант монтажа";
    public ProjectState Apply(ProjectState project) => project.EnsureSequenceContainer().ActivateSequence(SequenceId);
}

public sealed record SetSequenceStatusCommand(Guid SequenceId, SequenceStatus Status) : IEditCommand
{
    public string Description => Status == SequenceStatus.Accepted
        ? "Принять вариант монтажа"
        : "Изменить статус варианта монтажа";

    public ProjectState Apply(ProjectState project)
    {
        var workspace = project.EnsureSequenceContainer().SynchronizeActiveSequence();
        if (workspace.FindSequence(SequenceId) is null)
            throw new EditRejectedException("Вариант монтажа не найден.");
        return workspace with
        {
            Sequences = workspace.Sequences.Select(item => item.Id == SequenceId
                ? item with { Status = Status }
                : item).ToImmutableArray()
        };
    }
}

public sealed record DeleteDraftSequenceCommand(Guid SequenceId) : IEditCommand
{
    public string Description => "Удалить черновик ИИ-монтажа";

    public ProjectState Apply(ProjectState project)
    {
        var workspace = project.EnsureSequenceContainer().SynchronizeActiveSequence();
        var sequence = workspace.FindSequence(SequenceId)
            ?? throw new EditRejectedException("Черновик монтажа не найден.");
        if (sequence.Status != SequenceStatus.Draft)
            throw new EditRejectedException("Удалять этой командой можно только черновики.");
        if (workspace.Sequences.Length <= 1)
            throw new EditRejectedException("Нельзя удалить единственную последовательность проекта.");

        if (workspace.ActiveSequenceId == SequenceId)
        {
            var fallback = sequence.ParentSequenceId is { } parentId && workspace.FindSequence(parentId) is not null
                ? parentId
                : workspace.Sequences.First(item => item.Id != SequenceId).Id;
            workspace = workspace.ActivateSequence(fallback);
        }
        return workspace with
        {
            Sequences = workspace.Sequences.Where(item => item.Id != SequenceId).ToImmutableArray()
        };
    }
}
