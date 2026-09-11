using System.Collections.Immutable;
using KadrStudio.Application.Preview;
using KadrStudio.Infrastructure.Preview;
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

    [Fact]
    public async Task Silent_audio_meter_is_finite_and_can_cross_the_media_host_protocol()
    {
        var level = new StereoPcmMeter().Measure(new float[64]);
        var expected = new MediaHostAudioMeterHeader(level, TimelineTime.Zero, 7);
        await using var stream = new MemoryStream();

        await MediaHostPacketIO.WriteAsync(stream,
            MediaHostPacket.Create(MediaHostPacketType.AudioMeter, expected));
        stream.Position = 0;
        var actual = (await MediaHostPacketIO.ReadAsync(stream))!.ReadHeader<MediaHostAudioMeterHeader>();

        Assert.True(float.IsFinite(actual.Level.LeftPeakDb));
        Assert.True(float.IsFinite(actual.Level.RightPeakDb));
        Assert.Equal(StereoPcmMeter.SilenceFloorDb, actual.Level.LeftPeakDb);
    }

    [Fact]
    public void Shared_frame_ring_presents_latest_and_never_overwrites_a_reading_slot()
    {
        var name = "kadr-frame-test-" + Guid.NewGuid().ToString("N");
        using var writer = SharedFrameRingWriter.Create(name, slotCapacity: 4096, slotCount: 3);
        using var reader = SharedFrameRingReader.Open(name, slotCapacity: 4096, slotCount: 3);
        for (var frameId = 1L; frameId <= 3; frameId++)
        {
            var pixels = Enumerable.Repeat((byte)frameId, 64).ToArray();
            Assert.True(writer.TryWrite(new VideoFrame(
                new TimelineTime(frameId), 4, 4, 16, pixels, Generation: 7), out _));
        }

        using var newest = reader.TryAcquireLatest();
        Assert.NotNull(newest);
        Assert.Null(reader.TryAcquireLatest());
        Assert.Equal(3, newest.FrameId);
        var copy = new byte[newest.ValidLength];
        newest.CopyTo(copy);
        Assert.All(copy, value => Assert.Equal((byte)3, value));

        Assert.True(writer.TryWrite(new VideoFrame(
            new TimelineTime(4), 4, 4, 16, new byte[64], Generation: 7), out var descriptor));
        Assert.Equal(4, descriptor.FrameId);
        using var fourth = reader.TryAcquireLatest();
        Assert.NotNull(fourth);
        Assert.True(writer.TryWrite(new VideoFrame(
            new TimelineTime(5), 4, 4, 16, new byte[64], Generation: 8), out _));
        using var fifth = reader.TryAcquireLatest();
        Assert.NotNull(fifth);
        Assert.Equal(5, fifth.FrameId);
        Assert.False(writer.TryWrite(new VideoFrame(
            new TimelineTime(6), 4, 4, 16, new byte[64], Generation: 8), out _));
        newest.CopyTo(copy);
        Assert.All(copy, value => Assert.Equal((byte)3, value));
    }

    [Fact]
    public void Latest_value_dispatcher_has_one_pending_callback_and_drops_superseded_values()
    {
        var scheduled = new List<Action>();
        var presented = new List<int>();
        var dispatcher = new LatestValueDispatcher<int>(scheduled.Add, presented.Add);

        dispatcher.Offer(1);
        dispatcher.Offer(2);
        dispatcher.Offer(3);

        Assert.Single(scheduled);
        scheduled.Single().Invoke();
        Assert.Equal([3], presented);
        Assert.Equal(2, dispatcher.DroppedCount);
    }

    [Fact]
    public void Queued_video_is_rechecked_at_presentation_and_can_be_cleared()
    {
        var scheduled = new List<Action>();
        var shown = new List<long>();
        long generation = 1;
        var dispatcher = new LatestVideoFrameDispatcher(scheduled.Add,
            frame => shown.Add(frame.Generation), frame => frame.Generation == generation);
        dispatcher.Offer(new VideoFrame(TimelineTime.Zero, 1, 1, 4, new byte[4], Generation: 1));
        generation = 2;
        scheduled[0]();
        Assert.Empty(shown);
        dispatcher.Offer(new VideoFrame(TimelineTime.Zero, 1, 1, 4, new byte[4], Generation: 2));
        dispatcher.Clear();
        scheduled[1]();
        Assert.Empty(shown);
        dispatcher.Offer(new VideoFrame(TimelineTime.Zero, 1, 1, 4, new byte[4], Generation: 2));
        scheduled[2]();
        Assert.Equal([2L], shown);
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
