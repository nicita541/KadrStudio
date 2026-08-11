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
        RefreshHistory();
    }

    private void CreateCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = _viewModel.CreateHistoryCheckpoint(CheckpointMessageTextBox.Text);
            RefreshHistory(entry.Id);
            CheckpointMessageTextBox.SelectAll();
            CheckpointMessageTextBox.Focus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось создать точку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RestoreCheckpoint_Click(object sender, RoutedEventArgs e) => RestoreSelectedCheckpoint();

    private void HistoryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RestoreSelectedCheckpoint();

    private void RestoreSelectedCheckpoint()
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
            _viewModel.RestoreHistoryCheckpoint(entry);
            RefreshHistory();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось восстановить проект", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteCheckpoint_Click(object sender, RoutedEventArgs e)
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
            _viewModel.DeleteHistoryCheckpoint(entry);
            RefreshHistory();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Не удалось удалить точку", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshHistory(Guid? selectId = null)
    {
        var entries = _viewModel.GetHistoryCheckpoints();
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
