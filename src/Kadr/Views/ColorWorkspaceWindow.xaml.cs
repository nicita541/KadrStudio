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
        _editingClip = _viewModel.CreateSelectedClipDraft(TrackKind.Visual);
        WorkspaceRoot.DataContext = _editingClip;
        SelectedClipNameTextBlock.Text = _editingClip is null
            ? "Выберите видеоклип или связанный звук на таймлайне"
            : _viewModel.Project.FindAsset(_editingClip.AssetId)?.Name ?? "Видеоклип";
        WorkspaceRoot.IsEnabled = _editingClip is not null;
        SelectedClipNameTextBlock.IsEnabled = true;
    }

    private void Edit_End(object sender, RoutedEventArgs e)
        => CommitDraft("Цветокоррекция изменена");

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_editingClip is null) return;
        _editingClip.Brightness = 0;
        _editingClip.Contrast = 1;
        _editingClip.Saturation = 1;
        _editingClip.Temperature = 0;
        CommitDraft("Цветокоррекция сброшена");
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
