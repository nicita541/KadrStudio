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
        _editingClip = _viewModel.CreateSelectedClipDraft(TrackKind.Audio);
        WorkspaceRoot.DataContext = _editingClip;
        SelectedAudioNameTextBlock.Text = _editingClip is null
            ? "Выберите аудиоклип или связанное видео на таймлайне"
            : _viewModel.Project.FindAsset(_editingClip.AssetId)?.Name ?? "Аудиоклип";
        WorkspaceRoot.IsEnabled = _editingClip is not null;
        SelectedAudioNameTextBlock.IsEnabled = true;
    }

    private void Edit_End(object sender, RoutedEventArgs e)
    {
        CommitDraft("Настройки звука изменены");
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _editingClip is null) return;
        CommitDraft("Звук клипа изменён");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_editingClip is null) return;
        _editingClip.Volume = 1;
        _editingClip.Pan = 0;
        _editingClip.FadeIn = 0;
        _editingClip.FadeOut = 0;
        _editingClip.Bass = 0;
        _editingClip.Mid = 0;
        _editingClip.Treble = 0;
        _editingClip.IsMuted = false;
        CommitDraft("Настройки звука сброшены");
    }

    private void CommitDraft(string description)
    {
        if (_editingClip is null) return;
        _viewModel.CommitClipDraft(_editingClip, description);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedClip) or nameof(MainViewModel.Project)) FollowSelection();
    }

    private void Window_Closed(object? sender, EventArgs e)
        => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
}
