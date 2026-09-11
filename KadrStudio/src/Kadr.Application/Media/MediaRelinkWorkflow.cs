using System.Collections.Immutable;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Editing;

namespace KadrStudio.Application.Media;

public sealed record MediaRelinkPreview(ProjectAutomationSnapshot Snapshot, ImmutableArray<RelinkCandidate> Candidates);

public sealed class MediaRelinkWorkflow(IMediaRegistry registry)
{
    public async Task<MediaRelinkPreview> PrepareAsync(IEditorSession session, Guid sourceId, string path,
        bool verifyContent = true, CancellationToken cancellationToken = default)
    {
        var snapshot = ProposalFactory.Capture(session);
        if (!snapshot.State.Sources.TryGetValue(sourceId, out var source))
            throw new KeyNotFoundException($"Media source {sourceId} was not found.");
        var candidate = await registry.ValidateRelinkAsync(source, path, verifyContent, cancellationToken).ConfigureAwait(false);
        return new MediaRelinkPreview(snapshot, [candidate]);
    }

    public async Task<MediaRelinkPreview> PrepareMissingAsync(IEditorSession session, IEnumerable<string> roots,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ProposalFactory.Capture(session);
        var searchRoots = roots.ToArray();
        var candidates = await Task.Run(() => registry.FindRelinkCandidatesAsync(snapshot.State, searchRoots, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new MediaRelinkPreview(snapshot, candidates);
    }

    public static void Validate(IEditorSession session, MediaRelinkPreview preview)
    {
        if (!ProposalFactory.IsCurrent(session, preview.Snapshot))
            throw new EditRejectedException("Проект изменился во время проверки файлов. Повторите поиск соответствий.");
        if (preview.Candidates.IsDefaultOrEmpty || preview.Candidates.Any(candidate => !candidate.CanApply))
            throw new EditRejectedException("Не все выбранные файлы совместимы с исходниками проекта.");
    }
}
