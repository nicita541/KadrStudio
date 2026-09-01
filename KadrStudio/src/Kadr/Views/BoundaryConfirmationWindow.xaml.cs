using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KadrStudio.Core.Domain;

namespace KadrStudio.Views;

public partial class BoundaryConfirmationWindow : Window
{
    private readonly Func<Guid, TimelineTime, CancellationToken, Task<string?>> _frameProvider;
    private readonly CancellationToken _cancellationToken;
    private int _previewGeneration;

    public BoundaryConfirmationWindow(
        BoundaryReviewRequest request,
        Func<Guid, TimelineTime, CancellationToken, Task<string?>> frameProvider,
        CancellationToken cancellationToken)
    {
        InitializeComponent();
        _frameProvider = frameProvider;
        _cancellationToken = cancellationToken;
        Rows = new ObservableCollection<BoundaryRow>(request.Candidates.Select(candidate => new BoundaryRow(candidate)));
        var episodeCount = request.Candidates.Select(item => item.SourceId).Distinct().Count();
        BoundaryHeadingText.Text = $"Покадровая проверка: {episodeCount} сер. · {request.Candidates.Length} границ";
        ConfirmBoundariesButton.Content = $"Подтвердить {request.Candidates.Length} границ";
        DataContext = this;
        Loaded += (_, _) => CandidateList.SelectedIndex = 0;
    }

    public ObservableCollection<BoundaryRow> Rows { get; }
    public ImmutableArray<BoundaryConfirmation> Confirmations { get; private set; } = [];

    private async void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RefreshFramesAsync();
    private async void PreviousFrame_Click(object sender, RoutedEventArgs e) { if (Selected is { } row) { row.FrameNumber--; await RefreshFramesAsync(); } }
    private async void NextFrame_Click(object sender, RoutedEventArgs e) { if (Selected is { } row) { row.FrameNumber++; await RefreshFramesAsync(); } }
    private BoundaryRow? Selected => CandidateList.SelectedItem as BoundaryRow;

    private async Task RefreshFramesAsync()
    {
        if (Selected is not { } row) return;
        var generation = ++_previewGeneration;
        ExactTimeText.Text = $"PTS {row.Time.Ticks}/{TimelineTime.TicksPerSecond} • кадр {row.FrameNumber} • {row.Candidate.FrameRate}";
        EvidenceText.Text = $"уверенность {row.Candidate.Confidence:P0} • признаков {row.Candidate.EvidenceChannels.Distinct().Count()}";
        try
        {
            var beforeTime = TimelineTime.FromFrames(Math.Max(0, row.FrameNumber - 1), row.Candidate.FrameRate);
            var afterTime = row.Time;
            var paths = await Task.WhenAll(
                _frameProvider(row.Candidate.SourceId, beforeTime, _cancellationToken),
                _frameProvider(row.Candidate.SourceId, afterTime, _cancellationToken));
            if (generation != _previewGeneration) return;
            BeforeImage.Source = Load(paths[0]);
            AfterImage.Source = Load(paths[1]);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { EvidenceText.Text = "Кадры недоступны: " + exception.Message; }
    }

    private static BitmapImage? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path, UriKind.Absolute); image.EndInit(); image.Freeze();
        return image;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmations = Rows.Select(row => new BoundaryConfirmation(
            row.Candidate.Id, row.Time, row.FrameNumber, DateTimeOffset.UtcNow, Accepted: true)).ToImmutableArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class BoundaryRow : INotifyPropertyChanged
{
    private long _frameNumber;
    public BoundaryRow(BoundaryCandidate candidate) { Candidate = candidate; _frameNumber = candidate.FrameNumber; }
    public BoundaryCandidate Candidate { get; }
    public long FrameNumber { get => _frameNumber; set { var normalized = Math.Max(0, value); if (_frameNumber == normalized) return; _frameNumber = normalized; OnPropertyChanged(); OnPropertyChanged(nameof(Time)); OnPropertyChanged(nameof(Label)); } }
    public TimelineTime Time => TimelineTime.FromFrames(FrameNumber, Candidate.FrameRate);
    public string Label => $"{(string.IsNullOrWhiteSpace(Candidate.SourceLabel) ? "Серия" : Candidate.SourceLabel)} · {RoleLabel(Candidate.Role)} — {(Candidate.IsStart ? "начало" : "конец")} • кадр {FrameNumber} • PTS {Time.Ticks}/{TimelineTime.TicksPerSecond}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string RoleLabel(SegmentRole role) => role == SegmentRole.Opening ? "Opening" : role == SegmentRole.Ending ? "Ending" : role.ToString();
}
