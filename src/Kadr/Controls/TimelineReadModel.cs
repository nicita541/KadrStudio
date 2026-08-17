using KadrStudio.Models;
using KadrStudio.Core.Domain;

namespace KadrStudio.Controls;

/// <summary>
/// Isolated read snapshot consumed by TimelineControl. Mutable legacy WPF
/// models are copied at the adapter boundary and never retained by the control.
/// </summary>
public sealed class TimelineReadModel
{
    private TimelineReadModel(EditorProject source)
    {
        FrameRateValue = source.FrameRateValue;
        InPoint = source.InPoint;
        OutPoint = source.OutPoint;
        Clips = source.Clips.Select(item => item.Clone()).ToArray();
        TextOverlays = source.TextOverlays.Select(item => item.Clone()).ToArray();
        Markers = source.Markers.Select(item => new Models.TimelineMarker
        {
            Id = item.Id,
            AssetId = item.AssetId,
            Kind = item.Kind,
            Start = item.Start,
            Duration = item.Duration,
            Title = item.Title,
            Description = item.Description,
            Confidence = item.Confidence,
            SourceStart = item.SourceStart,
            Query = item.Query
        }).ToArray();
        Media = source.Media.Select(item => new MediaAsset
        {
            Id = item.Id,
            Name = item.Name,
            Kind = item.Kind,
            Path = item.Path,
            Duration = item.Duration,
            Width = item.Width,
            Height = item.Height,
            FrameRate = item.FrameRate,
            HasAudio = item.HasAudio,
            VideoCodec = item.VideoCodec,
            AudioCodec = item.AudioCodec,
            FileSizeBytes = item.FileSizeBytes,
            ThumbnailPath = item.ThumbnailPath,
            TimelineFramePaths = item.TimelineFramePaths.ToArray(),
            Waveform = item.Waveform,
            IsMissing = item.IsMissing
        }).ToArray();
    }

    public FrameRate FrameRateValue { get; }
    public double? InPoint { get; }
    public double? OutPoint { get; }
    public IReadOnlyList<TimelineClip> Clips { get; }
    public IReadOnlyList<TextOverlay> TextOverlays { get; }
    public IReadOnlyList<Models.TimelineMarker> Markers { get; }
    public IReadOnlyList<MediaAsset> Media { get; }

    public double Duration => Math.Max(
        Clips.Count == 0 ? 0 : Clips.Max(item => item.End),
        TextOverlays.Count == 0 ? 0 : TextOverlays.Max(item => item.End));
    public double TimelineDisplayDuration => Math.Max(1200, Duration + 1200);
    public int VisualTrackCount => RequiredTrackCount(Models.TrackKind.Visual);
    public int AudioTrackCount => RequiredTrackCount(Models.TrackKind.Audio);

    public MediaAsset? FindAsset(Guid id) => Media.FirstOrDefault(item => item.Id == id);

    public IReadOnlyList<TimelineClip> GetTrackClips(Models.TrackKind kind, int index)
        => Clips.Where(item => item.Track == kind && item.TrackIndex == index)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .ToArray();

    public static TimelineReadModel From(EditorProject source)
        => new(source ?? throw new ArgumentNullException(nameof(source)));

    private int RequiredTrackCount(Models.TrackKind kind)
    {
        var highest = Clips.Where(item => item.Track == kind)
            .Select(item => item.TrackIndex)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Max(2, highest + 2);
    }
}
