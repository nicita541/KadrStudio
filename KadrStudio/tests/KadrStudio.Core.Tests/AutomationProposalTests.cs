using System.Collections.Immutable;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class AutomationProposalTests
{
    [Fact]
    public void Subtitle_proposal_applies_as_one_undoable_transaction()
    {
        var project = ProjectState.CreateNew("Automation");
        var textTrack = project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var snapshot = ProposalFactory.Capture(project);
        var proposal = ProposalFactory.ForSubtitles(
            snapshot,
            [new TextClip(Guid.NewGuid(), textTrack.Id, TimelineTime.Zero, TimelineTime.FromSeconds(2), "Hello", new TextStyle(IsSubtitle: true))],
            "Auto subtitles", "One cue", "local-whisper");
        var session = new EditorSession(project);

        var result = new AutomationProposalApplier().Apply(session, proposal);

        Assert.True(result.Applied);
        Assert.Single(session.State.TextClips);
        Assert.True(session.Undo());
        Assert.Empty(session.State.TextClips);
    }

    [Fact]
    public void Proposal_is_rejected_if_project_changed_during_analysis()
    {
        var project = ProjectState.CreateNew("Automation");
        var snapshot = ProposalFactory.Capture(project);
        var marker = new TimelineMarker(
            Guid.NewGuid(), MarkerKind.Opening, TimelineTime.Zero, TimelineTime.FromSeconds(90), "Opening");
        var proposal = ProposalFactory.ForMarkers(snapshot, [marker], "Analysis", "Opening found", "ollama");
        var session = new EditorSession(project);
        session.Execute(new EditTransaction("rename", new RenameProjectCommand("Changed")));

        var result = new AutomationProposalApplier().Apply(session, proposal);

        Assert.False(result.Applied);
        Assert.True(result.IsStale);
        Assert.Empty(session.State.Markers);
    }

    [Fact]
    public void Invalid_generated_timing_does_not_partially_change_project()
    {
        var project = ProjectState.CreateNew("Automation");
        var snapshot = ProposalFactory.Capture(project);
        var track = project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var invalid = new TextClip(
            Guid.NewGuid(), track.Id, TimelineTime.Zero, TimelineTime.Zero, "Broken", new TextStyle());
        var proposal = new AutomationProposal(
            Guid.NewGuid(), project.Id, project.Revision, DateTimeOffset.UtcNow,
            "Broken subtitles", "Invalid cue", "test",
            ImmutableArray.Create<IEditCommand>(new AddTextClipsCommand([invalid])));
        var session = new EditorSession(project);

        var result = new AutomationProposalApplier().Apply(session, proposal);

        Assert.False(result.Applied);
        Assert.Empty(session.State.TextClips);
        Assert.False(session.CanUndo);
    }
}
