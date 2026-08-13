using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Models;
using UiMediaKind = KadrStudio.Models.MediaKind;

namespace KadrStudio.Services;

/// <summary>
/// Compatibility adapter for the WPF preview session. All composition is built
/// by the same RenderPlan and FFmpeg engine used by final export.
/// </summary>
public sealed class PreviewCompositionService
{
    public const double SegmentStep = 15;
    public const double SegmentOverlap = 4;

    private readonly TimelineRenderCoordinator _coordinator;
    private readonly string _videoDirectory;
    private readonly string _audioDirectory;
    private readonly string _stillDirectory;

    public PreviewCompositionService(
        FfmpegLocator locator,
        ProcessRunner processRunner,
        TimelineRenderCoordinator coordinator,
        string? cacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(processRunner);
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        var root = Path.GetFullPath(cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "Cache", "RenderPlan"));
        _videoDirectory = Path.Combine(root, "video");
        _audioDirectory = Path.Combine(root, "audio");
        _stillDirectory = Path.Combine(root, "stills");
    }

    public string GetVideoSignature(EditorProject project, bool halfQuality)
    {
        if (project.Duration <= 0) return $"empty-video-{halfQuality}";
        return $"{_coordinator.CreatePlan(project).VideoContentSignature}-v-{(halfQuality ? "half" : "full")}";
    }

    public string GetAudioSignature(EditorProject project)
    {
        if (project.Duration <= 0) return "empty-audio";
        return $"{_coordinator.CreatePlan(project).AudioContentSignature}-a";
    }

    public bool HasRenderableVideo(EditorProject project)
        => project.GetVisualClips().Any(clip =>
            project.FindAsset(clip.AssetId) is { IsMissing: false, Kind: UiMediaKind.Video or UiMediaKind.Image } asset &&
            File.Exists(asset.Path));

    public bool HasRenderableAudio(EditorProject project)
        => project.GetAudioClips().Any(clip =>
            project.FindAsset(clip.AssetId) is { IsMissing: false, HasAudio: true, Kind: UiMediaKind.Video or UiMediaKind.Audio } asset &&
            File.Exists(asset.Path));

    public async Task<TimelinePreviewSegment> EnsureVideoSegmentAsync(
        EditorProject project,
        double timelinePosition,
        bool halfQuality,
        CancellationToken cancellationToken = default)
    {
        var range = SegmentRange(project, timelinePosition);
        var plan = _coordinator.CreatePlan(project, range);
        var generationSignature = GetVideoSignature(project, halfQuality);
        var artifactSignature = $"{plan.VideoContentSignature}-v-{(halfQuality ? "half" : "full")}";
        Directory.CreateDirectory(_videoDirectory);
        var output = Path.Combine(_videoDirectory, $"{artifactSignature}.mp4");
        if (!IsUsable(output))
        {
            var (width, height) = ResolvePreviewSize(project, halfQuality);
            await _coordinator.RenderAsync(
                plan,
                new RenderOutputOptions(RenderPurpose.Preview, output, width, height, 24,
                    IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return new TimelinePreviewSegment(
            PreviewPipeline.Video, generationSignature, artifactSignature, output,
            range.Start.TotalSeconds, range.Duration.TotalSeconds);
    }

    public async Task<TimelinePreviewSegment> EnsureAudioSegmentAsync(
        EditorProject project,
        double timelinePosition,
        CancellationToken cancellationToken = default)
    {
        var range = SegmentRange(project, timelinePosition);
        var plan = _coordinator.CreatePlan(project, range);
        var generationSignature = GetAudioSignature(project);
        var artifactSignature = $"{plan.AudioContentSignature}-a";
        Directory.CreateDirectory(_audioDirectory);
        var output = Path.Combine(_audioDirectory, $"{artifactSignature}.m4a");
        if (!IsUsable(output))
        {
            await _coordinator.RenderAsync(
                plan,
                new RenderOutputOptions(RenderPurpose.AudioPreview, output, 16, 16, 24,
                    IncludeVideo: false, IncludeAudio: true, IncludeOverlays: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return new TimelinePreviewSegment(
            PreviewPipeline.Audio, generationSignature, artifactSignature, output,
            range.Start.TotalSeconds, range.Duration.TotalSeconds);
    }

    public async Task<CompositedStillFrame> EnsureStillFrameAsync(
        EditorProject project,
        double timelinePosition,
        bool halfQuality,
        CancellationToken cancellationToken = default)
    {
        var fps = Math.Max(1, project.FrameRate);
        var frame = Math.Max(0, (long)Math.Round(Math.Max(0, timelinePosition) * fps));
        var exact = TimelineTime.FromFrames(frame, new FrameRate(fps));
        var duration = TimelineTime.FromFrames(1, new FrameRate(fps));
        var plan = _coordinator.CreatePlan(project, new TimeRange(exact, duration));
        var signature = $"{plan.ContentSignature}-s-{(halfQuality ? "half" : "full")}";
        Directory.CreateDirectory(_stillDirectory);
        var output = Path.Combine(_stillDirectory, $"{signature}.png");
        if (!IsUsable(output, 256))
        {
            await _coordinator.RenderAsync(
                plan,
                new RenderOutputOptions(
                    RenderPurpose.StillFrame, output,
                    ResolvePreviewSize(project, halfQuality).Width,
                    ResolvePreviewSize(project, halfQuality).Height,
                    IncludeVideo: true, IncludeAudio: false, IncludeOverlays: false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return new CompositedStillFrame(signature, output, exact.TotalSeconds);
    }

    public void InvalidateCachedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!IsUnder(fullPath, _videoDirectory) && !IsUnder(fullPath, _audioDirectory) && !IsUnder(fullPath, _stillDirectory)) return;
        try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
    }

    private static TimeRange SegmentRange(EditorProject project, double timelinePosition)
    {
        if (project.Duration <= 0) throw new InvalidOperationException("The project has no renderable duration.");
        var start = Math.Floor(Math.Clamp(timelinePosition, 0, project.Duration) / SegmentStep) * SegmentStep;
        if (start >= project.Duration) start = Math.Max(0, project.Duration - 1.0 / Math.Max(1, project.FrameRate));
        var duration = Math.Min(SegmentStep + SegmentOverlap, project.Duration - start);
        return new TimeRange(TimelineTime.FromSeconds(start), TimelineTime.FromSeconds(Math.Max(duration, 0.001)));
    }

    private static bool IsUsable(string path, long minimumLength = 1024)
        => File.Exists(path) && new FileInfo(path).Length > minimumLength;

    internal static (int Width, int Height) ResolvePreviewSize(EditorProject project, bool halfQuality)
    {
        if (!halfQuality)
            return (project.CanvasWidth, project.CanvasHeight);
        var scale = Math.Min(0.5, Math.Min(960d / project.CanvasWidth, 540d / project.CanvasHeight));
        var width = Math.Max(2, (int)Math.Round(project.CanvasWidth * scale / 2) * 2);
        var height = Math.Max(2, (int)Math.Round(project.CanvasHeight * scale / 2) * 2);
        return (width, height);
    }

    private static bool IsUnder(string path, string root)
        => path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

public enum PreviewPipeline
{
    Video,
    Audio
}

public sealed record TimelinePreviewSegment(
    PreviewPipeline Pipeline,
    string Signature,
    string ArtifactSignature,
    string Path,
    double TimelineStart,
    double Duration)
{
    public double TimelineEnd => TimelineStart + Duration;
    public bool Contains(double timelinePosition)
        => timelinePosition >= TimelineStart - 0.001 && timelinePosition < TimelineEnd - 0.001;
}

public sealed record CompositedStillFrame(string Signature, string Path, double TimelinePosition);
