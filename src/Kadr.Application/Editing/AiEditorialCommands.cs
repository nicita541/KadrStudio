using System.Collections.Immutable;
using KadrStudio.Application.Automation;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Editing;

public sealed record UpsertMediaUnderstandingIndexCommand(MediaUnderstandingIndex Index) : IEditCommand
{
    public string Description => "Сохранить мультимодальный индекс";

    public ProjectState Apply(ProjectState project)
    {
        if (!project.Sources.TryGetValue(Index.SourceId, out var source))
            throw new EditRejectedException("Мультимодальный индекс относится к неизвестному исходнику.");
        if (!Index.SourceFingerprint.Equals(
                MediaSourceFingerprint.Stable(source),
                StringComparison.Ordinal))
            throw new EditRejectedException("Мультимодальный индекс устарел: fingerprint исходника изменился.");

        var indexes = project.UnderstandingIndexes
            .Where(item => item.Id != Index.Id &&
                           !(item.SourceId == Index.SourceId &&
                             item.PipelineVersion == Index.PipelineVersion))
            .Append(Index)
            .OrderBy(item => item.SourceId)
            .ThenBy(item => item.CreatedAt)
            .ToImmutableArray();
        return project with { UnderstandingIndexes = indexes };
    }
}

public sealed record UpsertMontageGraphCommand(MontageGraph Graph) : IEditCommand
{
    public string Description => "Сохранить режиссёрский монтажный граф";

    public ProjectState Apply(ProjectState project)
    {
        var source = project.FindSequence(Graph.SourceSequenceId)
            ?? throw new EditRejectedException("Исходная последовательность монтажного графа не найдена.");
        if (source.Revision != Graph.SourceSequenceRevision)
            throw new EditRejectedException("Монтажный граф относится к устаревшей ревизии последовательности.");
        var graphs = project.MontageGraphs.Any(item => item.Id == Graph.Id)
            ? project.MontageGraphs.Select(item => item.Id == Graph.Id ? Graph : item).ToImmutableArray()
            : project.MontageGraphs.Add(Graph);
        return project with { MontageGraphs = graphs };
    }
}

public sealed record ReplaceDraftPatchesCommand(
    Guid MontageGraphId,
    IReadOnlyList<DraftPatch> Patches) : IEditCommand
{
    public string Description => "Сохранить монтажные проходы";

    public ProjectState Apply(ProjectState project)
    {
        if (!project.MontageGraphs.Any(item => item.Id == MontageGraphId))
            throw new EditRejectedException("Монтажный граф для проходов не найден.");
        if (Patches.Any(item => item.MontageGraphId != MontageGraphId))
            throw new EditRejectedException("Монтажный проход относится к другому графу.");
        return project with
        {
            DraftPatches = project.DraftPatches
                .Where(item => item.MontageGraphId != MontageGraphId)
                .Concat(Patches)
                .OrderBy(item => item.MontageGraphId)
                .ThenBy(item => item.Order)
                .ToImmutableArray()
        };
    }
}

public sealed record UpsertDraftQualityReportCommand(DraftQualityReport Report) : IEditCommand
{
    public string Description => "Сохранить проверку Agent Draft";

    public ProjectState Apply(ProjectState project)
    {
        if (project.FindSequence(Report.DraftSequenceId) is not { Status: SequenceStatus.Draft })
            throw new EditRejectedException("QC-отчёт можно привязать только к существующему Agent Draft.");
        var reports = project.DraftQualityReports.Any(item => item.Id == Report.Id)
            ? project.DraftQualityReports.Select(item => item.Id == Report.Id ? Report : item).ToImmutableArray()
            : project.DraftQualityReports.Add(Report);
        return project with { DraftQualityReports = reports };
    }
}

public sealed record ReplaceDraftCommandReceiptsCommand(
    Guid DraftSequenceId,
    IReadOnlyList<DraftCommandReceipt> Receipts) : IEditCommand
{
    public string Description => "Сохранить receipts native Draft compiler";

    public ProjectState Apply(ProjectState project)
    {
        if (project.FindSequence(DraftSequenceId) is not { Status: SequenceStatus.Draft })
            throw new EditRejectedException("Receipts можно сохранить только для существующего Agent Draft.");
        if (Receipts.Any(item => item.DraftSequenceId != DraftSequenceId))
            throw new EditRejectedException("Receipt относится к другому Draft.");
        return project with
        {
            DraftCommandReceipts = project.DraftCommandReceipts
                .Where(item => item.DraftSequenceId != DraftSequenceId)
                .Concat(Receipts)
                .OrderBy(item => item.DraftSequenceId)
                .ThenBy(item => item.Order)
                .ToImmutableArray()
        };
    }
}

public sealed record AddExternalReferenceCommand(ExternalReference Reference) : IEditCommand
{
    public string Description => "Добавить внешний справочный источник";

    public ProjectState Apply(ProjectState project)
    {
        if (!Reference.UserRequested)
            throw new EditRejectedException("Внешний поиск разрешён только после явного запроса пользователя.");
        if (!Uri.TryCreate(Reference.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new EditRejectedException("Внешний источник должен иметь абсолютный HTTP(S) URL.");
        return project.ExternalReferences.Any(item => item.Id == Reference.Id)
            ? project
            : project with { ExternalReferences = project.ExternalReferences.Add(Reference) };
    }
}
