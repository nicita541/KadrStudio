using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using KadrStudio.Core.Domain;

namespace KadrStudio.Views;

public partial class UpscaleWindow : Window
{
    public UpscaleWindow(IEnumerable<TimelineTrack> tracks, Guid? initiallySelectedTrackId)
    {
        InitializeComponent();
        Tracks = new ObservableCollection<UpscaleTrackChoice>(tracks.Select(track =>
            new UpscaleTrackChoice(track.Id, $"V{track.Index + 1} — {track.Name}",
                track.Id == initiallySelectedTrackId)));
        if (!Tracks.Any(track => track.IsSelected) && Tracks.Count > 0) Tracks[0].IsSelected = true;
        DataContext = this;
    }

    public ObservableCollection<UpscaleTrackChoice> Tracks { get; }
    public IReadOnlyList<Guid> SelectedTrackIds => Tracks.Where(track => track.IsSelected).Select(track => track.Id).ToArray();
    public UpscaleScaleMode ScaleMode =>
        ScaleComboBox.SelectedItem is ComboBoxItem { Tag: string value } &&
        Enum.TryParse<UpscaleScaleMode>(value, out var mode) ? mode : UpscaleScaleMode.Auto2160p;

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrackIds.Count == 0)
        {
            MessageBox.Show(this, "Выберите хотя бы одну видеодорожку.", "AI Upscale",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class UpscaleTrackChoice : INotifyPropertyChanged
{
    private bool _isSelected;
    public UpscaleTrackChoice(Guid id, string label, bool isSelected) => (Id, Label, _isSelected) = (id, label, isSelected);
    public Guid Id { get; }
    public string Label { get; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
