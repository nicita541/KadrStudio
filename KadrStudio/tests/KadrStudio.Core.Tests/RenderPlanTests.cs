using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Rendering;

namespace KadrStudio.Core.Tests;

public sealed class RenderPlanTests
{
    [Fact]
    public void Hardware_fallback_only_classifies_encoder_or_device_failures()
    {
        Assert.True(HardwareEncodingFallback.IsUnavailable(
            new InvalidOperationException("Unknown encoder 'h264_nvenc'")));
        Assert.True(HardwareEncodingFallback.IsUnavailable(
            new InvalidOperationException("No capable devices found for CUDA")));
        Assert.False(HardwareEncodingFallback.IsUnavailable(
            new InvalidOperationException("Source file was not found")));
    }

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
    public void Pipeline_signatures_change_only_for_their_own_content()
    {
        var project = CreateProject();
        var builder = new RenderPlanBuilder();
        var initial = builder.Build(project);
        var audio = project.MediaClips.Single(item => item.Audio is not null);
        var audioChanged = builder.Build(project with
        {
            MediaClips = project.MediaClips.Replace(audio,
                audio with { Audio = audio.Audio! with { Volume = 0.35 } })
        });

        Assert.Equal(initial.VideoContentSignature, audioChanged.VideoContentSignature);
        Assert.NotEqual(initial.AudioContentSignature, audioChanged.AudioContentSignature);
        Assert.Equal(initial.OverlaySignature, audioChanged.OverlaySignature);

        var text = project.TextClips.Single();
        var textChanged = builder.Build(project with
        {
            TextClips = project.TextClips.Replace(text, text with { Text = "Changed" })
        });
        Assert.Equal(initial.VideoContentSignature, textChanged.VideoContentSignature);
        Assert.Equal(initial.AudioContentSignature, textChanged.AudioContentSignature);
        Assert.NotEqual(initial.OverlaySignature, textChanged.OverlaySignature);
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

        Assert.Equal(plan.GetPipelineSignature(true, true, true), preview.PlanSignature);
        Assert.Equal(plan.GetPipelineSignature(true, true, true), export.PlanSignature);
        Assert.Contains("-filter_complex", preview.Arguments);
        Assert.Contains("-filter_complex", export.Arguments);
        Assert.Contains("[vout]", preview.Arguments);
        Assert.Contains("[aout]", preview.Arguments);
        Assert.Equal("F:\\output folder\\preview.mp4", preview.Arguments[^1]);
        Assert.Equal("F:\\output folder\\export.mp4", export.Arguments[^1]);
        Assert.DoesNotContain(preview.Arguments, item => item.Contains('"'));
    }

