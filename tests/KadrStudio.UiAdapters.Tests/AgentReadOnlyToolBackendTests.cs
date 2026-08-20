using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;
using KadrStudio.Core.Domain;
using KadrStudio.Services.Agent;

namespace KadrStudio.UiAdapters.Tests;

public sealed class AgentReadOnlyToolBackendTests
{
    [Fact]
    public async Task Project_and_timeline_tools_read_real_core_state()
    {
        var fixture = CreateProject();
        var backend = new KadrAgentReadOnlyToolBackend(
            () => fixture.Project,
            new FakeRangeInspector());
        var context = Context(fixture.Project, fixture.SequenceId);

        var project = await backend.InspectProjectAsync(context, CancellationToken.None);
        var timeline = await backend.InspectTimelineAsync(
            context, fixture.SequenceId, CancellationToken.None);

        Assert.Equal(fixture.Project.Id, project.GetProperty("project_id").GetGuid());
        Assert.Equal(1, project.GetProperty("source_count").GetInt32());
        Assert.Equal(fixture.SequenceId, timeline.GetProperty("sequence_id").GetGuid());
        Assert.Equal(1, timeline.GetProperty("media_clip_count").GetInt32());
    }

    [Fact]
    public async Task Media_tool_exposes_stable_id_metadata_without_mutation()
    {
        var fixture = CreateProject();
        var backend = new KadrAgentReadOnlyToolBackend(
            () => fixture.Project,
            new FakeRangeInspector());
        var before = fixture.Project;

        var data = await backend.InspectMediaAsync(
            Context(fixture.Project, fixture.SequenceId),
            fixture.SourceId,
            CancellationToken.None);

        Assert.Equal(fixture.SourceId, data.GetProperty("media_id").GetGuid());
        Assert.Equal("episode.mp4", data.GetProperty("name").GetString());
        Assert.Same(before, fixture.Project);
    }

    [Fact]
    public async Task Sequence_range_maps_timeline_time_to_source_time_and_forwards_query()
    {
        var fixture = CreateProject();
        var inspector = new FakeRangeInspector();
        var backend = new KadrAgentReadOnlyToolBackend(
            () => fixture.Project,
            inspector);

        var data = await backend.InspectRangeAsync(
            Context(fixture.Project, fixture.SequenceId),
            new AgentRangeInspectionRequest(
                AgentRangeTargetKind.Sequence,
                fixture.SequenceId,
                12,
                15,
                AgentRangeInspectionDetail.Frames,
                "Что происходит в этом месте?"),
            CancellationToken.None);

        var forwarded = Assert.Single(inspector.Requests);
        Assert.Equal(fixture.SourceId, forwarded.SourceId);
        Assert.Equal(32, forwarded.Request.StartSeconds, 3);
        Assert.Equal(35, forwarded.Request.EndSeconds, 3);
        Assert.Equal("Что происходит в этом месте?", forwarded.Request.Query);
        Assert.False(data.GetProperty("analysis_deferred").GetBoolean());
    }

    [Fact]
    public async Task Project_change_invalidates_old_agent_context()
    {
        var fixture = CreateProject();
        var backend = new KadrAgentReadOnlyToolBackend(
            () => fixture.Project,
            new FakeRangeInspector());
        var wrong = new AgentToolContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.SequenceId,
            null,
            AgentTaskPhase.Investigating);

        var error = await Assert.ThrowsAsync<AgentToolRejectedException>(
            async () => await backend.InspectProjectAsync(wrong, CancellationToken.None));

        Assert.Equal("project_changed", error.ErrorCode);
    }

    [Fact]
    public async Task Timeline_integrity_reports_exact_gap_seconds_and_frames()
    {
        var fixture = CreateProject();
        var project = fixture.Project;
        var first = Assert.Single(project.MediaClips);
        var second = first with
        {
            Id = Guid.NewGuid(),
            Start = TimelineTime.FromSeconds(30.197863),
            SourceIn = TimelineTime.FromSeconds(60),
            Duration = TimelineTime.FromSeconds(10)
        };
        project = (project with { MediaClips = [first, second] })
            .SynchronizeActiveSequence(incrementRevision: false);
        var backend = new KadrAgentReadOnlyToolBackend(
            () => project,
            new FakeRangeInspector());

        var integrity = await backend.InspectTimelineIntegrityAsync(
            Context(project, fixture.SequenceId),
            fixture.SequenceId,
            CancellationToken.None);

        Assert.Equal(1, integrity.GetProperty("gap_count").GetInt32());
        var gap = Assert.Single(integrity.GetProperty("gaps").EnumerateArray());
        Assert.Equal(0.197863, gap.GetProperty("delta_seconds").GetDouble(), precision: 6);
        Assert.Equal(5.936, gap.GetProperty("delta_frames").GetDouble(), precision: 3);
        Assert.Equal(0, integrity.GetProperty("overlap_count").GetInt32());
    }

    private static Fixture CreateProject()
    {
        var project = ProjectState.CreateNew("Agent backend test");
        var visualTrack = project.Tracks.First(item => item.Kind == TrackKind.Visual);
        var source = new MediaSource(
            Guid.NewGuid(),
            @"C:\media\episode.mp4",
            "episode.mp4",
            MediaKind.Video,
            TimelineTime.FromSeconds(120),
            true,
            1920,
            1080,
            FrameRate.Fps30);

        var clip = new MediaClip(
            Guid.NewGuid(),
            source.Id,
            visualTrack.Id,
            TimelineTime.FromSeconds(10),
            TimelineTime.FromSeconds(30),
            TimelineTime.FromSeconds(20));

        project = project with
        {
            Sources = project.Sources.Add(source.Id, source),
            MediaClips = [clip]
        };
        project = project.EnsureSequenceContainer();

        return new Fixture(
            project,
            source.Id,
            project.ActiveSequenceId!.Value);
    }

    private static AgentToolContext Context(
        ProjectState project,
        Guid sequenceId)
        => new(
            Guid.NewGuid(),
            project.Id,
            sequenceId,
            null,
            AgentTaskPhase.Investigating);

    private sealed class FakeRangeInspector : IAgentMediaRangeInspector
    {
        public List<(Guid SourceId, AgentRangeInspectionRequest Request)> Requests { get; } = [];

        public ValueTask<JsonElement> InspectAsync(
            MediaSource source,
            AgentRangeInspectionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add((source.Id, request));
            return ValueTask.FromResult(
                AgentToolJson.ToElement(new
                {
                    source_id = source.Id,
                    start_seconds = request.StartSeconds,
                    end_seconds = request.EndSeconds,
                    query = request.Query
                }));
        }
    }

    private sealed record Fixture(
        ProjectState Project,
        Guid SourceId,
        Guid SequenceId);
}
