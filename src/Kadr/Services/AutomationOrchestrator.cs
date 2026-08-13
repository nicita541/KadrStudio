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
    OllamaVideoAnalysisService ollama,
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
        string? ollamaModel,
        IProgress<VideoAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var isolatedRequest = request with { Asset = CopyAsset(request.Asset) };
        var key = JobKey.Create(
            "video-analysis", isolatedRequest.Asset.Id, Fingerprint(isolatedRequest.Asset),
            isolatedRequest.SourceStart, isolatedRequest.SourceEnd, isolatedRequest.Query, ollamaModel);
        var job = scheduler.Schedule(new JobRequest<VideoAnalysisPipelineResult>(
            key,
            JobLane.Analysis,
            JobPriority.UserInitiated,
            async token =>
            {
                var result = await analysis.AnalyzeAsync(isolatedRequest, progress, token).ConfigureAwait(false);
                string? warning = null;
                if (!string.IsNullOrWhiteSpace(ollamaModel))
                {
                    try
                    {
                        var enhancement = await ollama.EnhanceAsync(
                            isolatedRequest.Asset, result, isolatedRequest.Query,
                            ollamaModel, progress, token).ConfigureAwait(false);
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
            }));
        return await AwaitAsync(job, cancellationToken).ConfigureAwait(false);
    }

    private static VideoAnalysisResult MergeEnhancement(
        VideoAnalysisResult baseline,
        OllamaAnalysisEnhancement enhancement)
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
            IsMissing = asset.IsMissing
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
