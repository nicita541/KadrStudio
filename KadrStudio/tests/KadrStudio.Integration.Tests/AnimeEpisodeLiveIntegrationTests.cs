using System.Collections.Immutable;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Media;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class AnimeEpisodeLiveIntegrationTests
{
    [EnvironmentRequiredFact(TestEnvironmentGate.LiveAnimeFixture, Timeout = 300_000)]
    [Trait("Category", "EnvironmentRequired")]
    [Trait("Environment", "AnimeFixture")]
    public async Task Real_anime_episode_probe_preserves_source_and_exposes_every_required_stream()
    {
        var path = TestEnvironment.Require(TestEnvironmentGate.LiveAnimeFixture);
        Assert.True(File.Exists(path), $"Anime fixture was not found: {path}");
        var before = new FileInfo(path);
        var originalLength = before.Length;
        var originalWriteTime = before.LastWriteTimeUtc;

        var locator = new FfmpegLocator();
        locator.EnsureAvailable();
        var result = await new MediaProbeService(locator, new ProcessRunner())
            .ProbeAsync(path, verifyContent: true);

        Assert.Equal(new FrameRate(24_000, 1_001), result.FrameRate);
        Assert.InRange(result.Duration.TotalSeconds, 1427.10, 1427.19);
        Assert.Equal([1, 2], result.Streams
            .Where(stream => stream.Kind == MediaStreamKind.Audio)
            .Select(stream => stream.StreamIndex).ToArray());
        var subtitles = result.Streams
            .Where(stream => stream.Kind == MediaStreamKind.Subtitle).ToArray();
        Assert.Equal([3, 4], subtitles.Select(stream => stream.StreamIndex).ToArray());
        Assert.All(subtitles, stream => Assert.Equal("ass", stream.Codec, ignoreCase: true));
        Assert.NotEmpty(result.Streams.Where(stream => stream.Kind == MediaStreamKind.Attachment));
        Assert.False(string.IsNullOrWhiteSpace(result.Fingerprint.VerifiedHash));

        var after = new FileInfo(path);
        Assert.Equal(originalLength, after.Length);
        Assert.Equal(originalWriteTime, after.LastWriteTimeUtc);
    }

    [EnvironmentRequiredFact(TestEnvironmentGate.LiveAnimeFixture, Timeout = 300_000)]
    [Trait("Category", "EnvironmentRequired")]
    [Trait("Environment", "AnimeFixture")]
    public async Task Real_anime_metadata_compiles_only_OP_and_ED_into_an_exact_complement_draft()
    {
        var path = TestEnvironment.Require(TestEnvironmentGate.LiveAnimeFixture);
        var info = new FileInfo(path);
        var originalLength = info.Length;
        var originalWriteTime = info.LastWriteTimeUtc;
        var locator = new FfmpegLocator();
        locator.EnsureAvailable();
        var probe = await new MediaProbeService(locator, new ProcessRunner())
            .ProbeAsync(path, verifyContent: true);
        var frameRate = probe.FrameRate ?? FrameRate.Fps23976;
        var source = new MediaSource(
            Guid.NewGuid(), path, Path.GetFileName(path), MediaKind.Video,
            probe.Duration, true, probe.Width, probe.Height, frameRate,
            probe.Streams.First(item => item.Kind == MediaStreamKind.Video).Codec,
            probe.Streams.First(item => item.Kind == MediaStreamKind.Audio).Codec,
            probe.Fingerprint.Length, probe.Fingerprint.LastWriteUtcTicks,
            probe.Fingerprint.FastHash,
            FastFingerprint: probe.Fingerprint.FastHash,
            VerifiedFingerprint: probe.Fingerprint.VerifiedHash ?? "",
            Streams: probe.Streams,
            IsVariableFrameRate: probe.IsVariableFrameRate);
        var project = ProjectState.CreateNew("live anime exact complement", frameRate);
        var visual = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audioTracks = project.Tracks.Where(item => item.Kind == TrackKind.Audio).OrderBy(item => item.Index).ToArray();
        var subtitleTracks = project.Tracks.Where(item => item.Kind == TrackKind.Subtitle).ToList();
        while (subtitleTracks.Count < 2)
        {
            var track = new TimelineTrack(Guid.NewGuid(), TrackKind.Subtitle, subtitleTracks.Count, $"S{subtitleTracks.Count + 1}");
            subtitleTracks.Add(track);
            project = project with { Tracks = project.Tracks.Add(track) };
        }
        var link = Guid.NewGuid();
        var videoStream = probe.Streams.Single(item => item.Kind == MediaStreamKind.Video);
        var audioStreams = probe.Streams.Where(item => item.Kind == MediaStreamKind.Audio).OrderBy(item => item.StreamIndex).ToArray();
        var subtitleStreams = probe.Streams.Where(item => item.Kind == MediaStreamKind.Subtitle).OrderBy(item => item.StreamIndex).ToArray();
        project = project with
        {
            Sources = project.Sources.Add(source.Id, source),
            MediaClips =
            [
                new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.Zero, TimelineTime.Zero,
                    source.Duration, link, Video: new VideoParameters(), StreamIndex: videoStream.StreamIndex),
                .. audioStreams.Select((stream, index) => new MediaClip(
                    Guid.NewGuid(), source.Id, audioTracks[index].Id, TimelineTime.Zero, TimelineTime.Zero,
                    source.Duration, link, Audio: new AudioParameters(), StreamIndex: stream.StreamIndex))
            ],
            SubtitleClips = subtitleStreams.Select((stream, index) => new SubtitleClip(
                Guid.NewGuid(), source.Id, stream.StreamIndex, subtitleTracks[index].Id,
                TimelineTime.Zero, TimelineTime.Zero, source.Duration, link)).ToImmutableArray(),
            Markers =
            [
                new TimelineMarker(Guid.NewGuid(), MarkerKind.PostCredits, TimelineTime.FromSeconds(1380),
                    TimelineTime.FromSeconds(20), "Post-credit"),
                new TimelineMarker(Guid.NewGuid(), MarkerKind.Preview, TimelineTime.FromSeconds(1400),
                    source.Duration - TimelineTime.FromSeconds(1400), "Preview")
            ]
        };
        project = project.EnsureSequenceContainer();

        var opening = new TimeRange(TimelineTime.FromSeconds(146), TimelineTime.FromSeconds(92));
        var ending = new TimeRange(TimelineTime.FromSeconds(1290), TimelineTime.FromSeconds(90));
        var facts = ImmutableArray.CreateBuilder<TemporalFact>();
        var hypotheses = ImmutableArray.CreateBuilder<SegmentRoleHypothesis>();
        foreach (var candidate in new[] { (opening, SegmentRole.Opening), (ending, SegmentRole.Ending) })
        {
            var audio = new TemporalFact(
                Guid.NewGuid(), source.Id, candidate.Item1, CoverageChannel.Audio,
                TemporalFactKind.Music, $"Measured {candidate.Item2} music", 0.95,
                "live-ground-truth", "1", ImmutableDictionary<string, string>.Empty,
                StreamIndex: audioStreams[0].StreamIndex);
            var ocr = new TemporalFact(
                Guid.NewGuid(), source.Id, candidate.Item1, CoverageChannel.Ocr,
                TemporalFactKind.Ocr, $"Measured {candidate.Item2} credits", 0.95,
                "live-ground-truth", "1", ImmutableDictionary<string, string>.Empty);
            var start = new TemporalFact(
                Guid.NewGuid(), source.Id, new TimeRange(candidate.Item1.Start, frameRate.FrameDuration),
                CoverageChannel.Frames, TemporalFactKind.ShotBoundary, "Measured start boundary", 0.99,
                "live-ground-truth", "1", ImmutableDictionary<string, string>.Empty);
            var end = new TemporalFact(
                Guid.NewGuid(), source.Id, new TimeRange(candidate.Item1.End, frameRate.FrameDuration),
                CoverageChannel.Frames, TemporalFactKind.ShotBoundary, "Measured end boundary", 0.99,
                "live-ground-truth", "1", ImmutableDictionary<string, string>.Empty);
            facts.Add(audio);
            facts.Add(ocr);
            facts.Add(start);
            facts.Add(end);
            var evidence = ImmutableArray.Create(audio.Id, ocr.Id, start.Id, end.Id);
            hypotheses.Add(new SegmentRoleHypothesis(
                Guid.NewGuid(), source.Id, candidate.Item1, candidate.Item2, 0.95,
                new CandidateBoundary(Guid.NewGuid(), source.Id, candidate.Item1.Start, 0.99, evidence),
                new CandidateBoundary(Guid.NewGuid(), source.Id, candidate.Item1.End, 0.99, evidence),
                evidence, [CoverageChannel.Audio, CoverageChannel.Ocr, CoverageChannel.Frames],
                "live-ground-truth", "1"));
        }
        hypotheses.Add(RoleHypothesis(source.Id, SegmentRole.PostCredits, 1380, 20));
        hypotheses.Add(RoleHypothesis(source.Id, SegmentRole.Preview, 1400, source.Duration.TotalSeconds - 1400));
        var coverage = new CoverageMap(
            source.Id, MediaSourceFingerprint.Stable(source), source.Duration,
            ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty)
            .Add(CoverageChannel.Frames, CoverageInterval.Create(opening, 2200, false, "live-ground-truth", "1"))
            .Add(CoverageChannel.Frames, CoverageInterval.Create(ending, 2200, false, "live-ground-truth", "1"));
        var now = DateTimeOffset.UtcNow;
        var index = new MediaUnderstandingIndex(
            Guid.NewGuid(), source.Id, MediaSourceFingerprint.Stable(source), "editorial-v2.1",
            source.Duration, coverage,
            [new AnalyzerManifest("live-ground-truth", "1", "manual", "test", [CoverageChannel.Frames], now)],
            [], [], [], [], facts.ToImmutable(), [], [], now, now,
            SegmentRoleHypotheses: hypotheses.ToImmutable());
        var profile = MontageProfileCatalog.Get(MontageProfileKind.AnimeEpisode);
        var brief = new EditorialBrief(
            Guid.NewGuid(), "remove OP and ED only", "viewer", profile,
            source.Duration - opening.Duration - ending.Duration, "exact complement", [], ["only OP/ED"], now,
            ScopePolicy: EditScopePolicy.AnimeOpeningEndingOnly);
        EditDecision Remove(TimeRange range, SegmentRole role, int order) => new(
            Guid.NewGuid(), EditDecisionKind.Remove, source.Id, range, order, $"Remove {role}", 0.95,
            facts.Where(item => item.SourceRange.Overlaps(range)).Select(item => item.Id).ToImmutableArray(),
            ImmutableDictionary<string, string>.Empty, SegmentRole: role);
        var sequence = project.ActiveSequence!;
        var graph = new MontageGraph(
            Guid.NewGuid(), Guid.NewGuid(), sequence.Id, sequence.Revision, brief,
            [Remove(opening, SegmentRole.Opening, 0), Remove(ending, SegmentRole.Ending, 1)],
            1, now, now);
        var catalog = new MediaUnderstandingCatalog([index]);
        var resolved = new MontageGraphTargetResolver().Resolve(project, graph);
        var validation = new MontageGraphValidatorV2().Validate(project, catalog, resolved);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Concat(validation.Gaps.Select(item => item.Message))));
        var compiled = new MontageGraphCompilerV2().Compile(project, catalog, resolved, []);
        var draft = compiled.Draft.Sequence;
        var quality = new DeterministicDraftQualityAnalyzer().Analyze(project, resolved.TaskId, draft, resolved, []);

        Assert.Equal(2, compiled.RemovedTimelineRanges.Length);
        Assert.InRange(compiled.RemovedTimelineRanges.Sum(item => item.Duration.TotalSeconds), 181.95, 182.05);
        Assert.True(Math.Abs(compiled.RemovedTimelineRanges[0].Start.TotalSeconds - 146) <= frameRate.FrameDuration.TotalSeconds);
        Assert.True(Math.Abs(compiled.RemovedTimelineRanges[0].End.TotalSeconds - 238) <= frameRate.FrameDuration.TotalSeconds);
        Assert.True(Math.Abs(compiled.RemovedTimelineRanges[1].Start.TotalSeconds - 1290) <= frameRate.FrameDuration.TotalSeconds);
        Assert.True(Math.Abs(compiled.RemovedTimelineRanges[1].End.TotalSeconds - 1380) <= frameRate.FrameDuration.TotalSeconds);
        Assert.InRange(draft.Duration.TotalSeconds, 1245.10, 1245.19);
        Assert.Equal([1, 2], draft.MediaClips.Where(item => item.Audio is not null)
            .Select(item => item.StreamIndex!.Value).Distinct().Order().ToArray());
        Assert.Equal([3, 4], draft.SubtitleClips.Select(item => item.StreamIndex).Distinct().Order().ToArray());
        Assert.Contains(draft.Markers, item => item.Kind == MarkerKind.PostCredits);
        Assert.Contains(draft.Markers, item => item.Kind == MarkerKind.Preview);
        Assert.Equal(DraftQualityStatus.Passed, quality.Status);
        var afterFingerprint = await new FileMediaFingerprintService().ComputeVerifiedAsync(path);
        Assert.Equal(probe.Fingerprint.VerifiedHash, afterFingerprint.VerifiedHash);
        Assert.Equal(originalLength, new FileInfo(path).Length);
        Assert.Equal(originalWriteTime, new FileInfo(path).LastWriteTimeUtc);
    }

    private static SegmentRoleHypothesis RoleHypothesis(
        Guid sourceId, SegmentRole role, double startSeconds, double durationSeconds)
    {
        var range = new TimeRange(TimelineTime.FromSeconds(startSeconds), TimelineTime.FromSeconds(durationSeconds));
        return new SegmentRoleHypothesis(
            Guid.NewGuid(), sourceId, range, role, 0.99,
            new CandidateBoundary(Guid.NewGuid(), sourceId, range.Start, 0.99, []),
            new CandidateBoundary(Guid.NewGuid(), sourceId, range.End, 0.99, []),
            [], [CoverageChannel.Frames, CoverageChannel.Audio], "live-ground-truth", "1");
    }
}
