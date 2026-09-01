using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using KadrStudio.Core.Domain;
using KadrStudio.ViewModels;

namespace KadrStudio.Models;

/// <summary>
/// Read-only-in-practice WPF projection rebuilt from an immutable ProjectState.
/// It is never persisted, rendered, or used as an edit source.
/// </summary>
public sealed class ProjectViewState : ObservableObject
{
    private string _name = "Новый проект";
    private int _canvasWidth = 1920;
    private int _canvasHeight = 1080;
    private int _frameRateNumerator = 30;
    private int _frameRateDenominator = 1;
    private double? _inPoint;
    private double? _outPoint;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? "Новый проект" : value.Trim());
    }

    public int CanvasWidth
    {
        get => _canvasWidth;
        set => SetProperty(ref _canvasWidth, Math.Clamp(value, 320, 7680));
    }

    public int CanvasHeight
    {
        get => _canvasHeight;
        set => SetProperty(ref _canvasHeight, Math.Clamp(value, 240, 4320));
    }

    public int FrameRate
    {
        get => (int)Math.Round(FrameRateValue.FramesPerSecond);
        set => FrameRateValue = new FrameRate(Math.Clamp(value, 1, 240));
    }

    /// <summary>
    /// Exact sequence timebase. FrameRate remains a compatibility convenience
    /// for legacy WPF bindings and must never be persisted as the source of truth.
    /// </summary>
    [JsonIgnore]
    public FrameRate FrameRateValue
    {
        get => new(_frameRateNumerator, _frameRateDenominator);
        set
        {
            if (_frameRateNumerator == value.Numerator && _frameRateDenominator == value.Denominator) return;
            _frameRateNumerator = value.Numerator;
            _frameRateDenominator = value.Denominator;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FrameRate));
        }
    }

    public ObservableCollection<MediaAsset> Media { get; set; } = new();
    public ObservableCollection<EditorTrack> Tracks { get; set; } = new();
    public ObservableCollection<TimelineClip> Clips { get; set; } = new();
    public ObservableCollection<TimelineMarker> Markers { get; set; } = new();
    public ObservableCollection<TextOverlay> TextOverlays { get; set; } = new();

    public double? InPoint
    {
        get => _inPoint;
        set => SetProperty(ref _inPoint, value is null ? null : Math.Max(0, value.Value));
    }

    public double? OutPoint
    {
        get => _outPoint;
        set => SetProperty(ref _outPoint, value is null ? null : Math.Max(0, value.Value));
    }

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public double Duration => Math.Max(
        Clips.Count == 0 ? 0 : Clips.Max(clip => clip.End),
        TextOverlays.Count == 0 ? 0 : TextOverlays.Max(overlay => overlay.End));

    [JsonIgnore]
    public double TimelineDisplayDuration => Math.Max(1200, Duration + 1200);

    public MediaAsset? FindAsset(Guid assetId) => Media.FirstOrDefault(asset => asset.Id == assetId);

    public TimelineClip? FindClip(Guid clipId) => Clips.FirstOrDefault(clip => clip.Id == clipId);

    public IReadOnlyList<TimelineClip> GetVisualClips(int? trackIndex = null) => Clips
        .Where(clip => clip.Track == TrackKind.Visual && (trackIndex is null || clip.TrackIndex == trackIndex))
        .OrderBy(clip => clip.TrackIndex)
        .ThenBy(clip => clip.Start)
        .ThenBy(clip => clip.Id)
        .ToList();

    public IReadOnlyList<TimelineClip> GetAudioClips(int? trackIndex = null) => Clips
        .Where(clip => clip.Track == TrackKind.Audio && (trackIndex is null || clip.TrackIndex == trackIndex))
        .OrderBy(clip => clip.TrackIndex)
        .ThenBy(clip => clip.Start)
        .ThenBy(clip => clip.Id)
        .ToList();

    [JsonIgnore]
    public int VisualTrackCount => RequiredTrackCount(TrackKind.Visual);

    [JsonIgnore]
    public int AudioTrackCount => RequiredTrackCount(TrackKind.Audio);

    public IReadOnlyList<TimelineClip> GetTrackClips(TrackKind kind, int trackIndex) => Clips
        .Where(clip => clip.Track == kind && clip.TrackIndex == trackIndex)
        .OrderBy(clip => clip.Start)
        .ThenBy(clip => clip.Id)
        .ToList();

    private int RequiredTrackCount(TrackKind kind)
    {
        var highestOccupied = Clips
            .Where(clip => clip.Track == kind)
            .Select(clip => clip.TrackIndex)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Max(2, highestOccupied + 2);
    }

}
