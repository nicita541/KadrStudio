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
    AiVideoAnalysisService aiServer,
    IArtifactStore artifacts) : IMediaAnalysisPipeline
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
        return manifests;
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
            var semantic = await aiServer.AnalyzeMaterialAsync(
                asset, technicalResult, request.Profile, request.Model, progress, cancellationToken)
                .ConfigureAwait(false);
            segments.AddRange(semantic);
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
        var baselineFingerprint = string.Join('|', fingerprint, PipelineVersion, "technical-whisper");
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
            aiModel: null,
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
            []);
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
    {
        _ = profile; // Compatibility preset may tune technical thresholds outside the AI prompt.
        return "Универсальный технический анализ видеоматериала: смены сцен, движение, речь, звук, тишина, чёрные и замершие кадры.";
    }

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
