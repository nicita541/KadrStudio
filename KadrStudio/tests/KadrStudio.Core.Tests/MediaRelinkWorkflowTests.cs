using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class MediaRelinkWorkflowTests
{
    [Fact]
    public async Task Folder_scan_returns_control_before_synchronous_enumeration_finishes()
    {
        using var release = new ManualResetEventSlim();
        var registry = new BlockingRegistry(release);
        var session = new EditorSession(ProjectState.CreateNew());
        var invocation = Task.Factory.StartNew(() => new MediaRelinkWorkflow(registry).PrepareMissingAsync(session, ["fixtures"]),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            var pending = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);
            release.Set();
            Assert.Empty((await pending).Candidates);
        }
        finally
        {
            release.Set();
            await (await invocation);
        }
    }

    private sealed class BlockingRegistry(ManualResetEventSlim release) : IMediaRegistry
    {
        public Task<ImmutableArray<RelinkCandidate>> FindRelinkCandidatesAsync(ProjectState project, IEnumerable<string> searchRoots,
            CancellationToken cancellationToken = default)
        {
            release.Wait(cancellationToken);
            return Task.FromResult(ImmutableArray<RelinkCandidate>.Empty);
        }
        public ProjectState RefreshOnlineState(ProjectState project) => project;
        public RelinkCompatibility CheckCompatibility(MediaSource source, MediaProbeResult candidate) => throw new NotSupportedException();
        public Task<RelinkCandidate> ValidateRelinkAsync(MediaSource source, string candidatePath, bool requireVerifiedFingerprint = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Relink_preview_is_read_only_and_rejects_reused_revision_after_undo()
    {
        var source = new MediaSource(Guid.NewGuid(), "old.mp4", "old.mp4", MediaKind.Video, TimelineTime.FromSeconds(2), false);
        var session = new EditorSession(ProjectState.CreateNew() with { Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source) });
        session.Execute(new EditTransaction("rename", new RenameProjectCommand("first")));
        var before = session.State;
        var registry = new PendingRegistry();
        var pending = new MediaRelinkWorkflow(registry).PrepareAsync(session, source.Id, "new.mp4");
        Assert.Same(before, session.State);
        Assert.True(session.Undo());
        session.Execute(new EditTransaction("rename", new RenameProjectCommand("second")));
        Assert.Equal(before.Revision, session.State.Revision);
        registry.Completion.SetResult(new RelinkCandidate(source.Id, "new.mp4", RelinkCompatibility.Compatible,
            new MediaProbeResult("new.mp4", MediaKind.Video, source.Duration, [], new MediaFingerprint(1, 1, "hash")), "Compatible"));
        var preview = await pending;
        Assert.Throws<EditRejectedException>(() => MediaRelinkWorkflow.Validate(session, preview));
        Assert.Equal("old.mp4", session.State.Sources[source.Id].Path);
    }

    private sealed class PendingRegistry : IMediaRegistry
    {
        public TaskCompletionSource<RelinkCandidate> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RelinkCandidate> ValidateRelinkAsync(MediaSource source, string candidatePath, bool requireVerifiedFingerprint = false,
            CancellationToken cancellationToken = default) => Completion.Task.WaitAsync(cancellationToken);
        public ProjectState RefreshOnlineState(ProjectState project) => project;
        public RelinkCompatibility CheckCompatibility(MediaSource source, MediaProbeResult candidate) => RelinkCompatibility.Compatible;
        public Task<ImmutableArray<RelinkCandidate>> FindRelinkCandidatesAsync(ProjectState project, IEnumerable<string> searchRoots,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
