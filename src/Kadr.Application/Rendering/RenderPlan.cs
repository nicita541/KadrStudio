using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Rendering;

public sealed record RenderVisualLayer(
    Guid ClipId,
    Guid SourceId,
    Guid TrackId,
    int TrackIndex,
    string SourcePath,
    MediaKind SourceKind,
    TimeRange TimelineRange,
    TimelineTime SourceIn,
    VideoParameters Parameters);

public sealed record RenderAudioLayer(
    Guid ClipId,
    Guid SourceId,
    Guid TrackId,
    int TrackIndex,
    string SourcePath,
    TimeRange TimelineRange,
    TimelineTime SourceIn,
    AudioParameters Parameters);

public sealed record RenderTextLayer(
    Guid ClipId,
    Guid TrackId,
    int TrackIndex,
    TimeRange TimelineRange,
    string Text,
    TextStyle Style);

public sealed record RenderPlan(
    Guid ProjectId,
    long ProjectRevision,
    int CanvasWidth,
    int CanvasHeight,
    FrameRate FrameRate,
    TimeRange Range,
    ImmutableArray<RenderVisualLayer> VisualLayers,
    ImmutableArray<RenderAudioLayer> AudioLayers,
    ImmutableArray<RenderTextLayer> TextLayers,
    string VideoContentSignature,
    string AudioContentSignature,
    string OverlaySignature,
    string ContentSignature)
{
    public TimelineTime Duration => Range.Duration;

    public string GetPipelineSignature(bool includeVideo, bool includeAudio, bool includeOverlays)
    {
        if (!includeVideo && !includeAudio)
            throw new ArgumentException("At least one media pipeline is required.");
        var components = new List<string>(3);
        if (includeVideo) components.Add($"V:{VideoContentSignature}");
        if (includeAudio) components.Add($"A:{AudioContentSignature}");
        if (includeVideo && includeOverlays) components.Add($"O:{OverlaySignature}");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('|', components))));
    }

    public RenderFrame GetFrame(TimelineTime timelineTime)
    {
        if (!Range.Contains(timelineTime))
            throw new ArgumentOutOfRangeException(nameof(timelineTime), "The requested frame is outside the render range.");
        return new RenderFrame(
            timelineTime,
            VisualLayers.Where(item => item.TimelineRange.Contains(timelineTime)).ToImmutableArray(),
            AudioLayers.Where(item => item.TimelineRange.Contains(timelineTime)).ToImmutableArray(),
            TextLayers.Where(item => item.TimelineRange.Contains(timelineTime)).ToImmutableArray());
    }
}

public sealed record RenderFrame(
    TimelineTime TimelineTime,
    ImmutableArray<RenderVisualLayer> VisualLayers,
    ImmutableArray<RenderAudioLayer> AudioLayers,
    ImmutableArray<RenderTextLayer> TextLayers);

public interface IRenderPlanBuilder
{
    RenderPlan Build(ProjectState project, TimeRange? requestedRange = null);
}

public enum RenderPurpose
{
    Preview,
    Export,
    StillFrame,
    AudioPreview,
    FrameServer,
    AudioServer
}

public sealed record RenderOutputOptions(
    RenderPurpose Purpose,
    string OutputPath,
    int Width,
    int Height,
    int VideoQuality = 20,
    bool UseHardwareEncoding = false,
    bool IncludeVideo = true,
    bool IncludeAudio = true,
    bool IncludeOverlays = true);

public sealed record ExternalRenderCommand(
    string ExecutableRole,
    ImmutableArray<string> Arguments,
    string OutputPath,
    string PlanSignature);

public interface IRenderCommandBuilder
{
    ExternalRenderCommand Build(RenderPlan plan, RenderOutputOptions options);
}

public sealed record RenderProgress(double Fraction, TimelineTime Rendered, string Stage);

public interface IRenderEngine
{
    Task<string> RenderAsync(
        RenderPlan plan,
        RenderOutputOptions options,
        IProgress<RenderProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
