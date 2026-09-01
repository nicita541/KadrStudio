using KadrStudio.Application.Jobs;
using KadrStudio.Models;

namespace KadrStudio.Services;

/// <summary>Schedules reusable local ASR jobs outside the live editor state.</summary>
public sealed class AutomationOrchestrator(
    IBackgroundJobScheduler scheduler,
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
        var handle = scheduler.Schedule(request);
        using var handleRegistration = cancellationToken.Register(handle.Cancel);
        return await handle.Completion.ConfigureAwait(false);
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
        try { modified = File.GetLastWriteTimeUtc(asset.Path).Ticks; }
        catch { modified = 0; }
        return $"{asset.FileSizeBytes:x}-{modified:x}";
    }
}
