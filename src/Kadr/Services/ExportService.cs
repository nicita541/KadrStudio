using KadrStudio.Application.Rendering;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class ExportService(
    FfmpegLocator locator,
    ProcessRunner processRunner,
    TimelineRenderCoordinator coordinator)
{
    public async Task ExportAsync(
        EditorProject project,
        string outputPath,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        locator.EnsureAvailable();
        _ = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (project.GetVisualClips().Count == 0)
            throw new InvalidOperationException("Add at least one video or image to the timeline.");
        ValidateSources(project);

        var plan = coordinator.CreatePlan(project);
        var (width, height) = settings.GetSize();
        var renderProgress = new Progress<RenderProgress>(value =>
        {
            var seconds = value.Rendered.TotalSeconds;
            progress?.Report(new ExportProgress(
                value.Fraction * 100,
                value.Stage,
                $"{FormatTime(seconds)} / {FormatTime(plan.Duration.TotalSeconds)}"));
        });
        await coordinator.RenderAsync(
            plan,
            new RenderOutputOptions(
                RenderPurpose.Export,
                Path.GetFullPath(outputPath),
                width,
                height,
                settings.Quality,
                settings.UseHardwareEncoding,
                IncludeVideo: true,
                IncludeAudio: true),
            renderProgress,
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSources(EditorProject project)
    {
        var missing = project.Clips
            .Select(clip => project.FindAsset(clip.AssetId))
            .Where(asset => asset is null || !File.Exists(asset.Path))
            .Select(asset => asset?.Name ?? "Unknown media")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException("Source files were not found:\n" + string.Join("\n", missing));
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
}
