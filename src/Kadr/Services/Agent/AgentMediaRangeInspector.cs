using System.Text.Json;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;
using KadrStudio.Application.Caching;
using KadrStudio.Application.Jobs;
using KadrStudio.Application.Media;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using UiMediaAsset = KadrStudio.Models.MediaAsset;
using UiMediaKind = KadrStudio.Models.MediaKind;
using UiMarkerKind = KadrStudio.Models.MarkerKind;

namespace KadrStudio.Services.Agent;

/// <summary>
/// Executes focused, read-only media inspection for the agent.
/// Results are cached by source fingerprint + exact range + detail + query.
/// </summary>
public sealed class AgentMediaRangeInspector(
    AutomationOrchestrator orchestrator,
    AutoSubtitleService subtitles,
    AiVideoAnalysisService aiServer,
    IArtifactStore artifacts) : IAgentMediaRangeInspector
{
    private const int CacheFormatVersion = 1;
    private const double MaximumTechnicalSeconds = 1_800;
    private const double MaximumVisionSeconds = 600;
    private const int MaximumQueryCharacters = 2_000;
    private const int MaximumRanges = 120;
    private const int MaximumTranscriptCues = 200;

    public async ValueTask<JsonElement> InspectAsync(
        MediaSource source,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        if (source.OnlineState != MediaOnlineState.Online || !File.Exists(source.Path))
        {
            throw new AgentToolRejectedException(
                "media_offline",
                $"Media '{source.Name}' is not available on disk.");
        }

        var start = Math.Clamp(request.StartSeconds, 0, source.Duration.TotalSeconds);
        var end = Math.Clamp(request.EndSeconds, 0, source.Duration.TotalSeconds);
        if (start >= source.Duration.TotalSeconds || end <= start + 0.01)
        {
            throw new AgentToolRejectedException(
                "range_out_of_bounds",
                "Requested media range does not overlap the source.");
        }

        var normalizedQuery = (request.Query ?? string.Empty).Trim();
        if (normalizedQuery.Length > MaximumQueryCharacters)
        {
            throw new AgentToolRejectedException(
                "query_too_long",
                $"Range query exceeds {MaximumQueryCharacters} characters.");
        }

        var duration = end - start;
        var limit = request.Detail is AgentRangeInspectionDetail.Frames or AgentRangeInspectionDetail.All
            ? MaximumVisionSeconds
            : MaximumTechnicalSeconds;
        if (duration > limit)
        {
            throw new AgentToolRejectedException(
                "range_too_large",
                $"Requested {duration:0.###}s range is too large for '{request.Detail}'. " +
                $"Inspect it in chunks no longer than {limit:0}s.");
        }

        ValidateDetailAgainstMedia(source, request.Detail);

        var normalized = request with
        {
            TargetKind = AgentRangeTargetKind.Media,
            TargetId = source.Id,
            StartSeconds = start,
            EndSeconds = end,
            Query = normalizedQuery
        };

        var key = BuildCacheKey(source, normalized);
        var cached = await artifacts.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
        if (cached is { } payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Corrupt/old agent observations are disposable cache entries.
            }
        }

        var observation = await BuildObservationAsync(source, normalized, cancellationToken)
            .ConfigureAwait(false);

        await artifacts.PutAsync(
            key,
            JsonSerializer.SerializeToUtf8Bytes(observation),
            cancellationToken).ConfigureAwait(false);

        return observation.Clone();
    }

    private async Task<JsonElement> BuildObservationAsync(
        MediaSource source,
        AgentRangeInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var asset = ToUiAsset(source);
        var start = request.StartSeconds;
        var end = request.EndSeconds;
        var duration = end - start;
        var useVision = request.Detail is AgentRangeInspectionDetail.Frames or AgentRangeInspectionDetail.All;
        var useTranscript =
            request.Detail == AgentRangeInspectionDetail.Transcript ||
            (request.Detail == AgentRangeInspectionDetail.All && source.HasAudio);
        var query = BuildQuery(request);

        VideoAnalysisPipelineResult? pipeline = null;
        AiRangeInspection? vision = null;
        string? visionWarning = null;
        if (request.Detail != AgentRangeInspectionDetail.Transcript)
        {
            pipeline = await orchestrator.InspectTechnicalRangeAsync(
                new VideoAnalysisRequest(
                    asset,
                    start,
                    end,
                    BuildTechnicalQuery(request.Detail)),
                progress: null,
                cancellationToken: cancellationToken,
                priority: JobPriority.UserInitiated).ConfigureAwait(false);

            if (useVision)
            {
                try
                {
                    vision = await orchestrator.InspectRangeAsync(
                        asset,
                        pipeline.Result,
                        query,
                        aiServer.PreferredModel,
                        progress: null,
                        cancellationToken: cancellationToken,
                        priority: JobPriority.UserInitiated).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    visionWarning = exception.Message;
                }
            }
        }

        SubtitleTranscriptionResult? transcription = null;
        string? transcriptWarning = null;
        if (useTranscript)
        {
            try
            {
                transcription = await orchestrator.TranscribeAsync(
                    asset,
                    start,
                    duration,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                transcriptWarning = exception.Message;
            }
        }

        var allRanges = pipeline?.Result.Ranges ?? Array.Empty<DetectedVideoRange>();
        if (request.Detail == AgentRangeInspectionDetail.Audio)
        {
            allRanges = allRanges
                .Where(item => item.Kind == UiMarkerKind.Silence)
                .ToArray();
        }

        var ranges = allRanges
            .OrderBy(item => item.SourceStart)
            .ThenBy(item => item.Kind)
            .Take(MaximumRanges)
            .ToArray();

        var transcriptCues = transcription?.Cues
            .Take(MaximumTranscriptCues)
            .ToArray();

        return AgentToolJson.ToElement(new
        {
            source_id = source.Id,
            source_name = source.Name,
            detail = request.Detail.ToString().ToLowerInvariant(),
            query = request.Query,
            range = new
            {
                start_seconds = Round(start),
                end_seconds = Round(end),
                duration_seconds = Round(duration)
            },
            analysis = pipeline is null
                ? null
                : new
                {
                    mode = "technical",
                    evidence_kind = "mechanical_measurement",
                    warning = pipeline.Warning,
                    summary = Compact(pipeline.Result.Summary, 2_000),
                    range_count = allRanges.Count,
                    ranges_truncated = allRanges.Count > ranges.Length,
                    ranges = ranges.Select(ToRangeObservation).ToArray()
                },
            vision = !useVision
                ? null
                : new
                {
                    model = aiServer.PreferredModel,
                    available = vision is not null,
                    sampling = "sparse_contact_sheets",
                    continuous_video_observed = false,
                    warning = visionWarning,
                    summary = Compact(vision?.Summary, 2_000),
                    observations = vision is null
                        ? Array.Empty<object>()
                        : vision.Observations.Select(item => (object)new
                        {
                            start_seconds = Round(item.Start),
                            end_seconds = Round(item.End),
                            title = Compact(item.Title, 500),
                            description = Compact(item.Description, 1_200),
                            confidence = Math.Round(item.Confidence, 3),
                            tags = item.Tags.Take(12).Select(tag => Compact(tag, 120)).ToArray()
                        }).ToArray()
                },
            transcript = transcription is null
                ? null
                : new
                {
                    engine = transcription.Engine,
                    warning = transcriptWarning,
                    cue_count = transcription.Cues.Count,
                    cues_truncated = transcription.Cues.Count > (transcriptCues?.Length ?? 0),
                    cues = (transcriptCues ?? Array.Empty<SubtitleCue>()).Select(cue => new
                    {
                        start_seconds = Round(start + cue.Start),
                        end_seconds = Round(start + cue.End),
                        text = Compact(cue.Text, 600)
                    }).ToArray()
                },
            transcript_warning = transcription is null ? transcriptWarning : null
        });
    }

    private MediaCacheKey BuildCacheKey(
        MediaSource source,
        AgentRangeInspectionRequest request)
    {
        var stableFingerprint = MontagePlanValidator.StableFingerprint(source);
        var modelIdentity = request.Detail is AgentRangeInspectionDetail.Frames or AgentRangeInspectionDetail.All
            ? $"{aiServer.Endpoint}|{aiServer.PreferredModel}"
            : "no-vision";
        var transcriptIdentity = request.Detail is AgentRangeInspectionDetail.Transcript or AgentRangeInspectionDetail.All
            ? TranscriptIdentity()
            : "no-transcript";
        var fingerprint = string.Join(
            '|',
            stableFingerprint,
            "agent-range-v1",
            request.Detail,
            TimelineTime.FromSeconds(request.StartSeconds).Ticks,
            TimelineTime.FromSeconds(request.EndSeconds).Ticks,
            modelIdentity,
            transcriptIdentity,
            request.Query);

        return new MediaCacheKey(
            source.Id,
            fingerprint,
            MediaArtifactKind.AgentObservation,
            (int)request.Detail,
            TimelineTime.FromSeconds(request.StartSeconds).Ticks,
            CacheFormatVersion);
    }

    private string TranscriptIdentity()
    {
        var availability = subtitles.GetWhisperAvailability();
        return string.Join(
            '|',
            availability.IsReady,
            availability.ExecutablePath ?? string.Empty,
            availability.ModelPath ?? string.Empty);
    }

    private static object ToRangeObservation(DetectedVideoRange range)
        => new
        {
            kind = range.Kind.ToString().ToLowerInvariant(),
            start_seconds = Round(range.SourceStart),
            end_seconds = Round(range.SourceStart + range.Duration),
            duration_seconds = Round(range.Duration),
            title = Compact(range.Title, 500),
            description = Compact(range.Description, 1_200),
            confidence = Math.Round(range.Confidence, 3),
            start_boundary = ToBoundaryObservation(range.StartBoundary),
            end_boundary = ToBoundaryObservation(range.EndBoundary)
        };

    private static object? ToBoundaryObservation(BoundaryVerificationResult? boundary)
        => boundary is null
            ? null
            : new
            {
                requested_seconds = Round(boundary.RequestedTime),
                verified_seconds = Round(boundary.VerifiedTime),
                coarse_candidates = boundary.CoarseCandidateCount,
                fine_candidates = boundary.FineCandidateCount,
                frame_candidates = boundary.FrameCandidateCount,
                unambiguous = boundary.HasUnambiguousCandidate,
                soft_transition = boundary.IsSoftTransition
            };

    private static string BuildTechnicalQuery(AgentRangeInspectionDetail detail)
        => detail == AgentRangeInspectionDetail.Audio
            ? "Технический анализ выбранного диапазона: паузы, тишина и границы."
            : "Технический анализ выбранного диапазона без изменения его границ.";

    private static string BuildQuery(AgentRangeInspectionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            return request.Query.Trim();
        }

        return request.Detail switch
        {
            AgentRangeInspectionDetail.Frames =>
                "Опиши только то, что действительно видно в выбранном диапазоне. " +
                "Отметь смысловые части, смены контекста и вероятные границы, не додумывая пропущенные кадры.",
            AgentRangeInspectionDetail.Audio =>
                "Проверь аудиоконтекст выбранного диапазона и технические паузы. " +
                "Тишина сама по себе не означает, что её надо удалять.",
            AgentRangeInspectionDetail.All =>
                "Исследуй выбранный диапазон в контексте задачи. " +
                "Опирайся на видимые кадры, технические границы и речь; не принимай монтажных решений вместо агента.",
            _ =>
                "Собери фактические технические наблюдения только по выбранному диапазону."
        };
    }

    private static void ValidateDetailAgainstMedia(
        MediaSource source,
        AgentRangeInspectionDetail detail)
    {
        if (detail is AgentRangeInspectionDetail.Summary or
            AgentRangeInspectionDetail.Frames or
            AgentRangeInspectionDetail.Audio or
            AgentRangeInspectionDetail.All)
        {
            if (source.Kind != MediaKind.Video)
            {
                throw new AgentToolRejectedException(
                    "unsupported_media_detail",
                    $"Detail '{detail}' currently requires a video source.");
            }
        }

        if (detail is AgentRangeInspectionDetail.Audio or
            AgentRangeInspectionDetail.Transcript)
        {
            if (!source.HasAudio)
            {
                throw new AgentToolRejectedException(
                    "media_has_no_audio",
                    $"Media '{source.Name}' has no audio stream.");
            }
        }
    }

    private static UiMediaAsset ToUiAsset(MediaSource source)
        => new()
        {
            Id = source.Id,
            Path = source.Path,
            Name = source.Name,
            Kind = (UiMediaKind)(int)source.Kind,
            Duration = source.Duration.TotalSeconds,
            Width = source.Width,
            Height = source.Height,
            FrameRate = source.FrameRate?.FramesPerSecond ?? 0,
            HasAudio = source.HasAudio,
            VideoCodec = source.VideoCodec,
            AudioCodec = source.AudioCodec,
            FileSizeBytes = source.FileSize,
            IsMissing = source.OnlineState != MediaOnlineState.Online,
            ProbeResult = new MediaProbeResult(
                source.Path,
                source.Kind,
                source.Duration,
                source.Streams.IsDefault
                    ? System.Collections.Immutable.ImmutableArray<MediaStreamDescriptor>.Empty
                    : source.Streams,
                new MediaFingerprint(
                    source.FileSize,
                    source.LastWriteUtcTicks,
                    string.IsNullOrWhiteSpace(source.FastFingerprint)
                        ? source.Fingerprint
                        : source.FastFingerprint,
                    string.IsNullOrWhiteSpace(source.VerifiedFingerprint)
                        ? null
                        : source.VerifiedFingerprint),
                source.Width,
                source.Height,
                source.FrameRate,
                source.IsVariableFrameRate)
        };

    private static string Compact(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "…";
    }

    private static double Round(double value)
        => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
