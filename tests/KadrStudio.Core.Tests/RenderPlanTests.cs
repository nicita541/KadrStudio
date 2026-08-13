using System.Collections.Immutable;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Rendering;

namespace KadrStudio.Core.Tests;

public sealed class RenderPlanTests
{
    [Fact]
    public void Builder_separates_video_audio_and_text_tracks()
    {
        var project = CreateProject();
        var plan = new RenderPlanBuilder().Build(project);

        Assert.Single(plan.VisualLayers);
        Assert.Single(plan.AudioLayers);
        Assert.Single(plan.TextLayers);
        Assert.Equal(TrackKind.Visual, project.FindTrack(plan.VisualLayers[0].TrackId)!.Kind);
        Assert.Equal(TrackKind.Audio, project.FindTrack(plan.AudioLayers[0].TrackId)!.Kind);
        Assert.Equal(TrackKind.Text, project.FindTrack(plan.TextLayers[0].TrackId)!.Kind);
        Assert.Equal(TimelineTime.FromSeconds(2), plan.Range.Start);
        Assert.Equal(TimelineTime.FromSeconds(10), plan.Range.Duration);
    }

    [Fact]
    public void Hidden_video_and_muted_audio_tracks_are_excluded_independently()
    {
        var project = CreateProject();
        project = project with
        {
            Tracks = project.Tracks.Select(track => track.Kind switch
            {
                TrackKind.Visual when track.Index == 0 => track with { IsVisible = false },
                TrackKind.Audio when track.Index == 0 => track with { IsMuted = true },
                _ => track
            }).ToImmutableArray()
        };

        var plan = new RenderPlanBuilder().Build(project);

        Assert.Empty(plan.VisualLayers);
        Assert.Empty(plan.AudioLayers);
        Assert.Single(plan.TextLayers);
    }

    [Fact]
    public void Content_signature_is_deterministic_and_changes_with_effects()
    {
        var project = CreateProject();
        var builder = new RenderPlanBuilder();
        var first = builder.Build(project);
        var revisionOnly = builder.Build(project with { Revision = project.Revision + 1 });
        var clip = project.MediaClips.Single(item => item.Video is not null);
        var changed = project with
        {
            MediaClips = project.MediaClips.Replace(
                clip,
                clip with { Video = clip.Video! with { Brightness = 0.25 } })
        };

        Assert.Equal(first.ContentSignature, revisionOnly.ContentSignature);
        Assert.NotEqual(first.ContentSignature, builder.Build(changed).ContentSignature);
    }

    [Fact]
    public void Preview_and_export_commands_use_the_same_plan_signature_and_graph()
    {
        var plan = new RenderPlanBuilder().Build(CreateProject());
        var builder = new FfmpegRenderCommandBuilder();
        var preview = builder.Build(plan, new RenderOutputOptions(
            RenderPurpose.Preview, "F:\\output folder\\preview.mp4", 960, 540, 25));
        var export = builder.Build(plan, new RenderOutputOptions(
            RenderPurpose.Export, "F:\\output folder\\export.mp4", 1920, 1080, 18));

        Assert.Equal(plan.ContentSignature, preview.PlanSignature);
        Assert.Equal(plan.ContentSignature, export.PlanSignature);
        Assert.Contains("-filter_complex", preview.Arguments);
        Assert.Contains("-filter_complex", export.Arguments);
        Assert.Contains("[vout]", preview.Arguments);
        Assert.Contains("[aout]", preview.Arguments);
        Assert.Equal("F:\\output folder\\preview.mp4", preview.Arguments[^1]);
        Assert.Equal("F:\\output folder\\export.mp4", export.Arguments[^1]);
        Assert.DoesNotContain(preview.Arguments, item => item.Contains('"'));
    }

    [Fact]
    public void Frame_query_returns_only_layers_active_at_exact_time()
    {
        var plan = new RenderPlanBuilder().Build(CreateProject());

        var beforeText = plan.GetFrame(TimelineTime.FromSeconds(2.5));
        var duringText = plan.GetFrame(TimelineTime.FromSeconds(4));

        Assert.Single(beforeText.VisualLayers);
        Assert.Single(beforeText.AudioLayers);
        Assert.Empty(beforeText.TextLayers);
        Assert.Single(duringText.TextLayers);
    }

    private static ProjectState CreateProject()
    {
        var project = ProjectState.CreateNew("Render plan", FrameRate.Fps23976);
        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media files\\episode 01.mkv", "episode 01.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(120), true, 1920, 1080, FrameRate.Fps23976,
            "hevc", "aac", 1000, 10, "source-fingerprint");
        var visual = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var text = project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var link = Guid.NewGuid();
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source),
            MediaClips =
            [
                new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.Zero,
                    TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(20), link, new VideoParameters(), null),
                new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.Zero,
                    TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(20), link, null, new AudioParameters())
            ],
            TextClips =
            [
                new TextClip(Guid.NewGuid(), text.Id, TimelineTime.FromSeconds(3),
                    TimelineTime.FromSeconds(3), "Line 1\nLine 2", new TextStyle())
            ],
            InPoint = TimelineTime.FromSeconds(2),
            OutPoint = TimelineTime.FromSeconds(12)
        };
    }
}
