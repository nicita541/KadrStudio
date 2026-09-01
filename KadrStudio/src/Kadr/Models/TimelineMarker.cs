using KadrStudio.ViewModels;

namespace KadrStudio.Models;

public sealed class TimelineMarker : ObservableObject
{
    private double _start;
    private double _duration = 0.1;
    private string _title = string.Empty;
    private string _description = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public MarkerKind Kind { get; set; }

    public double Start
    {
        get => _start;
        set => SetProperty(ref _start, Math.Max(0, value));
    }

    public double Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, Math.Max(0.1, value));
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value?.Trim() ?? string.Empty);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value?.Trim() ?? string.Empty);
    }

    public double Confidence { get; set; }
    public double SourceStart { get; set; }
    public string Query { get; set; } = string.Empty;
    public double End => Start + Duration;

    public string TimeLabel => $"{FormatTime(Start)}–{FormatTime(End)}";
    public string ConfidenceLabel => Confidence <= 0 ? string.Empty : $"{Confidence:P0}";

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
}
