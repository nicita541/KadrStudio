using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Editorial;

public sealed class AiServerMediaUnderstandingIndexer(
    AiServerV2Client client,
    AnalysisProxyBuilder proxies) : IMediaUnderstandingIndexer, IEvidenceGapResolver
{
    public const string PipelineVersion = "editorial-v2.1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MediaUnderstandingCatalog> EnsureIndexesAsync(
        ProjectState project,
        Guid sourceSequenceId,
        MontageProfile profile,
        IProgress<EditorialPipelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sequence = project.FindSequence(sourceSequenceId)
            ?? throw new InvalidOperationException("Source sequence was not found for indexing.");
        var sourceIds = sequence.MediaClips.Select(item => item.SourceId).Distinct().ToArray();
        var results = new List<MediaUnderstandingIndex>(sourceIds.Length);
        var assets = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sourceIds.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = project.Sources[sourceIds[index]];
            progress?.Report(new EditorialPipelineProgress(
                EditorialPipelineStage.Indexing,
                index / (double)Math.Max(1, sourceIds.Length),
                $"Индексирую «{source.Name}» ({index + 1}/{sourceIds.Length})."));
            var fingerprint = MediaSourceFingerprint.Stable(source);
            var reusable = project.UnderstandingIndexes
                .Where(item => item.SourceId == source.Id &&
                               item.SourceFingerprint == fingerprint &&
                               item.PipelineVersion == PipelineVersion)
                .OrderByDescending(item => item.UpdatedAt)
                .FirstOrDefault(item => HasProfileCoverage(item, source, profile));
            if (reusable is not null)
            {
                results.Add(reusable);
                if (!string.IsNullOrWhiteSpace(reusable.ArtifactReference))
                    artifacts.Add(reusable.ArtifactReference);
                continue;
            }
            var analysis = await AnalyzeSourceAsync(
                source, profile, [], progress, cancellationToken).ConfigureAwait(false);
            results.Add(analysis.Index);
            assets.UnionWith(analysis.AssetIds);
            jobs.UnionWith(analysis.JobIds);
            artifacts.UnionWith(analysis.ArtifactIds);
        }
        return new MediaUnderstandingCatalog(
            results.ToImmutableArray(), assets.ToImmutableArray(),
            jobs.ToImmutableArray(), artifacts.ToImmutableArray());
    }

    public async Task<MediaUnderstandingCatalog> ResolveAsync(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        ImmutableArray<EvidenceGap> gaps,
        IProgress<EditorialPipelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var updated = indexes.Indexes.ToDictionary(item => item.SourceId);
        var assets = indexes.AssetIds.ToHashSet(StringComparer.Ordinal);
        var jobs = indexes.JobIds.ToHashSet(StringComparer.Ordinal);
        var artifacts = indexes.ArtifactIds.ToHashSet(StringComparer.Ordinal);
        foreach (var sourceGaps in gaps.GroupBy(item => item.SourceId))
        {
            if (!project.Sources.TryGetValue(sourceGaps.Key, out var source))
                throw new InvalidOperationException("EvidenceGap references an unknown source.");
            var current = updated.TryGetValue(source.Id, out var existing) ? existing : null;
            var animeEvidence = current?.SegmentRoleHypotheses.Any() == true || sourceGaps.Any(gap =>
                gap.Message.Contains("opening", StringComparison.OrdinalIgnoreCase) ||
                gap.Message.Contains("ending", StringComparison.OrdinalIgnoreCase));
            var profile = MontageProfileCatalog.Get(animeEvidence
                ? MontageProfileKind.AnimeEpisode
                : MontageProfileKind.Generic);
            var measured = await AnalyzeSourceAsync(
                source, profile, sourceGaps.ToImmutableArray(), progress, cancellationToken).ConfigureAwait(false);
            updated[source.Id] = current is null ? measured.Index : Merge([current, measured.Index]);
            assets.UnionWith(measured.AssetIds);
            jobs.UnionWith(measured.JobIds);
            artifacts.UnionWith(measured.ArtifactIds);
        }
        return new MediaUnderstandingCatalog(
            updated.Values.OrderBy(item => item.SourceId).ToImmutableArray(),
            assets.ToImmutableArray(), jobs.ToImmutableArray(), artifacts.ToImmutableArray());
    }

    private async Task<AnalysisRun> AnalyzeSourceAsync(
        MediaSource source,
        MontageProfile profile,
        ImmutableArray<EvidenceGap> gaps,
        IProgress<EditorialPipelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var bundle = await proxies.BuildAsync(source, cancellationToken).ConfigureAwait(false);
        var assetIds = new List<string>();
        var assetDescriptors = new List<AnalysisAssetDescriptor>();
        for (var index = 0; index < bundle.Files.Count; index++)
        {
            var proxy = bundle.Files[index];
            var assetId = await client.UploadAssetAsync(
                proxy.Path,
                proxy.MediaType,
                new Progress<double>(value => progress?.Report(new EditorialPipelineProgress(
                    EditorialPipelineStage.Indexing,
                    value * 0.1,
                    $"Загружаю безопасный analysis proxy: {proxy.Kind}."))),
                cancellationToken).ConfigureAwait(false);
            assetIds.Add(assetId);
            assetDescriptors.Add(new AnalysisAssetDescriptor(
                assetId,
                proxy.Kind,
                proxy.ChunkOrder,
                proxy.Kind == "audio-chunk"
                    ? TimelineTime.FromSeconds(proxy.ChunkOrder * 600d).Ticks
                    : 0,
                proxy.StreamIndex));
            progress?.Report(new EditorialPipelineProgress(
                EditorialPipelineStage.Indexing, 0.1,
                $"Analysis asset {assetId[..12]} готов.",
                AssetIds: assetIds.ToImmutableArray()));
        }

        var analyzers = SelectAnalyzers(source, profile, gaps);
        var primaryAsrStreamIndex = source.Streams
            .Where(item => item.Kind == MediaStreamKind.Audio)
            .OrderByDescending(item => item.Language.Equals("rus", StringComparison.OrdinalIgnoreCase) ||
                                       item.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.IsDefault)
            .Select(item => (int?)item.StreamIndex)
            .FirstOrDefault();
        var jobs = new List<Guid>(analyzers.Length);
        foreach (var analyzer in analyzers)
        {
            var relevantGaps = gaps
                .Where(gap => AnalyzerFor(gap.Channel).Equals(analyzer, StringComparison.OrdinalIgnoreCase) ||
                              gap.RecommendedAnalyzer.Equals(analyzer, StringComparison.OrdinalIgnoreCase))
                .Select(gap => new
                {
                    channel = gap.Channel.ToString(),
                    startTicks = gap.SourceRange.Start.Ticks,
                    durationTicks = gap.SourceRange.Duration.Ticks,
                    gap.MinimumDensityHz,
                    gap.RequireContinuousCoverage
                })
                .ToArray();
            var jobId = await client.StartJobAsync(
                analyzer,
                "2",
                assetIds,
                new
                {
                    sourceId = source.Id,
                    sourceFingerprint = MediaSourceFingerprint.Stable(source),
                    sourceDurationTicks = source.Duration.Ticks,
                    pipelineVersion = PipelineVersion,
                    profile = profile.Id,
                    assets = assetDescriptors.Select(item => new
                    {
                        id = item.AssetId,
                        kind = item.Kind,
                        order = item.Order,
                        startTicks = item.StartTicks,
                        streamIndex = item.StreamIndex
                    }),
                    primaryAsrStreamIndex,
                    gaps = relevantGaps
                },
                cancellationToken).ConfigureAwait(false);
            jobs.Add(jobId);
            progress?.Report(new EditorialPipelineProgress(
                EditorialPipelineStage.Indexing, 0.1,
                $"Analyzer {analyzer} поставлен в очередь.",
                AssetIds: assetIds.ToImmutableArray(),
                JobIds: jobs.Select(item => item.ToString("N")).ToImmutableArray()));
        }

        var partials = new List<MediaUnderstandingIndex>();
        var artifactIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var jobId in jobs)
        {
            var job = await client.WaitForJobAsync(
                jobId,
                new Progress<double>(value => progress?.Report(new EditorialPipelineProgress(
                    EditorialPipelineStage.Indexing, 0.1 + value * 0.9,
                    $"Analyzer job {jobId:N}: {value:P0}."))),
                cancellationToken).ConfigureAwait(false);
            foreach (var artifactId in job.ArtifactIds)
            {
                artifactIds.Add(artifactId);
                using var artifact = await client.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
                var root = artifact.RootElement;
                if (root.TryGetProperty("index", out var wrapped)) root = wrapped;
                try
                {
                    var partial = root.Deserialize<MediaUnderstandingIndex>(Json);
                    if (partial is not null && partial.SourceId == source.Id)
                        partials.Add(partial with { ArtifactReference = artifactId });
                }
                catch (JsonException)
                {
                    // Other structured artifacts remain addressable but are not an index fragment.
                }
            }
            progress?.Report(new EditorialPipelineProgress(
                EditorialPipelineStage.Indexing, job.Progress,
                $"Analyzer job {jobId:N} сохранил артефакты.",
                AssetIds: assetIds.ToImmutableArray(),
                JobIds: jobs.Select(item => item.ToString("N")).ToImmutableArray(),
                ArtifactIds: artifactIds.ToImmutableArray()));
        }
        if (partials.Count == 0)
            throw new InvalidDataException("Analyzer jobs completed without a MediaUnderstandingIndex artifact.");
        var merged = Merge(partials);
        if (!merged.SourceFingerprint.Equals(MediaSourceFingerprint.Stable(source), StringComparison.Ordinal))
            throw new InvalidDataException("Analyzer returned an index for a stale source fingerprint.");
        return new AnalysisRun(
            merged,
            assetIds.Distinct(StringComparer.Ordinal).ToImmutableArray(),
            jobs.Select(item => item.ToString("N")).ToImmutableArray(),
            artifactIds.ToImmutableArray());
    }

    private static ImmutableArray<string> SelectAnalyzers(
        MediaSource source,
        MontageProfile profile,
        ImmutableArray<EvidenceGap> gaps)
    {
        if (!gaps.IsDefaultOrEmpty)
            return gaps.Select(item => item.RecommendedAnalyzer)
                .Where(item => !string.IsNullOrWhiteSpace(item) && item is not "critic")
                .Concat(gaps.Select(item => AnalyzerFor(item.Channel)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        var analyzers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source.Kind is MediaKind.Video or MediaKind.Image) analyzers.Add("video-understanding");
        if (source.HasAudio || source.Kind == MediaKind.Audio) analyzers.Add("audio-events");
        if ((source.HasAudio || source.Kind == MediaKind.Audio) &&
            profile.RequiredChannels.Contains(CoverageChannel.Transcript))
            analyzers.Add("asr-align");
        if ((source.HasAudio || source.Kind == MediaKind.Audio) &&
            profile.Kind is MontageProfileKind.Podcast or MontageProfileKind.TalkingHead or MontageProfileKind.FilmSeries)
            analyzers.Add("diarization");
        return analyzers.OrderBy(item => item, StringComparer.Ordinal).ToImmutableArray();
    }

    private static string AnalyzerFor(CoverageChannel channel)
        => channel switch
        {
            CoverageChannel.Audio => "audio-events",
            CoverageChannel.Transcript => "asr-align",
            _ => "video-understanding"
        };

    private static bool HasProfileCoverage(
        MediaUnderstandingIndex index,
        MediaSource source,
        MontageProfile profile)
    {
        var complete = new TimeRange(TimelineTime.Zero, source.Duration);
        return profile.RequiredChannels
            .Where(channel => IsAvailable(source, channel))
            .All(channel => index.Coverage.Covers(
                channel,
                complete,
                requireContinuous: channel is CoverageChannel.Audio or CoverageChannel.Transcript));
    }

    private static bool IsAvailable(MediaSource source, CoverageChannel channel)
        => channel switch
        {
            CoverageChannel.Audio or CoverageChannel.Transcript => source.HasAudio || source.Kind == MediaKind.Audio,
            CoverageChannel.Frames or CoverageChannel.Motion or CoverageChannel.Ocr => source.Kind is MediaKind.Video or MediaKind.Image,
            _ => false
        };

    private static MediaUnderstandingIndex Merge(IEnumerable<MediaUnderstandingIndex> source)
    {
        var fragments = source.ToArray();
        if (fragments.Length == 0) throw new ArgumentException("At least one index fragment is required.", nameof(source));
        var first = fragments[0];
        if (fragments.Any(item => item.SourceId != first.SourceId ||
                                  item.SourceFingerprint != first.SourceFingerprint ||
                                  item.SourceDuration != first.SourceDuration))
            throw new InvalidDataException("Analyzer index fragments describe different media.");
        var channels = fragments
            .SelectMany(item => item.Coverage.Channels)
            .GroupBy(item => item.Key)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.SelectMany(item => item.Value)
                    .Distinct()
                    .OrderBy(item => item.Range.Start)
                    .ToImmutableArray());
        var facts = Distinct(fragments.SelectMany(item => item.Facts), item => item.Id);
        var segmentRoles = Distinct(
                fragments.SelectMany(item => item.SegmentRoleHypotheses), item => item.Id)
            .Select(hypothesis => EnrichSegmentRole(hypothesis, facts))
            .ToImmutableArray();
        return first with
        {
            Id = Guid.NewGuid(),
            PipelineVersion = PipelineVersion,
            Coverage = first.Coverage with { Channels = channels },
            Analyzers = Distinct(fragments.SelectMany(item => item.Analyzers), item => $"{item.Id}|{item.Version}"),
            Chapters = Distinct(fragments.SelectMany(item => item.Chapters), item => item.Id),
            Scenes = Distinct(fragments.SelectMany(item => item.Scenes), item => item.Id),
            Shots = Distinct(fragments.SelectMany(item => item.Shots), item => item.Id),
            Moments = Distinct(fragments.SelectMany(item => item.Moments), item => item.Id),
            Facts = facts,
            AudioEvents = Distinct(fragments.SelectMany(item => item.AudioEvents), item => item.Id),
            SpeakerTurns = Distinct(fragments.SelectMany(item => item.SpeakerTurns), item => item.Id),
            TranscriptWords = fragments
                .SelectMany(item => item.TranscriptWords)
                .Distinct()
                .OrderBy(item => item.SourceRange.Start)
                .ToImmutableArray(),
            SemanticHypotheses = Distinct(
                fragments.SelectMany(item => item.SemanticHypotheses), item => item.Id),
            SegmentRoleHypotheses = segmentRoles,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static SegmentRoleHypothesis EnrichSegmentRole(
        SegmentRoleHypothesis hypothesis,
        ImmutableArray<TemporalFact> facts)
    {
        var supporting = facts.Where(item =>
                item.SourceId == hypothesis.SourceId &&
                item.SourceRange.Overlaps(hypothesis.SourceRange) &&
                (item.Channel is CoverageChannel.Frames or CoverageChannel.Ocr ||
                 item.Channel == CoverageChannel.Audio && item.Kind == TemporalFactKind.Music))
            .ToArray();
        var boundaryTolerance = TimelineTime.FromSeconds(2);
        ImmutableArray<Guid> BoundaryEvidence(TimelineTime boundary) => facts.Where(item =>
                item.SourceId == hypothesis.SourceId && item.Kind == TemporalFactKind.ShotBoundary &&
                Distance(item.SourceRange.Start, boundary) <= boundaryTolerance)
            .Select(item => item.Id)
            .ToImmutableArray();
        var startFacts = BoundaryEvidence(hypothesis.StartBoundary.Time);
        var endFacts = BoundaryEvidence(hypothesis.EndBoundary.Time);
        return hypothesis with
        {
            EvidenceFactIds = hypothesis.EvidenceFactIds
                .Concat(supporting.Select(item => item.Id)).Distinct().ToImmutableArray(),
            EvidenceChannels = hypothesis.EvidenceChannels
                .Concat(supporting.Select(item => item.Channel)).Distinct().ToImmutableArray(),
            StartBoundary = hypothesis.StartBoundary with
            {
                EvidenceFactIds = hypothesis.StartBoundary.EvidenceFactIds.Concat(startFacts).Distinct().ToImmutableArray()
            },
            EndBoundary = hypothesis.EndBoundary with
            {
                EvidenceFactIds = hypothesis.EndBoundary.EvidenceFactIds.Concat(endFacts).Distinct().ToImmutableArray()
            }
        };
    }

    private static TimelineTime Distance(TimelineTime left, TimelineTime right)
        => left >= right ? left - right : right - left;

    private static ImmutableArray<T> Distinct<T, TKey>(IEnumerable<T> source, Func<T, TKey> key)
        where TKey : notnull
        => source.GroupBy(key).Select(group => group.OrderByDescending(item => item is AnalyzerManifest manifest
                ? manifest.CreatedAt
                : DateTimeOffset.MinValue).First()).ToImmutableArray();

    private sealed record AnalysisAssetDescriptor(
        string AssetId,
        string Kind,
        int Order,
        long StartTicks,
        int? StreamIndex);

    private sealed record AnalysisRun(
        MediaUnderstandingIndex Index,
        ImmutableArray<string> AssetIds,
        ImmutableArray<string> JobIds,
        ImmutableArray<string> ArtifactIds);
}
