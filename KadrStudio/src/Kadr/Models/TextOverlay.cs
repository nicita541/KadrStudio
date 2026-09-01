using KadrStudio.ViewModels;

namespace KadrStudio.Models;

public sealed class TextOverlay : ObservableObject
{
    private double _start;
    private double _duration = 3;
    private string _text = "Текст";
    private string _fontFamily = "Segoe UI";
    private double _fontSize = 48;
    private double _x = 0.5;
    private double _y = 0.82;
    private double _rotation;
    private string _color = "#FFFFFF";
    private double _boxWidth = 0.6;
    private double _boxHeight = 0.18;

    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsSubtitle { get; set; }

    public double Start
    {
        get => _start;
        set
        {
            if (SetProperty(ref _start, Math.Max(0, value))) OnPropertyChanged(nameof(TimeLabel));
        }
    }

    public double Duration
    {
        get => _duration;
        set
        {
            if (SetProperty(ref _duration, Math.Max(0.1, value))) OnPropertyChanged(nameof(TimeLabel));
        }
    }

    public double End => Start + Duration;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? string.Empty);
    }

    public string FontFamily
    {
        get => _fontFamily;
        set => SetProperty(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim());
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, 10, 240));
    }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, Math.Clamp(value, 0, 1));
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, Math.Clamp(value, 0, 1));
    }

    public double Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, Math.Clamp(value, -180, 180));
    }

    public string Color
    {
        get => _color;
        set => SetProperty(ref _color, string.IsNullOrWhiteSpace(value) ? "#FFFFFF" : value.Trim());
    }

    public double BoxWidth
    {
        get => _boxWidth;
        set => SetProperty(ref _boxWidth, Math.Clamp(value, 0.08, 1));
    }

    public double BoxHeight
    {
        get => _boxHeight;
        set => SetProperty(ref _boxHeight, Math.Clamp(value, 0.06, 1));
    }

    public string TimeLabel => $"{FormatTime(Start)}–{FormatTime(End)}";

    public TextOverlay Clone() => new()
    {
        Id = Id,
        IsSubtitle = IsSubtitle,
        Start = Start,
        Duration = Duration,
        Text = Text,
        FontFamily = FontFamily,
        FontSize = FontSize,
        X = X,
        Y = Y,
        Rotation = Rotation,
        Color = Color,
        BoxWidth = BoxWidth,
        BoxHeight = BoxHeight
    };

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}
