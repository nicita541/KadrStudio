using System.ComponentModel;
using System.Windows;
using KadrStudio.Models;
using KadrStudio.ViewModels;

namespace KadrStudio.Views;

public partial class ColorWorkspaceWindow : Window
{
    private readonly MainViewModel _viewModel;
    private TimelineClip? _editingClip;

    public ColorWorkspaceWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        FollowSelection();
    }

    public void FollowSelection()
    {
        var selected = _viewModel.SelectedClip;
        _editingClip = selected?.Track == TrackKind.Visual
            ? selected
            : selected?.LinkGroupId is Guid groupId
                ? _viewModel.Project.Clips.FirstOrDefault(clip => clip.Track == TrackKind.Visual && clip.LinkGroupId == groupId)
                : null;
        WorkspaceRoot.DataContext = _editingClip;
        SelectedClipNameTextBlock.Text = _editingClip is null
            ? "Выберите видеоклип или связанный звук на таймлайне"
            : _viewModel.Project.FindAsset(_editingClip.AssetId)?.Name ?? "Видеоклип";
        WorkspaceRoot.IsEnabled = _editingClip is not null;
        SelectedClipNameTextBlock.IsEnabled = true;
    }

    private void Edit_Begin(object sender, RoutedEventArgs e)
    {
        if (_editingClip is not null) _viewModel.BeginEdit();
    }

    private void Edit_End(object sender, RoutedEventArgs e)
        => _viewModel.CommitEdit("Цветокоррекция изменена");

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_editingClip is null) return;
        _viewModel.BeginEdit();
        _editingClip.Brightness = 0;
        _editingClip.Contrast = 1;
        _editingClip.Saturation = 1;
        _editingClip.Temperature = 0;
        _viewModel.CommitEdit("Цветокоррекция сброшена");
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedClip) or nameof(MainViewModel.Project)) FollowSelection();
    }

    private void Window_Closed(object? sender, EventArgs e)
        => _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
}
