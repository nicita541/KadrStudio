using System.Globalization;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Rendering;

public sealed class RenderPlanBuilder(IProjectValidator? validator = null) : IRenderPlanBuilder
{
    private readonly IProjectValidator _validator = validator ?? new ProjectValidator();

    public RenderPlan Build(ProjectState project, TimeRange? requestedRange = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var validation = _validator.Validate(project);
        if (!validation.IsValid)
            throw new InvalidDataException("Cannot build a render plan for an invalid project: " +
                                           string.Join("; ", validation.Errors.Select(item => item.Message)));

        var range = ResolveRange(project, requestedRange);
        var trackById = project.Tracks.ToDictionary(item => item.Id);
        var visual = project.MediaClips
            .Where(clip => Intersects(clip.Range, range))
            .Select(clip => (Clip: clip, Track: trackById[clip.TrackId], Source: project.Sources[clip.SourceId]))
            .Where(item => item.Track.Kind == TrackKind.Visual && item.Track.IsVisible &&
                           item.Source.Kind is MediaKind.Video or MediaKind.Image)
            .OrderBy(item => item.Track.Index)
            .ThenBy(item => item.Clip.Start)
            .ThenBy(item => item.Clip.Id)
            .Select(item => new RenderVisualLayer(
                item.Clip.Id, item.Source.Id, item.Track.Id, item.Track.Index,
                item.Source.Path, item.Source.Kind, item.Clip.Range, item.Clip.SourceIn,
                item.Clip.Video ?? new VideoParameters()))
            .ToImmutableArray();
        var audio = project.MediaClips
            .Where(clip => Intersects(clip.Range, range))
            .Select(clip => (Clip: clip, Track: trackById[clip.TrackId], Source: project.Sources[clip.SourceId]))
            .Where(item => item.Track.Kind == TrackKind.Audio && !item.Track.IsMuted && item.Source.HasAudio &&
                           item.Source.Kind is MediaKind.Video or MediaKind.Audio &&
                           item.Clip.Audio is not { IsMuted: true })
            .OrderBy(item => item.Track.Index)
            .ThenBy(item => item.Clip.Start)
            .ThenBy(item => item.Clip.Id)
            .Select(item => new RenderAudioLayer(
                item.Clip.Id, item.Source.Id, item.Track.Id, item.Track.Index,
                item.Source.Path, item.Clip.Range, item.Clip.SourceIn,
                item.Clip.Audio ?? new AudioParameters()))
            .ToImmutableArray();
        var text = project.TextClips
            .Where(clip => Intersects(clip.Range, range))
            .Select(clip => (Clip: clip, Track: trackById[clip.TrackId]))
            .Where(item => item.Track.Kind == TrackKind.Text && item.Track.IsVisible)
            .OrderBy(item => item.Track.Index)
            .ThenBy(item => item.Clip.Start)
            .ThenBy(item => item.Clip.Id)
            .Select(item => new RenderTextLayer(
                item.Clip.Id, item.Track.Id, item.Track.Index,
                item.Clip.Range, item.Clip.Text, item.Clip.Style))
            .ToImmutableArray();

        var signature = ComputeSignature(project, range, visual, audio, text);
        return new RenderPlan(
            project.Id, project.Revision, project.CanvasWidth, project.CanvasHeight,
            project.FrameRate, range, visual, audio, text, signature);
    }

    private static TimeRange ResolveRange(ProjectState project, TimeRange? requestedRange)
    {
        if (requestedRange is { } explicitRange) return explicitRange;
        var start = project.InPoint ?? TimelineTime.Zero;
        var end = project.OutPoint ?? project.Duration;
        if (end <= start) throw new InvalidOperationException("The project has no renderable duration.");
        return new TimeRange(start, end - start);
    }

    private static bool Intersects(TimeRange left, TimeRange right)
        => left.Start < right.End && left.End > right.Start;

    private static string ComputeSignature(
        ProjectState project,
        TimeRange range,
        IEnumerable<RenderVisualLayer> visual,
        IEnumerable<RenderAudioLayer> audio,
        IEnumerable<RenderTextLayer> text)
    {
        var builder = new StringBuilder(4096);
        builder.Append(project.CanvasWidth).Append('|').Append(project.CanvasHeight).Append('|')
            .Append(project.FrameRate.Numerator).Append('/').Append(project.FrameRate.Denominator).Append('|')
            .Append(range.Start.Ticks).Append('|').Append(range.Duration.Ticks);
        foreach (var item in visual)
        {
            builder.Append("|V|").Append(item.ClipId.ToString("N")).Append('|').Append(item.TrackIndex)
                .Append('|').Append(item.SourcePath).Append('|').Append(item.TimelineRange.Start.Ticks)
                .Append('|').Append(item.TimelineRange.Duration.Ticks).Append('|').Append(item.SourceIn.Ticks)
                .Append('|').Append(F(item.Parameters.Brightness)).Append('|').Append(F(item.Parameters.Contrast))
                .Append('|').Append(F(item.Parameters.Saturation)).Append('|').Append(F(item.Parameters.Temperature));
        }
        foreach (var item in audio)
        {
            builder.Append("|A|").Append(item.ClipId.ToString("N")).Append('|').Append(item.TrackIndex)
                .Append('|').Append(item.SourcePath).Append('|').Append(item.TimelineRange.Start.Ticks)
                .Append('|').Append(item.TimelineRange.Duration.Ticks).Append('|').Append(item.SourceIn.Ticks)
                .Append('|').Append(F(item.Parameters.Volume)).Append('|').Append(F(item.Parameters.Pan))
                .Append('|').Append(item.Parameters.FadeIn.Ticks).Append('|').Append(item.Parameters.FadeOut.Ticks)
                .Append('|').Append(F(item.Parameters.Bass)).Append('|').Append(F(item.Parameters.Mid))
                .Append('|').Append(F(item.Parameters.Treble));
        }
        foreach (var item in text)
        {
            builder.Append("|T|").Append(item.ClipId.ToString("N")).Append('|').Append(item.TrackIndex)
                .Append('|').Append(item.TimelineRange.Start.Ticks).Append('|').Append(item.TimelineRange.Duration.Ticks)
                .Append('|').Append(item.Text).Append('|').Append(item.Style.FontFamily)
                .Append('|').Append(F(item.Style.FontSize)).Append('|').Append(item.Style.Color)
                .Append('|').Append(F(item.Style.X)).Append('|').Append(F(item.Style.Y))
                .Append('|').Append(F(item.Style.Rotation)).Append('|').Append(F(item.Style.BoxWidth))
                .Append('|').Append(F(item.Style.BoxHeight)).Append('|').Append(item.Style.IsSubtitle);
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
