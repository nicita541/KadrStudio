using System.Windows;
using System.Windows.Input;
using KadrStudio.Services;
using KadrStudio.ViewModels;

namespace KadrStudio.Views;

public partial class ProjectHistoryWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ProjectHistoryWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Loaded += async (_, _) => await RefreshHistoryAsync();
    }

    private async void CreateCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = await _viewModel.CreateHistoryCheckpointAsync(CheckpointMessageTextBox.Text);
            await RefreshHistoryAsync(entry.Id);
            CheckpointMessageTextBox.SelectAll();
            CheckpointMessageTextBox.Focus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось создать точку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RestoreCheckpoint_Click(object sender, RoutedEventArgs e) => await RestoreSelectedCheckpointAsync();

    private async void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await RestoreSelectedCheckpointAsync();

    private async Task RestoreSelectedCheckpointAsync()
    {
        if (HistoryListBox.SelectedItem is not ProjectHistoryEntry entry)
        {
            MessageBox.Show(this, "Выберите контрольную точку.", "История проекта", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Вернуть проект к версии «{entry.Message}»?\n\nТекущее состояние будет автоматически сохранено отдельной точкой, поэтому его можно будет вернуть.",
            "Откат проекта",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _viewModel.RestoreHistoryCheckpointAsync(entry);
            await RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось восстановить проект", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is not ProjectHistoryEntry entry)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить контрольную точку «{entry.Message}»? Это действие нельзя отменить.",
            "Удаление контрольной точки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _viewModel.DeleteHistoryCheckpointAsync(entry);
            await RefreshHistoryAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось удалить точку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshHistoryAsync(Guid? selectId = null)
    {
        var entries = await _viewModel.GetHistoryCheckpointsAsync();
        HistoryListBox.ItemsSource = entries;
        EmptyHistoryTextBlock.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectId is Guid id)
        {
            HistoryListBox.SelectedItem = entries.FirstOrDefault(entry => entry.Id == id);
        }
        else if (entries.Count > 0)
        {
            HistoryListBox.SelectedIndex = 0;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
