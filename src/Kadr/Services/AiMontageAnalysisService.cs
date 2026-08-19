using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Caching;
using KadrStudio.Application.Jobs;
using KadrStudio.Core.Domain;
using UiMediaAsset = KadrStudio.Models.MediaAsset;
using UiMediaKind = KadrStudio.Models.MediaKind;

namespace KadrStudio.Services;

public sealed class AiMontageAnalysisService(
    AutomationOrchestrator orchestrator,
    AutoSubtitleService subtitles,
    OllamaVideoAnalysisService ollama,
    IArtifactStore artifacts,
    AnimeFingerprintService fingerprintService) : IMediaAnalysisPipeline
{
    public const string PipelineVersion = "content-analysis-v2";
    private const int CacheFormatVersion = 2;

    public async Task<ImmutableDictionary<Guid, MediaAnalysisManifest>> AnalyzeSourcesAsync(
        ProjectState project,
        MediaAnalysisRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ids = request.SourceIds.IsDefaultOrEmpty
            ? project.Sources.Keys.Order().ToArray()
            : request.SourceIds.Distinct().ToArray();
        var result = ImmutableDictionary.CreateBuilder<Guid, MediaAnalysisManifest>();
        for (var index = 0; index < ids.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.Sources.TryGetValue(ids[index], out var source) || source.Kind != MediaKind.Video)
                continue;
            var sourceProgress = new Progress<VideoAnalysisProgress>(value =>
                progress?.Report((index + value.Percent / 100d) / Math.Max(1, ids.Length)));
            var manifest = await AnalyzeSourceAsync(source, request, sourceProgress, cancellationToken)
                .ConfigureAwait(false);
            result[source.Id] = manifest;
            progress?.Report((index + 1d) / Math.Max(1, ids.Length));
        }
        var manifests = result.ToImmutable();
        return request.Profile.Kind == MaterialProfileKind.Anime && manifests.Count > 1
            ? await CorrelateRepeatedSectionsAsync(project, manifests, cancellationToken).ConfigureAwait(false)
            : manifests;
    }

    private async Task<ImmutableDictionary<Guid, MediaAnalysisManifest>> CorrelateRepeatedSectionsAsync(
        ProjectState project,
        ImmutableDictionary<Guid, MediaAnalysisManifest> manifests,
        CancellationToken cancellationToken)
    {
        var candidates = manifests.Values
            .SelectMany(manifest => (manifest.StructuralSegments.IsDefault
                    ? ImmutableArray<StructuralSegment>.Empty
                    : manifest.StructuralSegments)
                .Where(segment => segment.Kind is StructuralSegmentKind.Opening or StructuralSegmentKind.Ending))
            .ToArray();
        var fingerprints = new Dictionary<Guid, AnimeSectionFingerprint>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!project.Sources.TryGetValue(candidate.SourceId, out var source)) continue;
            try
            {
                fingerprints[candidate.Id] = await fingerprintService.CreateAsync(
                    source, candidate.SourceRange, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Fingerprint correlation is corroborating evidence. Vision and
                // frame-boundary review remain available when extraction fails.
            }
        }

        var correlated = manifests;
        foreach (var candidate in candidates)
        {
            if (!fingerprints.TryGetValue(candidate.Id, out var fingerprint)) continue;
            var best = candidates
                .Where(other => other.Id != candidate.Id && other.SourceId != candidate.SourceId &&
                                other.Kind == candidate.Kind && fingerprints.ContainsKey(other.Id))
                .Select(other => AnimeFingerprintService.Similarity(fingerprint, fingerprints[other.Id]))
                .DefaultIfEmpty(0)
                .Max();
            if (best < 0.72) continue;
            var evidence = new AnalysisEvidence(
                MontageEvidenceKind.Technical,
                $"Повторяющийся блок подтверждён визуально-звуковым отпечатком другой серии ({best:P0}).",
                $"anime-fingerprint:{best:0.000}");
            var manifest = correlated[candidate.SourceId];
            var segments = manifest.StructuralSegments.Select(segment => segment.Id == candidate.Id
                ? segment with
                {
                    Confidence = Math.Clamp(segment.Confidence + best * 0.12, 0, 0.99),
                    Disposition = segment.HasConfirmedBoundaries
                        ? StructuralSegmentDisposition.Remove
                        : segment.Disposition,
                    Evidence = segment.Evidence.Add(evidence)
                }
                : segment).ToImmutableArray();
            correlated = correlated.SetItem(candidate.SourceId, manifest with { StructuralSegments = segments });
        }
        return correlated;
    }

    private async Task<MediaAnalysisManifest> AnalyzeSourceAsync(
        MediaSource source,
        MediaAnalysisRequest request,
        IProgress<VideoAnalysisProgress> progress,
        CancellationToken cancellationToken)
    {
        var fingerprint = MontagePlanValidator.StableFingerprint(source);
        var cacheFingerprint = string.Join('|', fingerprint, PipelineVersion, request.Profile.Id,
            request.Profile.Version, request.Model, request.DeepAnalysis);
        var key = new MediaCacheKey(
            source.Id, cacheFingerprint, MediaArtifactKind.AnalysisManifest, 0, 0, CacheFormatVersion);
        var cached = await artifacts.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is { } payload)
        {
            var restored = JsonSerializer.Deserialize<MediaAnalysisManifest>(payload.Span);
            if (restored is not null) return restored;
        }

        var asset = ToUiAsset(source);
        var baseline = await GetOrCreateBaselineAsync(
            source, fingerprint, request, progress, cancellationToken).ConfigureAwait(false);
        var segments = baseline.Segments.ToBuilder();
        var structural = baseline.StructuralSegments.IsDefault
            ? ImmutableArray<StructuralSegment>.Empty
            : baseline.StructuralSegments;

        if (request.DeepAnalysis && !string.IsNullOrWhiteSpace(request.Model))
        {
            var technicalResult = RestoreTechnicalResult(source, baseline.Segments);
            if (request.Profile.Kind == MaterialProfileKind.Anime)
            {
                var enhanced = await orchestrator.EnhanceStructureAsync(
                    asset,
                    technicalResult,
                    BuildBaselineQuery(request.Profile),
                    request.Model,
                    progress,
                    cancellationToken,
                    request.IsBackground ? JobPriority.Background : JobPriority.UserInitiated).ConfigureAwait(false);
                structural = BuildStructuralSegments(
                    source, enhanced.Result.Ranges, hasVisionEvidence: enhanced.Warning is null);
                segments.AddRange(enhanced.Result.Ranges
                    .Where(range => IsStructural(range.Kind))
                    .Select(range => ToAnalysisSegment(source.Id, range, MontageEvidenceKind.Vision)));
            }
            else
            {
                var semantic = await ollama.AnalyzeMaterialAsync(
                    asset, technicalResult, request.Profile, request.Model, progress, cancellationToken)
                    .ConfigureAwait(false);
                segments.AddRange(semantic);
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(new AnalysisSegment(
                Guid.NewGuid(), source.Id,
                new TimeRange(TimelineTime.Zero, source.Duration),
                0.3, 0.3, 0, string.Empty,
                ImmutableDictionary<string, double>.Empty.Add("unclassified", 0.3),
                0.3,
                [new AnalysisEvidence(MontageEvidenceKind.Technical, "Исходник доступен, но заметные события не найдены.")]));
        }

        var manifest = new MediaAnalysisManifest(
            source.Id,
            fingerprint,
            PipelineVersion,
            string.IsNullOrWhiteSpace(request.Model) ? "technical+whisper" : request.Model,
            request.Profile.Id,
            request.Profile.Version,
            DateTimeOffset.UtcNow,
            segments.OrderBy(item => item.SourceRange.Start).ThenBy(item => item.Id).ToImmutableArray(),
            structural);
        await artifacts.PutAsync(key, JsonSerializer.SerializeToUtf8Bytes(manifest), cancellationToken)
            .ConfigureAwait(false);
        return manifest;
    }

    private async Task<MediaAnalysisManifest> GetOrCreateBaselineAsync(
        MediaSource source,
        string fingerprint,
        MediaAnalysisRequest request,
        IProgress<VideoAnalysisProgress> progress,
        CancellationToken cancellationToken)
    {
        var baselineFingerprint = string.Join('|', fingerprint, PipelineVersion, "technical-whisper", request.Profile.Kind);
        var key = new MediaCacheKey(
            source.Id, baselineFingerprint, MediaArtifactKind.AnalysisManifest, 0, 0, CacheFormatVersion);
        var cached = await artifacts.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is { } payload)
        {
            var restored = JsonSerializer.Deserialize<MediaAnalysisManifest>(payload.Span);
            if (restored is not null) return restored;
        }

        var asset = ToUiAsset(source);
        var pipeline = await orchestrator.AnalyzeAsync(
            new VideoAnalysisRequest(
                asset, 0, source.Duration.TotalSeconds, BuildBaselineQuery(request.Profile)),
            ollamaModel: null,
            progress,
            cancellationToken,
            request.IsBackground ? JobPriority.Background : JobPriority.UserInitiated).ConfigureAwait(false);
        var segments = ImmutableArray.CreateBuilder<AnalysisSegment>();
        foreach (var range in pipeline.Result.Ranges)
            segments.Add(ToAnalysisSegment(source.Id, range, MontageEvidenceKind.Technical));

        if (source.HasAudio)
        {
            try
            {
                var transcription = await subtitles.TranscribeLocalAsync(
                    asset, 0, source.Duration.TotalSeconds, cancellationToken).ConfigureAwait(false);
                foreach (var cue in transcription.Cues)
                {
                    var start = Math.Clamp(cue.Start, 0, source.Duration.TotalSeconds);
                    var end = Math.Clamp(cue.End, start, source.Duration.TotalSeconds);
                    if (end <= start + 0.05 || string.IsNullOrWhiteSpace(cue.Text)) continue;
                    segments.Add(new AnalysisSegment(
                        Guid.NewGuid(), source.Id,
                        new TimeRange(TimelineTime.FromSeconds(start), TimelineTime.FromSeconds(end - start)),
                        0.25, 0.55, 1, cue.Text.Trim(),
                        ImmutableDictionary<string, double>.Empty.Add("speech", 1),
                        0.92,
                        [new AnalysisEvidence(MontageEvidenceKind.Transcript, cue.Text.Trim(), transcription.Engine)]));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Speech is an optional evidence channel; technical evidence remains usable.
            }
        }

        if (segments.Count == 0)
        {
            segments.Add(new AnalysisSegment(
                Guid.NewGuid(), source.Id,
                new TimeRange(TimelineTime.Zero, source.Duration),
                0.3, 0.3, 0, string.Empty,
                ImmutableDictionary<string, double>.Empty.Add("unclassified", 0.3),
                0.3,
                [new AnalysisEvidence(MontageEvidenceKind.Technical, "Исходник доступен, но заметные события не найдены.")]));
        }

        var baseline = new MediaAnalysisManifest(
            source.Id, fingerprint, PipelineVersion, "technical+whisper", "technical", 1,
            DateTimeOffset.UtcNow,
            segments.OrderBy(item => item.SourceRange.Start).ThenBy(item => item.Id).ToImmutableArray(),
            request.Profile.Kind == MaterialProfileKind.Anime
                ? BuildStructuralSegments(source, pipeline.Result.Ranges, hasVisionEvidence: false)
                : []);
        await artifacts.PutAsync(key, JsonSerializer.SerializeToUtf8Bytes(baseline), cancellationToken)
            .ConfigureAwait(false);
        return baseline;
    }

    private static VideoAnalysisResult RestoreTechnicalResult(
        MediaSource source,
        ImmutableArray<AnalysisSegment> segments)
    {
        var ranges = new List<DetectedVideoRange>();
        foreach (var segment in segments)
        {
            var tag = segment.Tags.Keys.FirstOrDefault(key =>
                Enum.TryParse<Models.MarkerKind>(key, ignoreCase: true, out _));
            if (tag is null || !Enum.TryParse<Models.MarkerKind>(tag, ignoreCase: true, out var kind))
                continue;
            var evidence = segment.Evidence.FirstOrDefault(item => item.Kind == MontageEvidenceKind.Technical);
            ranges.Add(new DetectedVideoRange(
                kind,
                segment.SourceRange.Start.TotalSeconds,
                segment.SourceRange.Duration.TotalSeconds,
                string.IsNullOrWhiteSpace(evidence?.Reference) ? kind.ToString() : evidence.Reference,
                evidence?.Summary ?? string.Empty,
                segment.Confidence));
        }
        return new VideoAnalysisResult(
            "Технические границы восстановлены из кэша анализа.",
            0,
            source.Duration.TotalSeconds,
            ranges);
    }

    private static string BuildBaselineQuery(GameEditingProfile? profile = null)
        => profile?.Kind == MaterialProfileKind.Anime
            ? "Аниме: найди опенинг, эндинг, титры, рекап и превью следующей серии; сохрани сюжетные сцены."
            : "Универсальный анализ видеоматериала: сцены, действия, движение, речь, эмоции, тишина и результат.";

    private static AnalysisSegment ToAnalysisSegment(
        Guid sourceId,
        DetectedVideoRange range,
        MontageEvidenceKind evidenceKind)
    {
        var kind = range.Kind.ToString().ToLowerInvariant();
        var motion = range.Kind switch
        {
            Models.MarkerKind.Scene => 0.72,
            Models.MarkerKind.Freeze or Models.MarkerKind.BlackFrame => 0.05,
            _ => 0.42
        };
        var loudness = range.Kind == Models.MarkerKind.Silence ? 0.02 : 0.5;
        return new AnalysisSegment(
            Guid.NewGuid(), sourceId,
            new TimeRange(
                TimelineTime.FromSeconds(range.SourceStart),
                TimelineTime.FromSeconds(range.Duration)),
            motion, loudness, 0, string.Empty,
            ImmutableDictionary<string, double>.Empty.Add(kind, range.Confidence),
            range.Confidence,
            [new AnalysisEvidence(evidenceKind, range.Description, range.Title)]);
    }

    private static ImmutableArray<StructuralSegment> BuildStructuralSegments(
        MediaSource source,
        IEnumerable<DetectedVideoRange> ranges,
        bool hasVisionEvidence)
    {
        var output = ImmutableArray.CreateBuilder<StructuralSegment>();
        foreach (var range in ranges.Where(item => IsStructural(item.Kind)))
        {
            var kind = ToStructuralKind(range.Kind);
            if (kind == StructuralSegmentKind.Unknown) continue;
            var startTime = TimelineTime.FromSeconds(Math.Clamp(range.SourceStart, 0, source.Duration.TotalSeconds));
            var endTime = TimelineTime.FromSeconds(Math.Clamp(
                range.SourceStart + range.Duration, startTime.TotalSeconds, source.Duration.TotalSeconds));
            if (endTime <= startTime) continue;
            var evidenceKind = hasVisionEvidence ? MontageEvidenceKind.Vision : MontageEvidenceKind.Technical;
            var evidence = ImmutableArray.Create(new AnalysisEvidence(
                evidenceKind, range.Description, range.Title));
            var confidence = hasVisionEvidence ? range.Confidence : Math.Min(range.Confidence, 0.72);
            output.Add(new StructuralSegment(
                Guid.NewGuid(),
                source.Id,
                kind,
                new TimeRange(startTime, endTime - startTime),
                DispositionFor(kind, confidence, hasVisionEvidence),
                confidence,
                ToBoundary(source, startTime, range.StartBoundary, confidence, hasVisionEvidence, evidence),
                ToBoundary(source, endTime, range.EndBoundary, confidence, hasVisionEvidence, evidence),
                evidence));
        }
        return output
            .GroupBy(item => item.Kind)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .OrderBy(item => item.SourceRange.Start)
            .ToImmutableArray();
    }

    private static ResolvedBoundary ToBoundary(
        MediaSource source,
        TimelineTime time,
        BoundaryVerificationResult? verification,
        double confidence,
        bool hasVisionEvidence,
        ImmutableArray<AnalysisEvidence> evidence)
    {
        var isSourceEdge = time == TimelineTime.Zero || time == source.Duration;
        var hasCut = verification?.HasUnambiguousCandidate == true;
        var verified = hasVisionEvidence && confidence >= 0.75 && (hasCut || isSourceEdge);
        return new ResolvedBoundary(
            time,
            time,
            verified ? BoundaryResolutionStatus.Verified : BoundaryResolutionStatus.Suggested,
            verified
                ? source.IsVariableFrameRate ? BoundaryPrecision.ExactPresentationTimestamp : BoundaryPrecision.Frame
                : BoundaryPrecision.Coarse,
            verified ? confidence : Math.Min(confidence, 0.74),
            evidence);
    }

    private static StructuralSegmentDisposition DispositionFor(
        StructuralSegmentKind kind,
        double confidence,
        bool hasVisionEvidence)
    {
        if (!hasVisionEvidence || confidence < 0.75)
            return StructuralSegmentDisposition.NeedsInput;
        return kind switch
        {
            StructuralSegmentKind.Opening or StructuralSegmentKind.Ending or
                StructuralSegmentKind.Recap or StructuralSegmentKind.Preview or StructuralSegmentKind.Credits
                => StructuralSegmentDisposition.Remove,
            StructuralSegmentKind.Story or StructuralSegmentKind.PostCreditsStory
                => StructuralSegmentDisposition.Retain,
            _ => StructuralSegmentDisposition.NeedsInput
        };
    }

    private static bool IsStructural(Models.MarkerKind kind)
        => kind is Models.MarkerKind.Opening or Models.MarkerKind.Ending or Models.MarkerKind.PostCredits or
            Models.MarkerKind.Preview or Models.MarkerKind.Recap;

    private static StructuralSegmentKind ToStructuralKind(Models.MarkerKind kind) => kind switch
    {
        Models.MarkerKind.Opening => StructuralSegmentKind.Opening,
        Models.MarkerKind.Ending => StructuralSegmentKind.Ending,
        Models.MarkerKind.Recap => StructuralSegmentKind.Recap,
        Models.MarkerKind.Preview => StructuralSegmentKind.Preview,
        Models.MarkerKind.PostCredits => StructuralSegmentKind.PostCreditsStory,
        _ => StructuralSegmentKind.Unknown
    };

    private static UiMediaAsset ToUiAsset(MediaSource source)
        => new()
        {
            Id = source.Id,
            Path = source.Path,
            Name = source.Name,
            Kind = UiMediaKind.Video,
            Duration = source.Duration.TotalSeconds,
            Width = source.Width,
            Height = source.Height,
            FrameRate = source.FrameRate?.FramesPerSecond ?? 0,
            HasAudio = source.HasAudio,
            VideoCodec = source.VideoCodec,
            AudioCodec = source.AudioCodec,
            FileSizeBytes = source.FileSize,
            IsMissing = source.OnlineState != MediaOnlineState.Online
        };
}
