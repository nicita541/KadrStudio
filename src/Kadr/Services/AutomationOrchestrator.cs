using KadrStudio.Application.Jobs;
using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>
/// Runs all automatic media operations outside the live editor model. Inputs are
/// immutable value snapshots; only completed results are returned to the caller.
/// </summary>
public sealed class AutomationOrchestrator(
    IBackgroundJobScheduler scheduler,
    VideoAnalysisService analysis,
    AiVideoAnalysisService aiServer,
    AutoSubtitleService subtitles)
{
    public async Task<SubtitleTranscriptionResult> TranscribeAsync(
        MediaAsset asset,
        double sourceStart,
        double duration,
        CancellationToken cancellationToken = default)
    {
        var snapshot = CopyAsset(asset);
        var request = new JobRequest<SubtitleTranscriptionResult>(
            JobKey.Create("subtitles", snapshot.Id, Fingerprint(snapshot), sourceStart, duration),
            JobLane.Analysis,
            JobPriority.UserInitiated,
            token => new ValueTask<SubtitleTranscriptionResult>(
                subtitles.TranscribeLocalAsync(snapshot, sourceStart, duration, token)));
        return await AwaitAsync(scheduler.Schedule(request), cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoAnalysisPipelineResult> AnalyzeAsync(
        VideoAnalysisRequest request,
        string? aiModel,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.UserInitiated)
    {
        var isolatedRequest = request with { Asset = CopyAsset(request.Asset) };
        var key = JobKey.Create(
            "video-analysis", isolatedRequest.Asset.Id, Fingerprint(isolatedRequest.Asset),
            isolatedRequest.SourceStart, isolatedRequest.SourceEnd, isolatedRequest.Query, aiModel);
        var job = scheduler.Schedule(new JobRequest<VideoAnalysisPipelineResult>(
            key,
            JobLane.Analysis,
            priority,
            async token =>
            {
                var result = await analysis.AnalyzeAsync(isolatedRequest, progress, token).ConfigureAwait(false);
                string? warning = null;
                if (!string.IsNullOrWhiteSpace(aiModel))
                {
                    try
                    {
                        var enhancement = await aiServer.EnhanceAsync(
                            isolatedRequest.Asset, result, isolatedRequest.Query,
                            aiModel, progress, token).ConfigureAwait(false);
                        result = MergeEnhancement(result, enhancement);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        warning = $"Local AI skipped: {exception.Message}";
                    }
                }
                result = await analysis.RefineSemanticBoundariesAsync(
                    isolatedRequest.Asset, result, progress, token).ConfigureAwait(false);
                return new VideoAnalysisPipelineResult(result, warning);
            },
            PauseDuringExport: priority >= JobPriority.Background));
        return await AwaitAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoAnalysisPipelineResult> InspectTechnicalRangeAsync(
        VideoAnalysisRequest request,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.UserInitiated)
    {
        var isolatedRequest = request with { Asset = CopyAsset(request.Asset) };
        var key = JobKey.Create(
            "agent-technical-range",
            isolatedRequest.Asset.Id,
            Fingerprint(isolatedRequest.Asset),
            isolatedRequest.SourceStart,
            isolatedRequest.SourceEnd);

        var job = scheduler.Schedule(new JobRequest<VideoAnalysisPipelineResult>(
            key,
            JobLane.Analysis,
            priority,
            async token =>
            {
                var result = await analysis.AnalyzeAsync(
                    isolatedRequest,
                    progress,
                    token).ConfigureAwait(false);

                // VideoAnalysisService also emits heuristic structural labels for the legacy
                // montage flow. Agent observations must not present those guesses as facts.
                // Keep only mechanical measurements here; semantic meaning is requested
                // separately from the generic vision tool when the agent needs it.
                var technicalRanges = result.Ranges
                    .Where(item => item.Kind is MarkerKind.Scene or MarkerKind.BlackFrame or
                        MarkerKind.Silence or MarkerKind.Freeze)
                    .OrderBy(item => item.SourceStart)
                    .ThenBy(item => item.Kind)
                    .ToArray();

                var sceneCount = technicalRanges.Count(item => item.Kind == MarkerKind.Scene);
                var blackCount = technicalRanges.Count(item => item.Kind == MarkerKind.BlackFrame);
                var silenceCount = technicalRanges.Count(item => item.Kind == MarkerKind.Silence);
                var freezeCount = technicalRanges.Count(item => item.Kind == MarkerKind.Freeze);
                var technical = result with
                {
                    Summary =
                        $"Technical range {result.SourceStart:0.###}-{result.SourceEnd:0.###}s: " +
                        $"scene cuts {sceneCount}, black frames {blackCount}, " +
                        $"silences {silenceCount}, freezes {freezeCount}.",
                    Ranges = technicalRanges
                };

                return new VideoAnalysisPipelineResult(technical, null);
            },
            PauseDuringExport: priority >= JobPriority.Background));

        return await AwaitAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoAnalysisPipelineResult> EnhanceStructureAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        string query,
        string model,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.UserInitiated)
    {
        if (string.IsNullOrWhiteSpace(model))
            return new VideoAnalysisPipelineResult(baseline, "Для смысловой проверки не выбрана vision-модель.");
        var snapshot = CopyAsset(asset);
        var key = JobKey.Create(
            "structure-analysis", snapshot.Id, Fingerprint(snapshot), baseline.SourceStart,
            baseline.SourceEnd, query, model);
        var job = scheduler.Schedule(new JobRequest<VideoAnalysisPipelineResult>(
            key,
            JobLane.Analysis,
            priority,
            async token =>
            {
                var enhancement = await aiServer.EnhanceAsync(
                    snapshot, baseline, query, model, progress, token).ConfigureAwait(false);
                var merged = MergeEnhancement(baseline, enhancement);
                var refined = await analysis.RefineSemanticBoundariesAsync(
                    snapshot, merged, progress, token).ConfigureAwait(false);
                return new VideoAnalysisPipelineResult(refined, null);
            },
            PauseDuringExport: priority >= JobPriority.Background));
        return await AwaitAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiRangeInspection> InspectRangeAsync(
        MediaAsset asset,
        VideoAnalysisResult baseline,
        string query,
        string model,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.UserInitiated)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("A vision model is required.", nameof(model));

        var snapshot = CopyAsset(asset);
        var key = JobKey.Create(
            "agent-range-vision",
            snapshot.Id,
            Fingerprint(snapshot),
            baseline.SourceStart,
            baseline.SourceEnd,
            query,
            model);

        var job = scheduler.Schedule(new JobRequest<AiRangeInspection>(
            key,
            JobLane.Analysis,
            priority,
            token => new ValueTask<AiRangeInspection>(
                aiServer.InspectRangeAsync(
                    snapshot,
                    baseline,
                    query,
                    model,
                    progress,
                    token)),
            PauseDuringExport: priority >= JobPriority.Background));

        return await AwaitAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private static VideoAnalysisResult MergeEnhancement(
        VideoAnalysisResult baseline,
        AiAnalysisEnhancement enhancement)
    {
        var refinedKinds = enhancement.Ranges.Select(range => range.Kind).ToHashSet();
        var ranges = baseline.Ranges
            .Where(range => !refinedKinds.Contains(range.Kind))
            .Concat(enhancement.Ranges)
            .OrderBy(range => range.SourceStart)
            .ThenBy(range => range.Kind)
            .ToArray();
        var summary = string.IsNullOrWhiteSpace(enhancement.Summary)
            ? baseline.Summary
            : $"{baseline.Summary} {enhancement.Model}: {enhancement.Summary}";
        return baseline with { Summary = summary, Ranges = ranges };
    }

    private static MediaAsset CopyAsset(MediaAsset asset)
        => new()
        {
            Id = asset.Id,
            Name = asset.Name,
            Kind = asset.Kind,
            Path = Path.GetFullPath(asset.Path),
            Duration = asset.Duration,
            Width = asset.Width,
            Height = asset.Height,
            FrameRate = asset.FrameRate,
            HasAudio = asset.HasAudio,
            VideoCodec = asset.VideoCodec,
            AudioCodec = asset.AudioCodec,
            FileSizeBytes = asset.FileSizeBytes,
            IsMissing = asset.IsMissing,
            ProbeResult = asset.ProbeResult
        };

    private static string Fingerprint(MediaAsset asset)
    {
        long modified;
        try { modified = File.GetLastWriteTimeUtc(asset.Path).Ticks; } catch { modified = 0; }
        return $"{asset.FileSizeBytes:x}-{modified:x}";
    }

    private static async Task<TResult> AwaitAsync<TResult>(
        JobHandle<TResult> handle,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(handle.Cancel);
        return await handle.Completion.ConfigureAwait(false);
    }
}

public sealed record VideoAnalysisPipelineResult(VideoAnalysisResult Result, string? Warning);
