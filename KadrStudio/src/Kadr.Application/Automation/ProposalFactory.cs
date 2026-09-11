using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation;

public static class ProposalFactory
{
    public static ProjectAutomationSnapshot Capture(IEditorSession session)
        => Capture(session.State) with
        {
            SessionStamp = new EditorSessionStamp(session.SessionId, session.EditVersion)
        };

    public static bool IsCurrent(IEditorSession session, ProjectAutomationSnapshot snapshot)
        => (snapshot.SessionStamp is null || snapshot.SessionStamp ==
                new EditorSessionStamp(session.SessionId, session.EditVersion)) &&
            EditorSession.HasSameEditableState(session.State, snapshot.State);

    public static ProjectAutomationSnapshot Capture(ProjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fingerprint = string.Join('|', state.Sources.Values
            .OrderBy(item => item.Id)
            .Select(item => $"{item.Id:N}:{item.Fingerprint}"));
        return new ProjectAutomationSnapshot(state.Id, state.Revision, DateTimeOffset.UtcNow, state, fingerprint);
    }

    public static AutomationProposal ForMarkers(
        ProjectAutomationSnapshot snapshot,
        IReadOnlyList<TimelineMarker> markers,
        string title,
        string summary,
        string producer)
        => Create(snapshot, title, summary, producer,
            new ReplaceMarkersCommand(markers));

    public static AutomationProposal ForSubtitles(
        ProjectAutomationSnapshot snapshot,
        IReadOnlyList<TextClip> subtitles,
        string title,
        string summary,
        string producer)
        => Create(snapshot, title, summary, producer,
            new AddTextClipsCommand(subtitles));

    private static AutomationProposal Create(
        ProjectAutomationSnapshot snapshot,
        string title,
        string summary,
        string producer,
        params IEditCommand[] commands)
        => new(
            Guid.NewGuid(),
            snapshot.ProjectId,
            snapshot.BaseRevision,
            DateTimeOffset.UtcNow,
            title,
            summary,
            producer,
            commands.ToImmutableArray(),
            CreateCheckpoint: true)
        {
            SessionStamp = snapshot.SessionStamp,
            InputState = snapshot.State
        };
}
