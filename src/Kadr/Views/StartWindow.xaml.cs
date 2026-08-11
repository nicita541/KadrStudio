using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using KadrStudio.Services;
using Microsoft.Win32;

namespace KadrStudio.Views;

public partial class StartWindow : Window
{
    private readonly RecentProjectsService _recentProjectsService = new();

    public StartWindow()
    {
        InitializeComponent();
        DataContext = this;
        RefreshRecentProjects();
    }

    public ObservableCollection<RecentProjectEntry> RecentProjects { get; } = new();

    private void NewProject_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть проект Kadr Studio",
            Filter = "Проект Kadr Studio (*.kadr)|*.kadr|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            OpenEditor(dialog.FileName);
        }
    }

    private void OpenSelectedRecent_Click(object sender, RoutedEventArgs e) => OpenSelectedRecent();

    private void RecentProjectsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelectedRecent();

    private void OpenSelectedRecent()
    {
        if (RecentProjectsList.SelectedItem is not RecentProjectEntry entry)
        {
            return;
        }

        if (!File.Exists(entry.Path))
        {
            MessageBox.Show(this, "Файл проекта больше не существует.", "Kadr Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
            _recentProjectsService.Remove(entry.Path);
            RefreshRecentProjects();
            return;
        }

        OpenEditor(entry.Path);
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (RecentProjectsList.SelectedItem is not RecentProjectEntry entry)
        {
            return;
        }

        _recentProjectsService.Remove(entry.Path);
        RefreshRecentProjects();
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var entry in _recentProjectsService.Load())
        {
            RecentProjects.Add(entry);
        }

        EmptyRecentText.Visibility = RecentProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentProjectsList.Visibility = RecentProjects.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenEditor(string? projectPath)
    {
        var editor = new MainWindow(projectPath);
        Application.Current.MainWindow = editor;
        editor.Show();
        Close();
    }
}
