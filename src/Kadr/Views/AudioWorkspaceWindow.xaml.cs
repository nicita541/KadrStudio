using System.ComponentModel;
using System.Windows;
using KadrStudio.Models;
using KadrStudio.ViewModels;

namespace KadrStudio.Views;

public partial class AudioWorkspaceWindow : Window
{
    private readonly MainViewModel _viewModel;
    private TimelineClip? _editingClip;

    public AudioWorkspaceWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        FollowSelection();
    }

    public void FollowSelection()
    {
        var selected = _viewModel.SelectedClip;
        _editingClip = selected?.Track == TrackKind.Audio
            ? selected
            : selected?.LinkGroupId is Guid groupId
                ? _viewModel.Project.Clips.FirstOrDefault(clip => clip.Track == TrackKind.Audio && clip.LinkGroupId == groupId)
                : null;
        WorkspaceRoot.DataContext = _editingClip;
        SelectedAudioNameTextBlock.Text = _editingClip is null
            ? "Выберите аудиоклип или связанное видео на таймлайне"
            : _viewModel.Project.FindAsset(_editingClip.AssetId)?.Name ?? "Аудиоклип";
        WorkspaceRoot.IsEnabled = _editingClip is not null;
        SelectedAudioNameTextBlock.IsEnabled = true;
    }

    private void Edit_Begin(object sender, RoutedEventArgs e)
    {
        if (_editingClip is not null) _viewModel.BeginEdit();
    }

    private void Edit_End(object sender, RoutedEventArgs e)
    {
        _viewModel.CommitEdit("Настройки звука изменены");
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _editingClip is null) return;
        _viewModel.BeginEdit();
        _viewModel.CommitEdit("Звук клипа изменён");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_editingClip is null) return;
        _viewModel.BeginEdit();
        _editingClip.Volume = 1;
        _editingClip.Pan = 0;
        _editingClip.FadeIn = 0;
        _editingClip.FadeOut = 0;
        _editingClip.Bass = 0;
        _editingClip.Mid = 0;
        _editingClip.Treble = 0;
        _editingClip.IsMuted = false;
        _viewModel.CommitEdit("Настройки звука сброшены");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedClip) or nameof(MainViewModel.Project)) FollowSelection();
    }

    private void Window_Closed(object? sender, EventArgs e)
        => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
}