    [Fact]
    public void Frame_server_writes_uncompressed_bgra_to_stdout_without_yuv_override()
    {
        var plan = new RenderPlanBuilder().Build(CreateProject());
        var command = new FfmpegRenderCommandBuilder().Build(plan, new RenderOutputOptions(
            RenderPurpose.FrameServer, "pipe:1", 960, 540,
            IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false));

        Assert.Equal("pipe:1", command.OutputPath);
        Assert.Equal("pipe:1", command.Arguments[^1]);
        Assert.Contains("rawvideo", command.Arguments);
        Assert.Contains("bgra", command.Arguments);
        Assert.DoesNotContain("yuv420p", command.Arguments);
        Assert.DoesNotContain("[aout]", command.Arguments);
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

    [Fact]
    public void All_video_parameters_are_part_of_video_signature_only()
    {
        var project = CreateProject();
        var builder = new RenderPlanBuilder();
        var initial = builder.Build(project);
        var clip = project.MediaClips.Single(item => item.Video is not null);
        var changed = builder.Build(project with
        {
            MediaClips = project.MediaClips.Replace(clip, clip with
            {
                Video = clip.Video! with
                {
                    PositionX = 0.31, PositionY = 0.68, ScaleX = 1.2, ScaleY = 0.8,
                    Rotation = 17, CropLeft = 0.1, CropBottom = 0.12, Opacity = 0.73
                }
            })
        });

        Assert.NotEqual(initial.VideoGraphSignature, changed.VideoGraphSignature);
        Assert.Equal(initial.AudioGraphSignature, changed.AudioGraphSignature);
        Assert.Equal(initial.OverlaySignature, changed.OverlaySignature);
    }

    [Fact]
    public void Typed_transitions_invalidate_only_their_pipeline_and_extend_decode_windows()
    {
        var project = CreateTransitionProject();
        var builder = new RenderPlanBuilder();
        var without = builder.Build(project with { Transitions = [] });
        var withVideo = builder.Build(project);
        var audioTransition = project.Transitions.Single(item => item.Kind == TransitionKind.ConstantPowerAudio);
        var withAudio = builder.Build(project with { Transitions = [audioTransition] });

        Assert.Single(withVideo.VideoTransitions);
        Assert.Single(withVideo.AudioTransitions);
        Assert.NotEqual(without.VideoGraphSignature, withVideo.VideoGraphSignature);
        Assert.Equal(without.OverlaySignature, withVideo.OverlaySignature);
        Assert.Equal(without.VideoGraphSignature, withAudio.VideoGraphSignature);
        Assert.NotEqual(without.AudioGraphSignature, withAudio.AudioGraphSignature);

        var command = new FfmpegRenderCommandBuilder().Build(withVideo, new RenderOutputOptions(
            RenderPurpose.Export, "F:\\output.mp4", 320, 180));
        var graph = command.Arguments[command.Arguments.IndexOf("-filter_complex") + 1];
        Assert.Contains("alpha(X,Y)*clip((T+", graph);
        Assert.Contains("volume='cos(clip((t+", graph);
        Assert.Contains("volume='sin(clip((t+", graph);
    }

    [Fact]
    public void Transition_command_clamps_requested_duration_to_source_handles()
    {
        var project = CreateTransitionProject() with { Transitions = [] };
        var visual = project.MediaClips.Where(item => item.Video is not null).OrderBy(item => item.Start).ToArray();
        var requested = new TimelineTransition(
            Guid.NewGuid(), TransitionKind.CrossDissolve, visual[0].TrackId,
            visual[0].Id, visual[1].Id, TimelineTime.FromSeconds(3), TimelineTime.FromSeconds(4));
        var session = new KadrStudio.Application.Editing.EditorSession(project);

        var result = session.Execute(new KadrStudio.Application.Editing.EditTransaction(
            "transition", new KadrStudio.Application.Editing.UpsertTransitionCommand(requested)));

        var transition = Assert.Single(result.State.Transitions);
        Assert.Equal(TimelineTime.FromSeconds(4), transition.Start);
        Assert.Equal(TimelineTime.FromSeconds(2), transition.Duration);
    }

    [Fact]
    public void Transition_at_full_length_edit_prepares_handles_and_linked_audio_atomically()
    {
        var project = CreateFullLengthTransitionProject();
        var from = project.MediaClips.Single(item => item.Video is not null && item.Start == TimelineTime.Zero);
        var transitionId = Guid.NewGuid();
        var audioTransitionId = Guid.NewGuid();
        var session = new EditorSession(project);

        var result = session.Execute(new EditTransaction(
            "auto transition",
            new CreateTransitionAtEditCommand(
                transitionId, from.Id, TransitionKind.CrossDissolve,
                TimelineTime.FromSeconds(2), audioTransitionId)));

        Assert.Equal(2, result.State.Transitions.Length);
        var videoTransition = result.State.Transitions.Single(item => item.Id == transitionId);
        Assert.Equal(TimelineTime.FromSeconds(8), videoTransition.Start);
        Assert.Equal(TimelineTime.FromSeconds(2), videoTransition.Duration);
        var visual = result.State.MediaClips.Where(item => item.Video is not null).OrderBy(item => item.Start).ToArray();
        Assert.Equal(TimelineTime.FromSeconds(9), visual[0].End);
        Assert.Equal(visual[0].End, visual[1].Start);
        Assert.Equal(TimelineTime.FromSeconds(1), visual[1].SourceIn);
        Assert.Equal(TimelineTime.FromSeconds(18), result.State.Duration);
        Assert.Contains(result.State.MediaClips, item => item.LinkGroupId is null &&
            item.Start == TimelineTime.FromSeconds(12));
        Assert.All(result.State.MediaClips.GroupBy(item => item.LinkGroupId), group =>
        {
            var first = group.First();
            Assert.All(group, item =>
            {
                Assert.Equal(first.Start, item.Start);
                Assert.Equal(first.SourceIn, item.SourceIn);
                Assert.Equal(first.Duration, item.Duration);
            });
        });
        var renderPlan = new RenderPlanBuilder().Build(result.State);
        Assert.Single(renderPlan.VideoTransitions);
        Assert.Single(renderPlan.AudioTransitions);
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

    private static ProjectState CreateTransitionProject()
    {
        var project = ProjectState.CreateNew("Transitions", new FrameRate(24));
        var source = new MediaSource(Guid.NewGuid(), "F:\\media\\handles.mp4", "handles.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(7), true, 320, 180, new FrameRate(24), Fingerprint: "handles");
        var visual = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var v1 = new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(5), Video: new VideoParameters());
        var v2 = new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.FromSeconds(5),
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(5), Video: new VideoParameters());
        var a1 = new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(5), Audio: new AudioParameters());
        var a2 = new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.FromSeconds(5),
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(5), Audio: new AudioParameters());
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source),
            MediaClips = [v1, v2, a1, a2],
            Transitions =
            [
                new TimelineTransition(Guid.NewGuid(), TransitionKind.CrossDissolve, visual.Id, v1.Id, v2.Id,
                    TimelineTime.FromSeconds(4), TimelineTime.FromSeconds(2)),
                new TimelineTransition(Guid.NewGuid(), TransitionKind.ConstantPowerAudio, audio.Id, a1.Id, a2.Id,
                    TimelineTime.FromSeconds(4), TimelineTime.FromSeconds(2))
            ]
        };
    }

    private static ProjectState CreateFullLengthTransitionProject()
    {
        var project = ProjectState.CreateNew("Full clips transition", new FrameRate(24));
        var firstSource = new MediaSource(Guid.NewGuid(), "F:\\media\\first.mp4", "first.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(10), true, 320, 180, new FrameRate(24), Fingerprint: "first");
        var secondSource = new MediaSource(Guid.NewGuid(), "F:\\media\\second.mp4", "second.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(10), true, 320, 180, new FrameRate(24), Fingerprint: "second");
        var visual = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var overlay = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 1);
        var firstGroup = Guid.NewGuid();
        var secondGroup = Guid.NewGuid();
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty
                .Add(firstSource.Id, firstSource).Add(secondSource.Id, secondSource),
            MediaClips =
            [
                new MediaClip(Guid.NewGuid(), firstSource.Id, visual.Id, TimelineTime.Zero,
                    TimelineTime.Zero, firstSource.Duration, firstGroup, Video: new VideoParameters()),
                new MediaClip(Guid.NewGuid(), firstSource.Id, audio.Id, TimelineTime.Zero,
                    TimelineTime.Zero, firstSource.Duration, firstGroup, Audio: new AudioParameters()),
                new MediaClip(Guid.NewGuid(), secondSource.Id, visual.Id, firstSource.Duration,
                    TimelineTime.Zero, secondSource.Duration, secondGroup, Video: new VideoParameters()),
                new MediaClip(Guid.NewGuid(), secondSource.Id, audio.Id, firstSource.Duration,
                    TimelineTime.Zero, secondSource.Duration, secondGroup, Audio: new AudioParameters()),
                new MediaClip(Guid.NewGuid(), secondSource.Id, overlay.Id, TimelineTime.FromSeconds(12),
                    TimelineTime.Zero, TimelineTime.FromSeconds(2), Video: new VideoParameters())
            ]
        };
    }
}
