using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KadrStudio.Models;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using Microsoft.Win32;

namespace KadrStudio.Views;

public partial class ExportWindow : Window
{
    private readonly ProjectState _project;
    private readonly ExportService _exportService;
    private CancellationTokenSource? _cancellation;
    private bool _isExporting;
    private string? _completedOutputPath;

    public ExportWindow(ProjectState project, ExportService exportService)
    {
        InitializeComponent();
        _project = project;
        _exportService = exportService;

        var videosDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videosDirectory))
        {
            videosDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
        OutputPathText.Text = Path.Combine(videosDirectory, SanitizeFileName(project.Name) + ".mp4");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить готовое видео",
            Filter = "Видео MP4 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileName(OutputPathText.Text),
            InitialDirectory = Path.GetDirectoryName(OutputPathText.Text)
        };
        if (dialog.ShowDialog(this) == true)
        {
            OutputPathText.Text = dialog.FileName;
        }
    }

    private async void StartExport_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPathText.Text))
        {
            MessageBox.Show(this, "Выберите путь для готового видео.", "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isExporting = true;
        _completedOutputPath = null;
        _cancellation = new CancellationTokenSource();
        SetControlsForExport(isExporting: true);

        try
        {
            var settings = ReadSettings();
            var progress = new Progress<ExportProgress>(value =>
            {
                ExportProgressBar.Value = value.Percent;
                ProgressPercentText.Text = $"{Math.Round(value.Percent)}%";
                StageText.Text = value.Stage;
                DetailText.Text = value.Detail;
            });

            await _exportService.ExportAsync(
                _project,
                OutputPathText.Text,
                settings,
                progress,
                _cancellation.Token);

            _completedOutputPath = OutputPathText.Text;
            StageText.Text = "Экспорт завершён";
            DetailText.Text = Path.GetFileName(_completedOutputPath);
            ExportProgressBar.Value = 100;
            ProgressPercentText.Text = "100%";
            OpenFolderButton.Visibility = Visibility.Visible;
            MessageBox.Show(this, "Видео успешно экспортировано.", "Kadr Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Экспорт отменён";
            DetailText.Text = "Готовый файл не был создан.";
        }
        catch (Exception exception)
        {
            StageText.Text = "Ошибка экспорта";
            DetailText.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isExporting = false;
            _cancellation.Dispose();
            _cancellation = null;
            SetControlsForExport(isExporting: false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting)
        {
            _cancellation?.Cancel();
            CancelButton.IsEnabled = false;
            StageText.Text = "Отмена…";
            return;
        }

        Close();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_completedOutputPath) || !File.Exists(_completedOutputPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_completedOutputPath}\"",
            UseShellExecute = true
        });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExporting)
        {
            return;
        }

        e.Cancel = true;
        _cancellation?.Cancel();
    }

    private ExportSettings ReadSettings()
    {
        var resolutionTag = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var qualityTag = (QualityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return new ExportSettings
        {
            Resolution = Enum.TryParse<ExportResolution>(resolutionTag, out var resolution)
                ? resolution
                : ExportResolution.P1080,
            Quality = int.TryParse(qualityTag, out var quality) ? quality : 20,
            UseHardwareEncoding = HardwareEncodingCheck.IsChecked == true
        };
    }

    private void SetControlsForExport(bool isExporting)
    {
        ResolutionCombo.IsEnabled = !isExporting;
        QualityCombo.IsEnabled = !isExporting;
        HardwareEncodingCheck.IsEnabled = !isExporting;
        StartExportButton.IsEnabled = !isExporting;
        StartExportButton.Visibility = isExporting ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = isExporting ? "Отменить" : "Закрыть";
        CancelButton.IsEnabled = true;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Видео" : cleaned;
    }
}
