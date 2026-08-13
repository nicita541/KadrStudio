using System.Text.Json.Serialization;
using KadrStudio.ViewModels;

namespace KadrStudio.Models;

public sealed class MediaAsset : ObservableObject
{
    private string _path = string.Empty;
    private string? _thumbnailPath;
    private IReadOnlyList<string> _timelineFramePaths = Array.Empty<string>();
    private string? _waveformPath;
    private IReadOnlyList<float> _waveformPeaks = Array.Empty<float>();
    private bool _isMissing;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public MediaKind Kind { get; set; }

    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public bool HasAudio { get; set; }
    public string VideoCodec { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }

    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set => SetProperty(ref _thumbnailPath, value);
    }

    [JsonIgnore]
    public IReadOnlyList<string> TimelineFramePaths
    {
        get => _timelineFramePaths;
        set => SetProperty(ref _timelineFramePaths, value ?? Array.Empty<string>());
    }

    [JsonIgnore]
    public string? WaveformPath
    {
        get => _waveformPath;
        set => SetProperty(ref _waveformPath, value);
    }

    [JsonIgnore]
    public IReadOnlyList<float> WaveformPeaks
    {
        get => _waveformPeaks;
        set => SetProperty(ref _waveformPeaks, value ?? Array.Empty<float>());
    }

    [JsonIgnore]
    public bool IsMissing
    {
        get => _isMissing;
        set => SetProperty(ref _isMissing, value);
    }

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        MediaKind.Video => "Видео",
        MediaKind.Audio => "Аудио",
        MediaKind.Image => "Изображение",
        _ => "Медиа"
    };

    [JsonIgnore]
    public string DurationLabel => Kind == MediaKind.Image
        ? "Изображение"
        : TimeSpan.FromSeconds(Math.Max(0, Duration)).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    [JsonIgnore]
    public string ResolutionLabel => Width > 0 && Height > 0 ? $"{Width}×{Height}" : string.Empty;
}
