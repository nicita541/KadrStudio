using System.Text.Json.Serialization;
using KadrStudio.ViewModels;

namespace KadrStudio.Models;

public sealed class TimelineClip : ObservableObject
{
    private double _start;
    private double _sourceStart;
    private double _duration;
    private double _volume = 1.0;
    private bool _isMuted;
    private int _trackIndex;
    private double _pan;
    private double _fadeIn;
    private double _fadeOut;
    private double _bass;
    private double _mid;
    private double _treble;
    private double _brightness;
    private double _contrast = 1;
    private double _saturation = 1;
    private double _temperature;
    private double _positionX = 0.5;
    private double _positionY = 0.5;
    private double _scaleX = 1;
    private double _scaleY = 1;
    private double _rotation;
    private double _cropLeft;
    private double _cropTop;
    private double _cropRight;
    private double _cropBottom;
    private double _opacity = 1;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public TrackKind Track { get; set; }
    public Guid? LinkGroupId { get; set; }

    public int TrackIndex
    {
        get => _trackIndex;
        set => SetProperty(ref _trackIndex, Math.Max(0, value));
    }

    public double Start
    {
        get => _start;
        set => SetProperty(ref _start, Math.Max(0, value));
    }

    public double SourceStart
    {
        get => _sourceStart;
        set => SetProperty(ref _sourceStart, Math.Max(0, value));
    }

    public double Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, Math.Max(0.1, value));
    }

    public double Volume
    {
        get => _volume;
        set => SetProperty(ref _volume, Math.Clamp(value, 0, 2));
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => SetProperty(ref _isMuted, value);
    }

    public double Pan
    {
        get => _pan;
        set => SetProperty(ref _pan, Math.Clamp(value, -1, 1));
    }

    public double FadeIn
    {
        get => _fadeIn;
        set => SetProperty(ref _fadeIn, Math.Clamp(value, 0, Duration));
    }

    public double FadeOut
    {
        get => _fadeOut;
        set => SetProperty(ref _fadeOut, Math.Clamp(value, 0, Duration));
    }

    public double Bass
    {
        get => _bass;
        set => SetProperty(ref _bass, Math.Clamp(value, -20, 20));
    }

    public double Mid
    {
        get => _mid;
        set => SetProperty(ref _mid, Math.Clamp(value, -20, 20));
    }

    public double Treble
    {
        get => _treble;
        set => SetProperty(ref _treble, Math.Clamp(value, -20, 20));
    }

    public double Brightness
    {
        get => _brightness;
        set => SetProperty(ref _brightness, Math.Clamp(value, -1, 1));
    }

    public double Contrast
    {
        get => _contrast;
        set => SetProperty(ref _contrast, Math.Clamp(value, 0, 3));
    }

    public double Saturation
    {
        get => _saturation;
        set => SetProperty(ref _saturation, Math.Clamp(value, 0, 3));
    }

    public double Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, Math.Clamp(value, -1, 1));
    }

    public double PositionX { get => _positionX; set => SetProperty(ref _positionX, Math.Clamp(value, -5, 5)); }
    public double PositionY { get => _positionY; set => SetProperty(ref _positionY, Math.Clamp(value, -5, 5)); }
    public double ScaleX { get => _scaleX; set => SetProperty(ref _scaleX, Math.Clamp(value, 0.01, 100)); }
    public double ScaleY { get => _scaleY; set => SetProperty(ref _scaleY, Math.Clamp(value, 0.01, 100)); }
    public double Rotation { get => _rotation; set => SetProperty(ref _rotation, Math.Clamp(value, -360, 360)); }
    public double CropLeft { get => _cropLeft; set => SetProperty(ref _cropLeft, Math.Clamp(value, 0, 0.99)); }
    public double CropTop { get => _cropTop; set => SetProperty(ref _cropTop, Math.Clamp(value, 0, 0.99)); }
    public double CropRight { get => _cropRight; set => SetProperty(ref _cropRight, Math.Clamp(value, 0, 0.99)); }
    public double CropBottom { get => _cropBottom; set => SetProperty(ref _cropBottom, Math.Clamp(value, 0, 0.99)); }
    public double Opacity { get => _opacity; set => SetProperty(ref _opacity, Math.Clamp(value, 0, 1)); }

    [JsonIgnore]
    public double End => Start + Duration;

    public TimelineClip Clone() => new()
    {
        Id = Id,
        AssetId = AssetId,
        Track = Track,
        LinkGroupId = LinkGroupId,
        TrackIndex = TrackIndex,
        Start = Start,
        SourceStart = SourceStart,
        Duration = Duration,
        Volume = Volume,
        IsMuted = IsMuted,
        Pan = Pan,
        FadeIn = FadeIn,
        FadeOut = FadeOut,
        Bass = Bass,
        Mid = Mid,
        Treble = Treble,
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Temperature = Temperature,
        PositionX = PositionX,
        PositionY = PositionY,
        ScaleX = ScaleX,
        ScaleY = ScaleY,
        Rotation = Rotation,
        CropLeft = CropLeft,
        CropTop = CropTop,
        CropRight = CropRight,
        CropBottom = CropBottom,
        Opacity = Opacity
    };
}
