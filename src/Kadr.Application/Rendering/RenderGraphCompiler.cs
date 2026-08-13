using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Rendering;

/// <summary>
/// Compiles immutable editor state into the single backend-neutral graph used
/// by preview and export. No WPF or process objects are allowed in this layer.
/// </summary>
public sealed class RenderGraphCompiler(IProjectValidator? validator = null) : IRenderGraphCompiler
{
    private readonly IProjectValidator _validator = validator ?? new ProjectValidator();

    public RenderGraph Compile(ProjectState project, TimeRange? requestedRange = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var validation = _validator.Validate(project);
        if (!validation.IsValid)
            throw new InvalidDataException("Cannot compile an invalid project: " +
                                           string.Join("; ", validation.Errors.Select(item => item.Message)));

        var range = ResolveRange(project, requestedRange);
        var trackById = project.Tracks.ToDictionary(item => item.Id);
        var transitionClipIds = project.Transitions
            .Where(item => Intersects(item.Range, range))
            .SelectMany(item => new[] { item.FromClipId, item.ToClipId })
            .ToHashSet();
        var visual = project.MediaClips
            .Where(clip => Intersects(clip.Range, range) || transitionClipIds.Contains(clip.Id))
            .Select(clip => (Clip: clip, Track: trackById[clip.TrackId], Source: project.Sources[clip.SourceId]))
            .Where(item => item.Track.Kind == TrackKind.Visual && item.Track.IsVisible &&
                           item.Source.Kind is MediaKind.Video or MediaKind.Image)
            .OrderBy(item => item.Track.Index)
            .ThenBy(item => item.Clip.Start)
            .ThenBy(item => item.Clip.Id)
            .Select(CreateVisualLayer)
            .ToImmutableArray();
        var audio = project.MediaClips
            .Where(clip => Intersects(clip.Range, range) || transitionClipIds.Contains(clip.Id))
            .Select(clip => (Clip: clip, Track: trackById[clip.TrackId], Source: project.Sources[clip.SourceId]))
            .Where(item => item.Track.Kind == TrackKind.Audio && !item.Track.IsMuted && item.Source.HasAudio &&
                           item.Source.Kind is MediaKind.Video or MediaKind.Audio &&
                           item.Clip.Audio is not { IsMuted: true })
            .OrderBy(item => item.Track.Index)
            .ThenBy(item => item.Clip.Start)
            .ThenBy(item => item.Clip.Id)
            .Select(CreateAudioLayer)
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

        var visualById = visual.ToDictionary(item => item.ClipId);
        var audioById = audio.ToDictionary(item => item.ClipId);
        var videoTransitions = project.Transitions
            .Where(item => Intersects(item.Range, range) && trackById[item.TrackId].Kind == TrackKind.Visual)
            .Where(item => visualById.ContainsKey(item.FromClipId) && visualById.ContainsKey(item.ToClipId))
            .OrderBy(item => trackById[item.TrackId].Index).ThenBy(item => item.Start).ThenBy(item => item.Id)
            .Select(item => new RenderVideoTransition(
                item.Id, item.Kind, item.TrackId, trackById[item.TrackId].Index, item.Range,
                visualById[item.FromClipId], visualById[item.ToClipId]))
            .ToImmutableArray();
        var audioTransitions = project.Transitions
            .Where(item => Intersects(item.Range, range) && trackById[item.TrackId].Kind == TrackKind.Audio)
            .Where(item => audioById.ContainsKey(item.FromClipId) && audioById.ContainsKey(item.ToClipId))
            .OrderBy(item => trackById[item.TrackId].Index).ThenBy(item => item.Start).ThenBy(item => item.Id)
            .Select(item => new RenderAudioTransition(
                item.Id, item.TrackId, trackById[item.TrackId].Index, item.Range,
                audioById[item.FromClipId], audioById[item.ToClipId]))
            .ToImmutableArray();

        var sourceSignature = ComputeSourceSignature(project, range, visual, audio);
        var videoSignature = ComputeVideoSignature(project, range, visual, videoTransitions);
        var audioSignature = ComputeAudioSignature(project, range, audio, audioTransitions);
        var overlaySignature = ComputeOverlaySignature(project, range, text);
        return new RenderGraph(
            project.Id, project.Revision, project.CanvasWidth, project.CanvasHeight,
            project.FrameRate, project.Sequence.AudioSampleRate, range,
            visual, audio, text, videoTransitions, audioTransitions,
            sourceSignature, videoSignature, audioSignature, overlaySignature);
    }

    private static RenderVisualLayer CreateVisualLayer(
        (MediaClip Clip, TimelineTrack Track, MediaSource Source) item)
        => new(item.Clip.Id, item.Source.Id, item.Track.Id, item.Track.Index,
            item.Source.Path, item.Source.Kind, item.Clip.Range, item.Clip.SourceIn,
            item.Clip.Video ?? new VideoParameters());

    private static RenderAudioLayer CreateAudioLayer(
        (MediaClip Clip, TimelineTrack Track, MediaSource Source) item)
        => new(item.Clip.Id, item.Source.Id, item.Track.Id, item.Track.Index,
            item.Source.Path, item.Clip.Range, item.Clip.SourceIn,
            item.Clip.Audio ?? new AudioParameters());

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

