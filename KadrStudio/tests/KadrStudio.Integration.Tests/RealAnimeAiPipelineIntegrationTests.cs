using System.Collections.Immutable;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using KadrStudio.Services.Editorial;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class RealAnimeAiPipelineIntegrationTests
{
    [EnvironmentRequiredFact(TestEnvironmentGate.RealAnimeAi, Timeout = 3_600_000)]
    [Trait("Category", "EnvironmentRequired")]
    [Trait("Environment", "RealAI")]
    public async Task Real_models_find_and_remove_only_opening_and_ending_without_injected_hypotheses()
    {
        var path = TestEnvironment.Require(TestEnvironmentGate.RealAnimeAi);
        var ffmpeg = new FfmpegLocator();
        ffmpeg.EnsureAvailable();
        var processes = new ProcessRunner();
        var media = await new MediaProbeService(ffmpeg, processes).ProbeAsync(path, verifyContent: true);
        var frameRate = media.FrameRate ?? FrameRate.Fps23976;
        var video = media.Streams.Single(item => item.Kind == MediaStreamKind.Video);
        var audio = media.Streams.Where(item => item.Kind == MediaStreamKind.Audio).OrderBy(item => item.StreamIndex).ToArray();
        var subtitles = media.Streams.Where(item => item.Kind == MediaStreamKind.Subtitle).OrderBy(item => item.StreamIndex).ToArray();
        var sourceId = Guid.TryParse(
            Environment.GetEnvironmentVariable("KADR_REAL_ANIME_SOURCE_ID"),
            out var configuredSourceId)
            ? configuredSourceId
            : Guid.NewGuid();
        var source = new MediaSource(
            sourceId, path, Path.GetFileName(path), MediaKind.Video, media.Duration, true,
            media.Width, media.Height, frameRate, video.Codec, audio[0].Codec,
            media.Fingerprint.Length, media.Fingerprint.LastWriteUtcTicks, media.Fingerprint.FastHash,
            FastFingerprint: media.Fingerprint.FastHash,
            VerifiedFingerprint: media.Fingerprint.VerifiedHash ?? string.Empty,
            Streams: media.Streams, IsVariableFrameRate: media.IsVariableFrameRate);
        var project = ProjectState.CreateNew("real anime AI", frameRate);
        var visualTrack = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audioTracks = project.Tracks.Where(item => item.Kind == TrackKind.Audio).OrderBy(item => item.Index).ToList();
        while (audioTracks.Count < audio.Length)
        {
            var track = new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, audioTracks.Count, $"A{audioTracks.Count + 1}");
            project = project with { Tracks = project.Tracks.Add(track) };
            audioTracks.Add(track);
        }
        var subtitleTracks = project.Tracks.Where(item => item.Kind == TrackKind.Subtitle).OrderBy(item => item.Index).ToList();
        while (subtitleTracks.Count < subtitles.Length)
        {
            var track = new TimelineTrack(Guid.NewGuid(), TrackKind.Subtitle, subtitleTracks.Count, $"S{subtitleTracks.Count + 1}");
            project = project with { Tracks = project.Tracks.Add(track) };
            subtitleTracks.Add(track);
        }
        var link = Guid.NewGuid();
        project = (project with
        {
            Sources = project.Sources.Add(source.Id, source),
            MediaClips =
            [
                new MediaClip(Guid.NewGuid(), source.Id, visualTrack.Id, TimelineTime.Zero, TimelineTime.Zero,
                    source.Duration, link, Video: new VideoParameters(), StreamIndex: video.StreamIndex),
                .. audio.Select((stream, index) => new MediaClip(
                    Guid.NewGuid(), source.Id, audioTracks[index].Id, TimelineTime.Zero, TimelineTime.Zero,
                    source.Duration, link, Audio: new AudioParameters(), StreamIndex: stream.StreamIndex))
            ],
            SubtitleClips = subtitles.Select((stream, index) => new SubtitleClip(
                Guid.NewGuid(), source.Id, stream.StreamIndex, subtitleTracks[index].Id,
                TimelineTime.Zero, TimelineTime.Zero, source.Duration, link)).ToImmutableArray()
        }).EnsureSequenceContainer();

        using var connection = new AiServerConnection();
        var client = new AiServerV2Client(connection, requireProduction: true);
        var indexer = new AiServerMediaUnderstandingIndexer(client, new AnalysisProxyBuilder(ffmpeg, processes));
        var reasoner = new AiServerEditorialReasoner(client);
        var pipeline = new EditorialPipeline(
            indexer, reasoner, reasoner, indexer,
            retriever: new HierarchicalMediaRetriever(semanticRanker: new AiServerSemanticNodeRanker(client)));
        var updates = new Progress<EditorialPipelineProgress>(value =>
            Console.WriteLine($"{value.Stage}: {value.Progress:P0} {value.Message}"));

        var result = await pipeline.RunAsync(new EditorialPipelineRequest(
            project, project.ActiveSequence!.Id, "Удали только опенинг и эндинг. Сохрани post-credit и preview.",
            MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode)), updates);

        var removals = result.Graph.Decisions.Where(item => item.Kind == EditDecisionKind.Remove)
            .OrderBy(item => item.SourceRange.Start).ToArray();
        Assert.Equal([SegmentRole.Opening, SegmentRole.Ending], removals.Select(item => item.SegmentRole).ToArray());
        Assert.True(result.Brief.TargetSourceIds.SequenceEqual([source.Id]));
        Assert.True(result.Brief.TargetSelectionConfidence >= 0.65,
            $"Director target confidence was {result.Brief.TargetSelectionConfidence:0.###}: {result.Brief.TargetSelectionRationale}");
        AssertWithinFrame(removals[0].SourceRange.Start, 146, frameRate);
        AssertWithinFrame(removals[0].SourceRange.End, 238, frameRate);
        AssertWithinFrame(removals[1].SourceRange.Start, 1290, frameRate);
        AssertWithinFrame(removals[1].SourceRange.End, 1380, frameRate);
        var draft = result.Compilation.Draft.Sequence;
        Assert.InRange(draft.Duration.TotalSeconds, 1245.10, 1245.19);
        Assert.Equal([1, 2], draft.MediaClips.Where(item => item.Audio is not null)
            .Select(item => item.StreamIndex!.Value).Distinct().Order().ToArray());
        Assert.Equal([3, 4], draft.SubtitleClips.Select(item => item.StreamIndex).Distinct().Order().ToArray());
        // Protected anchors are human-readable wall-clock times.  At 24000/1001
        // the first decodable frame at 23:00 is 1380.003625s, so compare on the
        // same frame grid used by the compiler instead of treating 1380.000000s
        // (where no frame exists) as media content.
        var postCreditsFrame = TimelineTime.FromSeconds(1380).SnapToFrame(frameRate);
        var previewFrame = TimelineTime.FromSeconds(1400).SnapToFrame(frameRate);
        Assert.DoesNotContain(result.Compilation.RemovedTimelineRanges,
            range => range.Contains(postCreditsFrame) || range.Contains(previewFrame));
    }

    private static void AssertWithinFrame(TimelineTime actual, double expectedSeconds, FrameRate frameRate)
        => Assert.True(Math.Abs(actual.TotalSeconds - expectedSeconds) <= frameRate.FrameDuration.TotalSeconds,
            $"Expected {expectedSeconds:F6}s ± one frame, got {actual.TotalSeconds:F6}s.");
}
