using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Models;

namespace KadrStudio.Services;

public sealed class ExportService(
    FfmpegLocator locator,
    ProcessRunner processRunner,
    TimelineRenderCoordinator coordinator)
{
    public async Task ExportAsync(
        ProjectState project,
        string outputPath,
        ExportSettings settings,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        locator.EnsureAvailable();
        _ = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (!project.MediaClips.Any(clip =>
                project.FindTrack(clip.TrackId)?.Kind == KadrStudio.Core.Domain.TrackKind.Visual))
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

    private static void ValidateSources(ProjectState project)
    {
        var missing = project.MediaClips
            .Select(clip => project.Sources.GetValueOrDefault(clip.SourceId))
            .Where(source => source is null || source.OnlineState != MediaOnlineState.Online || !File.Exists(source.Path))
            .Select(source => source?.Name ?? "Unknown media")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException("Source files were not found:\n" + string.Join("\n", missing));
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
}