    private static string ComputeSourceSignature(
        ProjectState project,
        TimeRange range,
        IEnumerable<RenderVisualLayer> visual,
        IEnumerable<RenderAudioLayer> audio)
    {
        var builder = new StringBuilder(4096);
        builder.Append("decode-v1|").Append(range.Start.Ticks).Append('|').Append(range.Duration.Ticks);
        foreach (var item in visual.Select(item => (item.ClipId, item.SourceId, item.SourcePath, item.TimelineRange, item.SourceIn))
                     .Concat(audio.Select(item => (item.ClipId, item.SourceId, item.SourcePath, item.TimelineRange, item.SourceIn)))
                     .Distinct().OrderBy(item => item.ClipId))
        {
            var source = project.Sources[item.SourceId];
            builder.Append('|').Append(item.ClipId.ToString("N")).Append('|').Append(item.SourcePath)
                .Append('|').Append(StableFingerprint(source)).Append('|').Append(item.TimelineRange.Start.Ticks)
                .Append('|').Append(item.TimelineRange.Duration.Ticks).Append('|').Append(item.SourceIn.Ticks);
        }
        return RenderSignature.Hash(builder.ToString());
    }

    private static string ComputeVideoSignature(
        ProjectState project,
        TimeRange range,
        IEnumerable<RenderVisualLayer> visual,
        IEnumerable<RenderVideoTransition> transitions)
    {
        var builder = new StringBuilder(4096);
        builder.Append("video-v2|").Append(project.CanvasWidth).Append('|').Append(project.CanvasHeight).Append('|')
            .Append(project.FrameRate.Numerator).Append('/').Append(project.FrameRate.Denominator).Append('|')
            .Append(range.Start.Ticks).Append('|').Append(range.Duration.Ticks);
        foreach (var item in visual)
        {
            var source = project.Sources[item.SourceId];
            var p = item.Parameters;
            builder.Append("|V|").Append(item.ClipId.ToString("N")).Append('|').Append(item.TrackIndex)
                .Append('|').Append(StableFingerprint(source)).Append('|').Append(item.TimelineRange.Start.Ticks)
                .Append('|').Append(item.TimelineRange.Duration.Ticks).Append('|').Append(item.SourceIn.Ticks)
                .Append('|').Append(F(p.Brightness)).Append('|').Append(F(p.Contrast))
                .Append('|').Append(F(p.Saturation)).Append('|').Append(F(p.Temperature))
                .Append('|').Append(F(p.PositionX)).Append('|').Append(F(p.PositionY))
                .Append('|').Append(F(p.ScaleX)).Append('|').Append(F(p.ScaleY))
                .Append('|').Append(F(p.Rotation)).Append('|').Append(F(p.CropLeft))
                .Append('|').Append(F(p.CropTop)).Append('|').Append(F(p.CropRight))
                .Append('|').Append(F(p.CropBottom)).Append('|').Append(F(p.Opacity));
        }
        AppendTransitions(builder, transitions.Select(item =>
            (item.Id, item.Kind, item.TrackIndex, item.TimelineRange, item.From.ClipId, item.To.ClipId)));
        return RenderSignature.Hash(builder.ToString());
    }

    private static string ComputeAudioSignature(
        ProjectState project,
        TimeRange range,
        IEnumerable<RenderAudioLayer> audio,
        IEnumerable<RenderAudioTransition> transitions)
    {
        var builder = new StringBuilder(4096);
        builder.Append("audio-v2|").Append(project.Sequence.AudioSampleRate).Append('|')
            .Append(range.Start.Ticks).Append('|').Append(range.Duration.Ticks);
        foreach (var item in audio)
        {
            var source = project.Sources[item.SourceId];
            var p = item.Parameters;
            builder.Append("|A|").Append(item.ClipId.ToString("N")).Append('|').Append(item.TrackIndex)
                .Append('|').Append(StableFingerprint(source)).Append('|').Append(item.TimelineRange.Start.Ticks)
                .Append('|').Append(item.TimelineRange.Duration.Ticks).Append('|').Append(item.SourceIn.Ticks)
                .Append('|').Append(F(p.Volume)).Append('|').Append(p.IsMuted).Append('|').Append(F(p.Pan))
                .Append('|').Append(p.FadeIn.Ticks).Append('|').Append(p.FadeOut.Ticks)
                .Append('|').Append(F(p.Bass)).Append('|').Append(F(p.Mid)).Append('|').Append(F(p.Treble));
        }
        AppendTransitions(builder, transitions.Select(item =>
            (item.Id, TransitionKind.ConstantPowerAudio, item.TrackIndex, item.TimelineRange,
                item.From.ClipId, item.To.ClipId)));
        return RenderSignature.Hash(builder.ToString());
    }

    private static string ComputeOverlaySignature(
        ProjectState project,
        TimeRange range,
        IEnumerable<RenderTextLayer> text)
    {
        var builder = new StringBuilder(2048);
        builder.Append("overlay-v2|").Append(project.CanvasWidth).Append('|').Append(project.CanvasHeight).Append('|')
            .Append(range.Start.Ticks).Append('|').Append(range.Duration.Ticks);
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
        return RenderSignature.Hash(builder.ToString());
    }

    private static void AppendTransitions(
        StringBuilder builder,
        IEnumerable<(Guid Id, TransitionKind Kind, int TrackIndex, TimeRange Range, Guid FromId, Guid ToId)> transitions)
    {
        foreach (var item in transitions)
            builder.Append("|X|").Append(item.Id.ToString("N")).Append('|').Append(item.Kind).Append('|')
                .Append(item.TrackIndex).Append('|').Append(item.Range.Start.Ticks).Append('|')
                .Append(item.Range.Duration.Ticks).Append('|').Append(item.FromId.ToString("N"))
                .Append('|').Append(item.ToId.ToString("N"));
    }

    private static string StableFingerprint(MediaSource source)
        => !string.IsNullOrWhiteSpace(source.VerifiedFingerprint) ? source.VerifiedFingerprint :
            !string.IsNullOrWhiteSpace(source.FastFingerprint) ? source.FastFingerprint : source.Fingerprint;

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
