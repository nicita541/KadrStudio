using System.Collections.Immutable;
using KadrStudio.Application.Preview;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class MediaHostProtocolTests
{
    [Fact]
    public async Task Binary_packet_round_trip_preserves_frame_header_and_raw_payload()
    {
        var payload = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        var expected = MediaHostPacket.Create(
            MediaHostPacketType.VideoFrame,
            new MediaHostFrameHeader(TimelineTime.FromFrames(137, FrameRate.Fps23976), 32, 32, 128, 42),
            Guid.NewGuid(), payload);
        await using var stream = new MemoryStream();

        await MediaHostPacketIO.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await MediaHostPacketIO.ReadAsync(stream);

        Assert.NotNull(actual);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.CorrelationId, actual.CorrelationId);
        Assert.Equal(expected.ReadHeader<MediaHostFrameHeader>(), actual.ReadHeader<MediaHostFrameHeader>());
        Assert.True(payload.AsSpan().SequenceEqual(actual.Payload.Span));
    }

    [Fact]
    public async Task Prepare_round_trip_preserves_exact_timebase_transitions_and_generations()
    {
        var project = CreateProject();
        var expected = new MediaHostPrepare(
            new RenderPlanBuilder().Build(project),
            new PreviewRequest(TimelineTime.FromFrames(17, FrameRate.Fps23976), FrameRate.Fps23976,
                960, 540, true, new PreviewGeneration(5, 8, 13)));
        await using var stream = new MemoryStream();

        await MediaHostPacketIO.WriteAsync(stream,
            MediaHostPacket.Create(MediaHostPacketType.Prepare, expected, Guid.NewGuid()));
        stream.Position = 0;
        var actual = (await MediaHostPacketIO.ReadAsync(stream))!.ReadHeader<MediaHostPrepare>();

        Assert.Equal(FrameRate.Fps23976, actual.Plan.FrameRate);
        Assert.Equal(44_100, actual.Plan.AudioSampleRate);
        Assert.Equal(expected.Request, actual.Request);
        Assert.Equal(expected.Plan.VideoTransitions.Single(), actual.Plan.VideoTransitions.Single());
        Assert.Equal(expected.Plan.AudioTransitions.Single(), actual.Plan.AudioTransitions.Single());
        Assert.Equal(expected.Plan.ContentSignature, actual.Plan.ContentSignature);
    }

    [Fact]
    public async Task Audio_meter_round_trip_carries_pcm_position_and_generation()
    {
        var expected = new MediaHostAudioMeterHeader(
            new AudioMeterLevel(0.8f, 0.4f, 0.3f, 0.2f, -1.9382f, -7.9588f),
            TimelineTime.FromFrames(411, FrameRate.Fps2997),
            93);
        await using var stream = new MemoryStream();

        await MediaHostPacketIO.WriteAsync(stream,
            MediaHostPacket.Create(MediaHostPacketType.AudioMeter, expected));
        stream.Position = 0;
        var actual = (await MediaHostPacketIO.ReadAsync(stream))!.ReadHeader<MediaHostAudioMeterHeader>();

        Assert.Equal(expected, actual);
    }

    private static ProjectState CreateProject()
    {
        var project = ProjectState.CreateNew("IPC", FrameRate.Fps23976) with
        {
            Sequence = new SequenceSettings(1920, 1080, FrameRate.Fps23976, 44_100)
        };
        var source = new MediaSource(Guid.NewGuid(), "F:\\media\\ipc.mp4", "ipc.mp4", MediaKind.Video,
            TimelineTime.FromSeconds(10), true, 1920, 1080, FrameRate.Fps23976, Fingerprint: "ipc");
        var video = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var v1 = new MediaClip(Guid.NewGuid(), source.Id, video.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(3), Video: new VideoParameters());
        var v2 = new MediaClip(Guid.NewGuid(), source.Id, video.Id, TimelineTime.FromSeconds(3),
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(3), Video: new VideoParameters());
        var a1 = new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.Zero,
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(3), Audio: new AudioParameters());
        var a2 = new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.FromSeconds(3),
            TimelineTime.FromSeconds(1), TimelineTime.FromSeconds(3), Audio: new AudioParameters());
        return project with
        {
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source),
            MediaClips = [v1, v2, a1, a2],
            Transitions =
            [
                new TimelineTransition(Guid.NewGuid(), TransitionKind.CrossDissolve, video.Id, v1.Id, v2.Id,
                    TimelineTime.FromSeconds(2.5), TimelineTime.FromSeconds(1)),
                new TimelineTransition(Guid.NewGuid(), TransitionKind.ConstantPowerAudio, audio.Id, a1.Id, a2.Id,
                    TimelineTime.FromSeconds(2.5), TimelineTime.FromSeconds(1))
            ]
        };
    }
}
