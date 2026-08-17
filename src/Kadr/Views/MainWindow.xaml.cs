using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KadrStudio.Controls;
using KadrStudio.Models;
using KadrStudio.Playback;
using KadrStudio.Services;
using KadrStudio.ViewModels;
using Microsoft.Win32;

namespace KadrStudio.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly RecentProjectsService _recentProjectsService = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private Point _mediaDragOrigin;
    private bool _isPlaying;
    private bool _allowClose;
    private bool _isCloseConfirmationPending;
    private bool _isShutdownComplete;
    private double _playbackStartSeconds;
    private readonly PreviewPresenter _previewPresenter;
    private readonly string? _initialProjectPath;
    private CancellationTokenSource? _analysisCancellation;
    private readonly ObservableCollection<OllamaModelInfo> _localAiModels = [];
    private readonly ObservableCollection<ProjectHistoryEntry> _inlineHistoryEntries = [];
    private bool _isRefreshingLocalAiModels;
    private double _previewScale = 1;
    private bool _useHalfQualityPreview = true;
    private AudioWorkspaceWindow? _audioWorkspaceWindow;
    private ColorWorkspaceWindow? _colorWorkspaceWindow;
    private bool _isDraggingPreviewText;
    private Point _previewTextDragOffset;
    private TextOverlay? _previewDraggedOverlay;
    private bool _isEditingPreviewText;
    private bool _isResizingPreviewText;
    private string _previewTextResizeHandle = "BottomRight";
    private Point _previewTextResizeStartPoint;
    private Rect _previewTextResizeStartBounds;
    private string _previewTextBeforeEdit = string.Empty;
    private TextOverlay? _previewEditedOverlay;
    private TextOverlay? _textPanelEditBefore;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialProjectPath)
    {
        _initialProjectPath = initialProjectPath;
        InitializeComponent();
        _viewModel = new MainViewModel();
        _previewPresenter = new PreviewPresenter(PreviewImage, EmptyPreview,
            new FfmpegLocator(), _viewModel.RenderCoordinator, _viewModel.ArtifactStore);
        _previewPresenter.Failed += (_, exception) =>
            Dispatcher.BeginInvoke(() => _viewModel.StatusText = $"Предпросмотр: {exception.Message}");
        _previewPresenter.AudioMeterUpdated += (_, level) => Dispatcher.BeginInvoke(() =>
        {
            AudioLeftMeter.Value = level.LeftPeak;
            AudioRightMeter.Value = level.RightPeak;
        });
        _previewPresenter.SetProject(_viewModel.Project, _useHalfQualityPreview);
        DataContext = _viewModel;
        LocalAiModelComboBox.ItemsSource = _localAiModels;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        _playbackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _playbackTimer.Tick += PlaybackTimer_Tick;

        TimelineEditor.ClipSelected += TimelineEditor_ClipSelected;
        TimelineEditor.TextOverlaySelected += TimelineEditor_TextOverlaySelected;
        TimelineEditor.TextOverlayEditRequested += TimelineEditor_TextOverlayEditRequested;
        TimelineEditor.PlayheadChanged += TimelineEditor_PlayheadChanged;
        TimelineEditor.EditRequested += TimelineEditor_EditRequested;
        TimelineEditor.AssetDropped += TimelineEditor_AssetDropped;
        UpdateWhisperAvailability();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TimelineEditor.Project = _viewModel.Project;
        TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
        UpdateWindowTitle();
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        _ = RefreshLocalAiModelsAsync(showError: false);

        if (!string.IsNullOrWhiteSpace(_initialProjectPath))
        {
            try
            {
                await _viewModel.OpenProjectAsync(_initialProjectPath);
                _recentProjectsService.Add(_initialProjectPath, _viewModel.Project.Name);
                ResetPreviewState();
            }
            catch (Exception exception)
            {
                ShowError("Не удалось открыть проект", exception);
            }
            return;
        }

        if (await _viewModel.HasAutosaveAsync())
        {
            var result = MessageBox.Show(
                this,
                "Найден несохранённый проект с прошлого запуска. Восстановить его?",
                "Восстановление проекта",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await _viewModel.RecoverAutosaveAsync();
                    ResetPreviewState();
                }
                catch (Exception exception)
                {
                    await _viewModel.DiscardAutosaveAsync();
                    ShowError("Не удалось восстановить проект", exception);
                }
            }
            else
            {
                await _viewModel.DiscardAutosaveAsync();
            }
        }
    }

    private async void ImportMedia_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт медиа",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Поддерживаемые медиа|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.wmv;*.webm;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|Видео|*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.wmv;*.webm|Аудио|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg|Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff|Все файлы|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var errors = await _viewModel.ImportFilesAsync(dialog.FileNames);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n\n", errors), "Некоторые файлы не импортированы", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowError("Не удалось импортировать медиа", exception);
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmCanLoseChangesAsync())
        {
            return;
        }

        StopPlayback();
        await _viewModel.NewProjectAsync();
        ResetPreviewState();
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmCanLoseChangesAsync())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Открыть проект Kadr Studio",
            Filter = "Проект Kadr Studio (*.kadr)|*.kadr|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            StopPlayback();
            await _viewModel.OpenProjectAsync(dialog.FileName);
            _recentProjectsService.Add(dialog.FileName, _viewModel.Project.Name);
            ResetPreviewState();
            var missing = _viewModel.Project.Media.Where(asset => asset.IsMissing).Select(asset => asset.Name).ToList();
            if (missing.Count > 0)
            {
                MessageBox.Show(
                    this,
                    "Часть исходников не найдена:\n\n" + string.Join("\n", missing),
                    "Отсутствуют исходные файлы",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            ShowError("Не удалось открыть проект", exception);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
        => await SaveProjectInternalAsync(forceSaveAs: false);

    private void ProjectHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingEditReview)
        {
            MessageBox.Show(
                this,
                "Сначала примите или верните черновик ИИ, затем откройте историю проекта.",
                "История проекта",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        new ProjectHistoryWindow(_viewModel) { Owner = this }.ShowDialog();
    }

    private async void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPanel(showAnalysis: false, showHistory: true, showText: false);
        await RefreshInlineHistoryAsync();
    }

    private void OpenAudioWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_audioWorkspaceWindow is not null)
        {
            _audioWorkspaceWindow.FollowSelection();
            _audioWorkspaceWindow.Activate();
            return;
        }
        _audioWorkspaceWindow = new AudioWorkspaceWindow(_viewModel) { Owner = this };
        _audioWorkspaceWindow.Closed += (_, _) => _audioWorkspaceWindow = null;
        _audioWorkspaceWindow.Show();
    }

    private void OpenColorWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_colorWorkspaceWindow is not null)
        {
            _colorWorkspaceWindow.FollowSelection();
            _colorWorkspaceWindow.Activate();
            return;
        }
        _colorWorkspaceWindow = new ColorWorkspaceWindow(_viewModel) { Owner = this };
        _colorWorkspaceWindow.Closed += (_, _) => _colorWorkspaceWindow = null;
        _colorWorkspaceWindow.Show();
    }

    private async Task<bool> SaveProjectInternalAsync(bool forceSaveAs)
    {
        if (_viewModel.HasPendingEditReview)
        {
            MessageBox.Show(
                this,
                "Сначала примите или верните черновик ИИ. Непроверенный черновик не будет сохранён в проект.",
                "Черновик ИИ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        var path = forceSaveAs ? null : _viewModel.Project.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить проект Kadr Studio",
                Filter = "Проект Kadr Studio (*.kadr)|*.kadr",
                DefaultExt = ".kadr",
                AddExtension = true,
                FileName = SanitizeFileName(_viewModel.Project.Name) + ".kadr"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            await _viewModel.SaveProjectAsync(path);
            _previewPresenter.SetProject(_viewModel.Project, _useHalfQualityPreview);
            _recentProjectsService.Add(path, _viewModel.Project.Name);
            return true;
        }
        catch (Exception exception)
        {
            ShowError("Не удалось сохранить проект", exception);
            return false;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingEditReview)
        {
            MessageBox.Show(
                this,
                "Перед экспортом примите или верните черновик ИИ.",
                "Черновик ИИ",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        StopPlayback();
        var exportWindow = new ExportWindow(_viewModel.Project, _viewModel.ExportService)
        {
            Owner = this
        };
        exportWindow.ShowDialog();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingEditReview)
        {
            _viewModel.StatusText = "Сначала примите или верните черновик ИИ";
            return;
        }
        StopPlayback();
        _viewModel.Undo();
        ResetPreviewState();
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingEditReview)
        {
            _viewModel.StatusText = "Сначала примите или верните черновик ИИ";
            return;
        }
        StopPlayback();
        _viewModel.Redo();
        ResetPreviewState();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (TextOverlayList.SelectedItem is TextOverlay overlay &&
            TimelineEditor.SelectedTextOverlayId == overlay.Id &&
            _viewModel.Playhead > overlay.Start + 0.1 && _viewModel.Playhead < overlay.End - 0.1)
        {
            var rightId = _viewModel.SplitTextOverlay(overlay.Id, _viewModel.Playhead);
            if (rightId is null) return;
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == rightId);
            TimelineEditor.SelectedTextOverlayId = rightId;
            return;
        }
        if (!_viewModel.SplitSelectedAtPlayhead())
        {
            _viewModel.StatusText = "Курсор должен находиться внутри выбранного клипа";
        }
        TimelineEditor.SelectedClipId = _viewModel.SelectedClip?.Id;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        if (TextOverlayList.SelectedItem is TextOverlay overlay && TimelineEditor.SelectedTextOverlayId == overlay.Id)
        {
            _viewModel.DeleteTextOverlay(overlay.Id);
            TimelineEditor.SelectedTextOverlayId = null;
            TextOverlayList.SelectedItem = null;
            UpdateTextOverlayPreview(_viewModel.Playhead);
            return;
        }
        _viewModel.DeleteSelectedClip();
        TimelineEditor.SelectedClipId = null;
        _viewModel.Playhead = Math.Min(_viewModel.Playhead, _viewModel.Project.Duration);
        TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void UnlinkClip_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.UnlinkSelectedClip())
        {
            _viewModel.StatusText = "У выбранного клипа нет связи видео/звук";
        }
        TimelineEditor.InvalidateVisual();
    }

    private void SetInPoint_Click(object sender, RoutedEventArgs e)
    {
        var existingOut = _viewModel.Project.OutPoint;
        double? outPoint = existingOut is double outValue && outValue > _viewModel.Playhead
            ? outValue
            : null;
        _viewModel.SetInOut(_viewModel.Playhead, outPoint,
            $"Точка входа: {FormatEditorTime(_viewModel.Playhead)}");
        AnalysisStartTextBox.Text = FormatEditorTime(_viewModel.Playhead);
        TimelineEditor.InvalidateVisual();
    }

    private void SetOutPoint_Click(object sender, RoutedEventArgs e)
    {
        var existingIn = _viewModel.Project.InPoint;
        double? inPoint = existingIn is double inValue && inValue < _viewModel.Playhead
            ? inValue
            : null;
        _viewModel.SetInOut(inPoint, _viewModel.Playhead,
            $"Точка выхода: {FormatEditorTime(_viewModel.Playhead)}");
        AnalysisEndTextBox.Text = FormatEditorTime(_viewModel.Playhead);
        TimelineEditor.InvalidateVisual();
    }

    private void ClearInOut_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Project.InPoint is null && _viewModel.Project.OutPoint is null)
        {
            return;
        }
        _viewModel.SetInOut(null, null, "Точки In/Out очищены");
        TimelineEditor.InvalidateVisual();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            PausePlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
        => SeekTo(_viewModel.Playhead - 1.0 / Math.Max(1, _viewModel.Project.FrameRateValue.FramesPerSecond));

    private void NextFrame_Click(object sender, RoutedEventArgs e)
        => SeekTo(_viewModel.Playhead + 1.0 / Math.Max(1, _viewModel.Project.FrameRateValue.FramesPerSecond));

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => TimelineEditor.PixelsPerSecond *= 1.2;
    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => TimelineEditor.PixelsPerSecond = Math.Max(GetTimelineFitScale(), TimelineEditor.PixelsPerSecond / 1.2);

    private void TopMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(
            this,
            "Kadr Studio\nНативный локальный видеоредактор\nFFmpeg + Ollama Vision",
            "О программе",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void ZoomToFit_Click(object sender, RoutedEventArgs e)
    {
        var duration = Math.Max(1, _viewModel.Project.TimelineDisplayDuration);
        TimelineEditor.PixelsPerSecond = GetTimelineFitScale();
        TimelineScrollViewer.ScrollToHorizontalOffset(0);
    }

    private double GetTimelineFitScale()
    {
        var duration = Math.Max(1, _viewModel.Project.TimelineDisplayDuration);
        var viewport = Math.Max(240, TimelineScrollViewer.ViewportWidth);
        return Math.Max(0.0001, (viewport - TimelineControl.LeftGutterWidth - 78) / duration);
    }

    private void ShowMedia_Click(object sender, RoutedEventArgs e) => SetLeftPanel(showAnalysis: false, showHistory: false, showText: false);

    private void ShowAnalysis_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPanel(showAnalysis: true, showHistory: false, showText: false);
        if (_localAiModels.Count == 0 && !_isRefreshingLocalAiModels)
        {
            _ = RefreshLocalAiModelsAsync(showError: false);
        }
        var asset = _viewModel.SelectedClipAsset ?? _viewModel.SelectedAsset ?? _viewModel.Project.Media.FirstOrDefault(item => item.Kind == MediaKind.Video);
        if (asset is not null)
        {
            AnalysisAssetComboBox.SelectedItem = asset;
        }

        if (_viewModel.SelectedClip is not null)
        {
            SetAnalysisRangeFromClip(_viewModel.SelectedClip);
        }
        else if (asset is not null)
        {
            AnalysisStartTextBox.Text = FormatEditorTime(0);
            AnalysisEndTextBox.Text = FormatEditorTime(asset.Duration);
        }
    }

    private void ShowText_Click(object sender, RoutedEventArgs e)
    {
        UpdateWhisperAvailability();
        SetLeftPanel(showAnalysis: false, showHistory: false, showText: true);
    }

    private void ShowTransitions_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPanel(showAnalysis: false, showHistory: false, showText: false, showTransitions: true);
        RefreshTransitionList();
    }

    private void SetLeftPanel(bool showAnalysis, bool showHistory, bool showText, bool showTransitions = false)
    {
        MediaPanel.Visibility = showAnalysis || showHistory || showText || showTransitions ? Visibility.Collapsed : Visibility.Visible;
        AnalysisPanel.Visibility = showAnalysis ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        TransitionsPanel.Visibility = showTransitions ? Visibility.Visible : Visibility.Collapsed;
        MediaNavButton.Tag = showAnalysis || showHistory || showText || showTransitions ? null : "Selected";
        AnalysisNavButton.Tag = showAnalysis ? "Selected" : null;
        HistoryNavButton.Tag = showHistory ? "Selected" : null;
        TextNavButton.Tag = showText ? "Selected" : null;
        TransitionsNavButton.Tag = showTransitions ? "Selected" : null;
    }

    private void AddTransition_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedClip is null)
        {
            TransitionStatusText.Text = "Выберите левый клип у склейки на таймлайне.";
            return;
        }
        if (TransitionKindComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<KadrStudio.Core.Domain.TransitionKind>(tag, out var kind))
            return;
        if (!double.TryParse(TransitionDurationTextBox.Text.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
        {
            TransitionStatusText.Text = "Введите длительность числом, например 1,0.";
            return;
        }
        try
        {
            var id = _viewModel.AddTransition(_viewModel.SelectedClip.Id, kind, duration);
            RefreshTransitionList();
            TransitionListBox.SelectedItem = TransitionListBox.Items.Cast<TransitionListItem>()
                .FirstOrDefault(item => item.Id == id);
            TransitionStatusText.Text = "Переход добавлен и сразу участвует в preview/export.";
            UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        }
        catch (Exception exception)
        {
            TransitionStatusText.Text = exception.Message;
        }
    }

    private void DeleteTransition_Click(object sender, RoutedEventArgs e)
    {
        if (TransitionListBox.SelectedItem is not TransitionListItem selected) return;
        _viewModel.DeleteTransition(selected.Id);
        RefreshTransitionList();
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void RefreshTransitionList()
    {
        TransitionListBox.ItemsSource = _viewModel.GetTransitions()
            .OrderBy(item => item.Start)
            .Select(item => new TransitionListItem(
                item.Id,
                $"{TransitionKindLabel(item.Kind)}  {FormatEditorTime(item.Start.TotalSeconds)}  {item.Duration.TotalSeconds:0.##} с"))
            .ToArray();
    }

    private static string TransitionKindLabel(KadrStudio.Core.Domain.TransitionKind kind) => kind switch
    {
        KadrStudio.Core.Domain.TransitionKind.CrossDissolve => "Растворение",
        KadrStudio.Core.Domain.TransitionKind.DipToBlack => "В чёрный",
        KadrStudio.Core.Domain.TransitionKind.DipToWhite => "В белый",
        KadrStudio.Core.Domain.TransitionKind.Wipe => "Вытеснение",
        KadrStudio.Core.Domain.TransitionKind.Slide => "Сдвиг",
        KadrStudio.Core.Domain.TransitionKind.ConstantPowerAudio => "Аудио Constant Power",
        _ => kind.ToString()
    };

    private async Task RefreshInlineHistoryAsync(Guid? selectedId = null)
    {
        _inlineHistoryEntries.Clear();
        foreach (var entry in await _viewModel.GetHistoryCheckpointsAsync())
        {
            _inlineHistoryEntries.Add(entry);
        }
        InlineHistoryList.ItemsSource = _inlineHistoryEntries;
        InlineHistoryEmptyText.Visibility = _inlineHistoryEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InlineHistoryList.SelectedItem = selectedId is Guid id
            ? _inlineHistoryEntries.FirstOrDefault(entry => entry.Id == id)
            : _inlineHistoryEntries.FirstOrDefault();
    }

    private async void CreateInlineCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = await _viewModel.CreateHistoryCheckpointAsync(InlineHistoryMessageTextBox.Text);
            await RefreshInlineHistoryAsync(entry.Id);
            InlineHistoryMessageTextBox.SelectAll();
        }
        catch (Exception exception)
        {
            ShowError("Не удалось создать контрольную точку", exception);
        }
    }

    private async void RestoreInlineCheckpoint_Click(object sender, RoutedEventArgs e) => await RestoreInlineCheckpointAsync();

    private async void InlineHistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await RestoreInlineCheckpointAsync();

    private async Task RestoreInlineCheckpointAsync()
    {
        if (InlineHistoryList.SelectedItem is not ProjectHistoryEntry entry)
        {
            return;
        }
        var answer = MessageBox.Show(
            this,
            $"Вернуть проект к версии «{entry.Message}»? Текущее состояние будет сохранено автоматически.",
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
            ResetPreviewState();
            await RefreshInlineHistoryAsync();
        }
        catch (Exception exception)
        {
            ShowError("Не удалось восстановить проект", exception);
        }
    }

    private void CreateTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        var start = _viewModel.Project.InPoint ?? _viewModel.Playhead;
        var duration = _viewModel.Project.OutPoint is double outPoint && outPoint > start
            ? outPoint - start
            : 3;
        var overlay = new TextOverlay
        {
            Start = start,
            Duration = duration,
            Text = "Новый текст",
            FontFamily = "Segoe UI",
            FontSize = 48,
            X = 0.5,
            Y = 0.82
        };
        _viewModel.AddTextOverlay(overlay);
        TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == overlay.Id);
        TimelineEditor.SelectedTextOverlayId = overlay.Id;
        TimelineEditor.SelectedClipId = null;
        UpdateTextOverlayPreview(_viewModel.Playhead);
    }

    private void DeleteTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (TextOverlayList.SelectedItem is not TextOverlay overlay)
        {
            return;
        }
        _viewModel.DeleteTextOverlay(overlay.Id);
        TimelineEditor.SelectedTextOverlayId = null;
        UpdateTextOverlayPreview(_viewModel.Playhead);
    }

    private void TextOverlayEdit_Begin(object sender, RoutedEventArgs e)
    {
        _textPanelEditBefore = (TextOverlayList.SelectedItem as TextOverlay)?.Clone();
    }

    private void TextOverlayEdit_End(object sender, RoutedEventArgs e)
    {
        if (TextOverlayList.SelectedItem is TextOverlay overlay &&
            (_textPanelEditBefore is null || !TextOverlayPresentationEquals(_textPanelEditBefore, overlay)))
        {
            var id = overlay.Id;
            _viewModel.UpdateTextOverlay(overlay.Clone());
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == id);
        }
        _textPanelEditBefore = null;
        UpdateTextOverlayPreview(_viewModel.Playhead);
    }

    private void TextOverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextOverlayList.SelectedItem is TextOverlay overlay)
        {
            TimelineEditor.SelectedTextOverlayId = overlay.Id;
            TimelineEditor.SelectedClipId = null;
            _viewModel.SelectedClip = null;
            SeekTo(overlay.Start);
        }
    }

    private async void ImportSrt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импортировать субтитры",
            Filter = "Субтитры SubRip (*.srt)|*.srt|Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            var cues = AutoSubtitleService.ParseSrt(File.ReadAllText(dialog.FileName));
            if (cues.Count == 0)
            {
                throw new InvalidDataException("В SRT не найдено корректных реплик.");
            }
            var offset = _viewModel.Project.InPoint ?? 0;
            var snapshot = _viewModel.CaptureAutomationSnapshot();
            var overlays = cues
                .Select(cue => CreateSubtitleOverlay(offset + cue.Start, cue.End - cue.Start, cue.Text))
                .ToArray();
            var proposal = _viewModel.CreateSubtitleProposal(snapshot, overlays, "srt-import");
            var applied = await _viewModel.ApplyAutomationProposalAsync(proposal);
            if (!applied.Applied) throw new InvalidOperationException(applied.Message);
            _viewModel.StatusText = $"Импортировано субтитров: {cues.Count}";
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.LastOrDefault();
        }
        catch (Exception exception)
        {
            ShowError("Не удалось импортировать субтитры", exception);
        }
    }

    private async void AutoSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var availability = _viewModel.AutoSubtitleService.GetWhisperAvailability();
        if (!availability.IsReady)
        {
            ConfigureWhisper();
            return;
        }
        var selected = _viewModel.SelectedClip;
        var audioClip = selected?.Track == TrackKind.Audio
            ? selected
            : selected?.LinkGroupId is Guid groupId
                ? _viewModel.Project.Clips.FirstOrDefault(clip => clip.Track == TrackKind.Audio && clip.LinkGroupId == groupId)
                : null;
        if (audioClip is null)
        {
            MessageBox.Show(this, "Выберите аудиоклип или связанный видеоклип на таймлайне.", "Автосубтитры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var asset = _viewModel.Project.FindAsset(audioClip.AssetId);
        if (asset is null || !asset.HasAudio)
        {
            return;
        }

        _viewModel.IsBusy = true;
        _viewModel.StatusText = "Поиск встроенных субтитров или локального распознавания…";
        try
        {
            var automationSnapshot = _viewModel.CaptureAutomationSnapshot();
            var transcription = await _viewModel.AutomationOrchestrator.TranscribeAsync(
                asset,
                audioClip.SourceStart,
                audioClip.Duration);
            var cues = transcription.Cues;
            if (!_viewModel.IsAutomationSnapshotCurrent(automationSnapshot))
                throw new InvalidOperationException("Project changed while subtitles were generated. Run the operation again.");
            if (cues.Count == 0)
            {
                throw new InvalidOperationException(
                    "Субтитры не найдены. Добавьте русскую дорожку субтитров в файл либо положите whisper-cli.exe и модель ggml-*.bin в папку tools.");
            }
            var overlays = cues
                .Select(cue => CreateSubtitleOverlay(
                    audioClip.Start + cue.Start,
                    cue.End - cue.Start,
                    cue.Text))
                .ToArray();
            var proposal = _viewModel.CreateSubtitleProposal(automationSnapshot, overlays, transcription.Engine);
            var applied = await _viewModel.ApplyAutomationProposalAsync(proposal);
            if (!applied.Applied) throw new InvalidOperationException(applied.Message);
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.LastOrDefault();
            _viewModel.StatusText = $"Создано субтитров: {cues.Count} ({transcription.Engine})";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось создать автосубтитры", exception);
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private void ConfigureWhisper()
    {
        var executableDialog = new OpenFileDialog
        {
            Title = "Выберите whisper-cli.exe из whisper.cpp",
            Filter = "whisper.cpp (whisper*.exe)|whisper*.exe|Программы (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (executableDialog.ShowDialog(this) != true) return;
        var modelDialog = new OpenFileDialog
        {
            Title = "Выберите локальную модель whisper.cpp",
            Filter = "Модель whisper.cpp (ggml-*.bin)|ggml-*.bin|Модели (*.bin)|*.bin",
            CheckFileExists = true
        };
        if (modelDialog.ShowDialog(this) != true) return;
        try
        {
            WhisperConfiguration.Save(executableDialog.FileName, modelDialog.FileName);
            UpdateWhisperAvailability();
            _viewModel.StatusText = "Локальный whisper.cpp настроен";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось сохранить настройку whisper.cpp", exception);
        }
    }

    private void UpdateWhisperAvailability()
    {
        var availability = _viewModel.AutoSubtitleService.GetWhisperAvailability();
        AutoSubtitlesButton.Content = availability.IsReady ? "Автосубтитры" : "Настроить whisper.cpp";
        WhisperStatusText.Text = availability.Message;
        WhisperStatusText.Foreground = availability.IsReady
            ? new SolidColorBrush(Color.FromRgb(110, 231, 183))
            : FindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
    }

    private static TextOverlay CreateSubtitleOverlay(double start, double duration, string text) => new()
    {
        Start = start,
        Duration = Math.Max(0.5, duration),
        Text = text,
        IsSubtitle = true,
        FontFamily = "Segoe UI",
        FontSize = 42,
        X = 0.5,
        Y = 0.86,
        BoxWidth = 0.86,
        BoxHeight = 0.16,
        Color = "#FFFFFF"
    };

    private void UseSelectedClipRange_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedClip is null)
        {
            _viewModel.StatusText = "Сначала выберите клип на таймлайне";
            return;
        }

        AnalysisAssetComboBox.SelectedItem = _viewModel.SelectedClipAsset;
        SetAnalysisRangeFromClip(_viewModel.SelectedClip);
    }

    private void SetAnalysisRangeFromClip(TimelineClip clip)
    {
        AnalysisStartTextBox.Text = FormatEditorTime(clip.SourceStart);
        AnalysisEndTextBox.Text = FormatEditorTime(clip.SourceStart + clip.Duration);
    }

    private async void RunAnalysis_Click(object sender, RoutedEventArgs e)
    {
        if (AnalysisAssetComboBox.SelectedItem is not MediaAsset asset)
        {
            AnalysisSummaryTextBlock.Text = "Выберите видео для анализа.";
            return;
        }

        if (asset.Kind != MediaKind.Video)
        {
            AnalysisSummaryTextBlock.Text = "Анализ сцен доступен только для видеофайлов.";
            return;
        }

        var timelineClips = _viewModel.Project.GetVisualClips()
            .Where(clip => clip.AssetId == asset.Id)
            .ToList();
        if (timelineClips.Count == 0)
        {
            AnalysisSummaryTextBlock.Text = "Сначала добавьте выбранное видео на таймлайн.";
            return;
        }

        double sourceStart;
        double sourceEnd;
        if (_viewModel.Project.InPoint is double inPoint &&
            _viewModel.Project.OutPoint is double outPoint &&
            outPoint > inPoint &&
            timelineClips.FirstOrDefault(clip => clip.End > inPoint && clip.Start < outPoint) is { } rangeClip)
        {
            sourceStart = rangeClip.SourceStart + Math.Max(0, inPoint - rangeClip.Start);
            sourceEnd = rangeClip.SourceStart + Math.Min(rangeClip.Duration, outPoint - rangeClip.Start);
        }
        else if (!TryParseEditorTime(AnalysisStartTextBox.Text, out sourceStart) ||
                 !TryParseEditorTime(AnalysisEndTextBox.Text, out sourceEnd) ||
                 sourceEnd <= sourceStart)
        {
            sourceStart = 0;
            sourceEnd = asset.Duration;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        RunAnalysisButton.IsEnabled = false;
        CancelAnalysisButton.IsEnabled = true;
        AnalysisProgressBar.Visibility = Visibility.Visible;
        AnalysisProgressBar.Value = 0;
        var progress = new Progress<VideoAnalysisProgress>(value =>
        {
            AnalysisProgressBar.Value = value.Percent;
            AnalysisSummaryTextBlock.Text = value.Stage;
            _viewModel.StatusText = value.Stage;
        });

        try
        {
            var automationSnapshot = _viewModel.CaptureAutomationSnapshot();
            var query = AnalysisPromptTextBox.Text?.Trim() ?? string.Empty;
            var pipeline = await _viewModel.AutomationOrchestrator.AnalyzeAsync(
                new VideoAnalysisRequest(asset, sourceStart, sourceEnd, query),
                UseLocalAiCheckBox.IsChecked == true && LocalAiModelComboBox.SelectedItem is OllamaModelInfo selectedModel
                    ? selectedModel.Name
                    : null,
                progress,
                _analysisCancellation.Token);
            var result = pipeline.Result;
            string? localAiWarning = pipeline.Warning;
            if (!_viewModel.IsAutomationSnapshotCurrent(automationSnapshot))
                throw new InvalidOperationException("Project changed while video analysis was running. Run the analysis again.");
            var mappedMarkers = MapAnalysisMarkers(asset, timelineClips, result, query);
            if (mappedMarkers.Count == 0)
            {
                AnalysisSummaryTextBlock.Text = string.Join(" ",
                    new[] { result.Summary, localAiWarning, "Подходящие участки не попали в клипы на таймлайне." }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return;
            }

            var mappedStart = mappedMarkers.Min(marker => marker.Start);
            var mappedEnd = mappedMarkers.Max(marker => marker.End);
            var proposal = _viewModel.CreateAnalysisProposal(
                automationSnapshot, asset.Id, mappedStart, mappedEnd, mappedMarkers,
                UseLocalAiCheckBox.IsChecked == true ? "local-ai-analysis" : "technical-analysis");
            var applied = await _viewModel.ApplyAutomationProposalAsync(proposal, _analysisCancellation.Token);
            if (!applied.Applied) throw new InvalidOperationException(applied.Message);
            TimelineEditor.InvalidateVisual();
            AnalysisSummaryTextBlock.Text = string.Join(" ",
                new[] { result.Summary, localAiWarning }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (OperationCanceledException)
        {
            AnalysisSummaryTextBlock.Text = "Анализ отменён.";
            _viewModel.StatusText = "Анализ отменён";
        }
        catch (Exception exception)
        {
            AnalysisSummaryTextBlock.Text = exception.Message;
            ShowError("Не удалось выполнить анализ видео", exception);
        }
        finally
        {
            RunAnalysisButton.IsEnabled = true;
            CancelAnalysisButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshLocalAiModels_Click(object sender, RoutedEventArgs e)
        => await RefreshLocalAiModelsAsync(showError: true);

    private async Task RefreshLocalAiModelsAsync(bool showError)
    {
        if (_isRefreshingLocalAiModels)
        {
            return;
        }

        _isRefreshingLocalAiModels = true;
        RefreshLocalAiModelsButton.IsEnabled = false;
        LocalAiStatusTextBlock.Text = "Запуск отдельного Ollama на диске F:…";
        try
        {
            var selectedName = (LocalAiModelComboBox.SelectedItem as OllamaModelInfo)?.Name;
            var models = await _viewModel.OllamaVideoAnalysisService.GetModelsAsync();
            _localAiModels.Clear();
            foreach (var model in models)
            {
                _localAiModels.Add(model);
            }

            var preferred = _localAiModels.FirstOrDefault(model => model.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                ?? _localAiModels.FirstOrDefault(model => model.Name.Equals("qwen3-vl:4b-instruct", StringComparison.OrdinalIgnoreCase))
                ?? _localAiModels.FirstOrDefault(model => model.SupportsVision)
                ?? _localAiModels.FirstOrDefault();
            LocalAiModelComboBox.SelectedItem = preferred;
            UseLocalAiCheckBox.IsEnabled = preferred is not null;
            LocalAiModelComboBox.IsEnabled = preferred is not null;
            LocalAiStatusTextBlock.Text = preferred is null
                ? "В папке проекта нет локальных моделей."
                : $"Готово: {preferred.Name}. Модели: {_viewModel.OllamaVideoAnalysisService.ModelRoot}";
        }
        catch (Exception exception)
        {
            UseLocalAiCheckBox.IsEnabled = false;
            LocalAiModelComboBox.IsEnabled = false;
            LocalAiStatusTextBlock.Text = $"Локальный ИИ недоступен: {exception.Message}";
            if (showError)
            {
                ShowError("Не удалось подключить локальный ИИ", exception);
            }
        }
        finally
        {
            _isRefreshingLocalAiModels = false;
            RefreshLocalAiModelsButton.IsEnabled = true;
        }
    }

    private static List<TimelineMarker> MapAnalysisMarkers(
        MediaAsset asset,
        IReadOnlyList<TimelineClip> clips,
        VideoAnalysisResult result,
        string query)
    {
        var mapped = new List<TimelineMarker>();
        foreach (var range in result.Ranges)
        {
            var rangeEnd = range.SourceStart + range.Duration;
            foreach (var clip in clips)
            {
                var clipSourceEnd = clip.SourceStart + clip.Duration;
                var intersectionStart = Math.Max(range.SourceStart, clip.SourceStart);
                var intersectionEnd = Math.Min(rangeEnd, clipSourceEnd);
                if (intersectionEnd <= intersectionStart + 0.05)
                {
                    continue;
                }

                mapped.Add(new TimelineMarker
                {
                    AssetId = asset.Id,
                    Kind = range.Kind,
                    Start = clip.Start + intersectionStart - clip.SourceStart,
                    Duration = intersectionEnd - intersectionStart,
                    SourceStart = intersectionStart,
                    Title = range.Title,
                    Description = range.Description,
                    Confidence = range.Confidence,
                    Query = query
                });
            }
        }
        return mapped;
    }

    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) => _analysisCancellation?.Cancel();

    private async void ApplyEditPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasPendingEditReview)
        {
            AnalysisSummaryTextBlock.Text = "Сначала примите или верните уже применённый черновик ИИ.";
            return;
        }

        var prompt = AnalysisPromptTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AnalysisSummaryTextBlock.Text = "Напишите команду, например: «удали опенинг» или «разрежь в 2:15».";
            return;
        }

        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        ApplyEditPromptButton.IsEnabled = false;
        RunAnalysisButton.IsEnabled = false;
        CancelAnalysisButton.IsEnabled = true;
        AnalysisProgressBar.IsIndeterminate = true;
        AnalysisProgressBar.Visibility = Visibility.Visible;
        AnalysisSummaryTextBlock.Text = "Локальный ИИ составляет безопасный план монтажа…";

        try
        {
            EditCommandPlan plan;
            if (EditingCommandPlanner.TryCreateDeterministic(
                    _viewModel.Project,
                    prompt,
                    _viewModel.SelectedClip,
                    out var deterministic))
            {
                plan = deterministic;
            }
            else
            {
                if (LocalAiModelComboBox.SelectedItem is not OllamaModelInfo model)
                {
                    throw new InvalidOperationException(
                        "Для свободной команды выберите локальную модель. Простые команды с точным временем работают и без ИИ.");
                }

                plan = await _viewModel.OllamaVideoAnalysisService.PlanEditsAsync(
                    _viewModel.Project,
                    prompt,
                    model.Name,
                    _viewModel.SelectedClip,
                    _analysisCancellation.Token);
            }

            var completed = _viewModel.BeginEditPlanReview(plan);
            if (completed == 0)
            {
                AnalysisSummaryTextBlock.Text = "Команда не затронула клипы на таймлайне.";
                return;
            }

            EditReviewSummaryTextBlock.Text = string.Join(
                Environment.NewLine,
                new[] { plan.Summary }.Concat(plan.Commands.Select(FormatEditCommand)));
            EditReviewPanel.Visibility = Visibility.Visible;
            ApplyEditPromptButton.IsEnabled = false;
            AnalysisSummaryTextBlock.Text =
                "Черновик применён. Проверьте результат в окне просмотра и на таймлайне, затем примите его или верните проект.";
            ResetPreviewState();
            TimelineEditor.InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
            AnalysisSummaryTextBlock.Text = "Подготовка черновика отменена.";
        }
        catch (Exception exception)
        {
            AnalysisSummaryTextBlock.Text = exception.Message;
            ShowError("Не удалось выполнить команду монтажа", exception);
        }
        finally
        {
            AnalysisProgressBar.IsIndeterminate = false;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
            CancelAnalysisButton.IsEnabled = false;
            RunAnalysisButton.IsEnabled = !_viewModel.HasPendingEditReview;
            ApplyEditPromptButton.IsEnabled = !_viewModel.HasPendingEditReview;
        }
    }

    private async void AcceptEditReview_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.AcceptEditPlanReviewAsync();
        EditReviewPanel.Visibility = Visibility.Collapsed;
        ApplyEditPromptButton.IsEnabled = true;
        RunAnalysisButton.IsEnabled = true;
        AnalysisSummaryTextBlock.Text = "Изменения ИИ приняты. Их всё ещё можно отменить общей кнопкой Undo.";
        ResetPreviewState();
        TimelineEditor.InvalidateVisual();
    }

    private void RejectEditReview_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RejectEditPlanReview();
        EditReviewPanel.Visibility = Visibility.Collapsed;
        ApplyEditPromptButton.IsEnabled = true;
        RunAnalysisButton.IsEnabled = true;
        AnalysisSummaryTextBlock.Text = "Черновик отменён. Проект полностью возвращён к состоянию до запроса.";
        ResetPreviewState();
        TimelineEditor.InvalidateVisual();
    }

    private static string FormatEditCommand(EditCommand command) => command.Type switch
    {
        EditCommandType.DeleteRange => $"• удалить {FormatEditorTime(command.Start)}–{FormatEditorTime(command.End)} — {command.Reason}",
        EditCommandType.SplitAt => $"• разрезать в {FormatEditorTime(command.Start)} — {command.Reason}",
        EditCommandType.DeleteSelected => $"• удалить выбранный клип — {command.Reason}",
        _ => $"• {command.Reason}"
    };

    private void ClearAnalysisMarkers_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearAnalysisMarkers();
        TimelineEditor.InvalidateVisual();
        AnalysisSummaryTextBlock.Text = "Метки анализа удалены.";
    }

    private void AnalysisMarkersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AnalysisMarkersList.SelectedItem is TimelineMarker marker)
        {
            SeekTo(marker.Start);
        }
    }

    private void TimelineEditor_ClipSelected(object? sender, ClipSelectedEventArgs e)
    {
        _viewModel.SelectedClip = e.ClipId is { } id ? _viewModel.Project.FindClip(id) : null;
        if (e.ClipId.HasValue)
        {
            TextOverlayList.SelectedItem = null;
            TimelineEditor.SelectedTextOverlayId = null;
        }
        _audioWorkspaceWindow?.FollowSelection();
        _colorWorkspaceWindow?.FollowSelection();
    }

    private void TimelineEditor_TextOverlaySelected(object? sender, TextOverlaySelectedEventArgs e)
    {
        var overlay = e.OverlayId is { } id
            ? _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == id)
            : null;
        TextOverlayList.SelectedItem = overlay;
        if (overlay is not null)
        {
            _viewModel.SelectedClip = null;
        }
    }

    private void TimelineEditor_TextOverlayEditRequested(object? sender, TextOverlaySelectedEventArgs e)
    {
        var overlay = e.OverlayId is { } id
            ? _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == id)
            : null;
        if (overlay is null)
        {
            return;
        }
        SetLeftPanel(showAnalysis: false, showHistory: false, showText: true);
        TextOverlayList.SelectedItem = overlay;
        if (_viewModel.Playhead < overlay.Start || _viewModel.Playhead >= overlay.End)
        {
            SeekTo(overlay.Start);
        }
        BeginPreviewTextEditing(overlay);
    }

    private void TimelineEditor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_viewModel.SelectedClip is null && TimelineEditor.SelectedTextOverlayId is null)
        {
            e.Handled = true;
        }
    }

    private void TimelineEditor_PlayheadChanged(object? sender, PlayheadChangedEventArgs e) => SeekTo(e.Seconds);

    private void TimelineEditor_EditRequested(object? sender, TimelineEditRequestedEventArgs e)
    {
        try
        {
            if (!_viewModel.ApplyTimelineEdit(e.Intent)) return;
            _viewModel.Playhead = Math.Min(_viewModel.Playhead, _viewModel.Project.Duration);
            TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
            UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        }
        catch (Exception exception)
        {
            ShowError("Не удалось изменить таймлайн", exception);
        }
    }

    private void TimelineEditor_AssetDropped(object? sender, AssetDroppedEventArgs e)
    {
        var asset = _viewModel.Project.FindAsset(e.AssetId);
        if (asset is null)
        {
            return;
        }

        if ((asset.Kind == MediaKind.Audio && e.RequestedTrack == TrackKind.Visual) ||
            (asset.Kind != MediaKind.Audio && e.RequestedTrack == TrackKind.Audio))
        {
            _viewModel.StatusText = asset.Kind == MediaKind.Audio
                ? "Аудиофайл добавлен на аудиодорожку"
                : "Видео или изображение добавлено на видеодорожку";
        }

        _viewModel.AddAssetToTimeline(e.AssetId, e.RequestedStart, e.RequestedTrack, e.RequestedTrackIndex);
        TimelineEditor.SelectedClipId = _viewModel.SelectedClip?.Id;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _mediaDragOrigin = e.GetPosition(this);

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || MediaList.SelectedItem is not MediaAsset asset)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _mediaDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _mediaDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(TimelineControl.MediaAssetDataFormat, asset.Id.ToString());
        DragDrop.DoDragDrop(MediaList, data, DragDropEffects.Copy);
    }

    private void MediaList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaAsset asset)
        {
            return;
        }

        _viewModel.AddAssetToTimeline(asset.Id);
        TimelineEditor.SelectedClipId = _viewModel.SelectedClip?.Id;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void InspectorEdit_Begin(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedClip is not null)
        {
            _viewModel.BeginEdit();
        }
    }

    private void InspectorEdit_End(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _viewModel.NormalizeSelectedClip();
            _viewModel.CommitEdit("Свойства клипа изменены");
            TimelineEditor.InvalidateVisual();
            UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        }, DispatcherPriority.Background);
    }

    private void InspectorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _viewModel.SelectedClip is null)
        {
            return;
        }

        _viewModel.CommitEdit("Звук клипа изменён");
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Delta > 0)
            {
                ZoomIn_Click(sender, e);
            }
            else
            {
                ZoomOut_Click(sender, e);
            }
            e.Handled = true;
            return;
        }

        if (TimelineScrollViewer.ScrollableHeight > 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            TimelineScrollViewer.ScrollToVerticalOffset(TimelineScrollViewer.VerticalOffset - e.Delta);
        }
        else
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset - e.Delta);
        }
        e.Handled = true;
    }

    private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        TimelineEditor.HorizontalViewportOffset = e.HorizontalOffset;
        TimelineEditor.HorizontalViewportWidth = TimelineScrollViewer.ViewportWidth;
        TimelineEditor.VerticalViewportOffset = e.VerticalOffset;
        TimelineEditor.VerticalViewportHeight = TimelineScrollViewer.ViewportHeight;
        TimelineEditor.InvalidateVisual();
    }

    private void StartPlayback()
    {
        if (_viewModel.Project.Duration <= 0)
        {
            return;
        }

        if (_viewModel.Playhead >= _viewModel.Project.Duration - 0.02)
        {
            SeekTo(0);
        }

        _isPlaying = true;
        _playbackStartSeconds = _viewModel.Playhead;
        _playbackClock.Restart();
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        _playbackTimer.Start();
        PlayPauseButton.Content = "\uE769";
    }

    private void PausePlayback()
    {
        if (!_isPlaying)
        {
            return;
        }

        _isPlaying = false;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        UpdateAudioMeters(null);
        PlayPauseButton.Content = "\uE768";
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void StopPlayback()
    {
        PausePlayback();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        var next = _previewPresenter.State == KadrStudio.Application.Preview.PreviewState.Playing
            ? _previewPresenter.Position.TotalSeconds
            : _playbackStartSeconds + _playbackClock.Elapsed.TotalSeconds;
        if (next >= _viewModel.Project.Duration)
        {
            SeekTo(_viewModel.Project.Duration);
            PausePlayback();
            return;
        }

        _viewModel.Playhead = next;
        TimelineEditor.PlayheadSeconds = next;
        UpdatePreviewAt(next, forceSeek: false);
        KeepPlayheadVisible(next);
    }

    private void SeekTo(double seconds)
    {
        var bounded = Math.Clamp(seconds, 0, Math.Max(0, _viewModel.Project.TimelineDisplayDuration));
        _viewModel.Playhead = bounded;
        TimelineEditor.PlayheadSeconds = bounded;
        UpdatePreviewAt(bounded, forceSeek: true);
        if (_isPlaying)
        {
            _playbackStartSeconds = bounded;
            _playbackClock.Restart();
        }
        KeepPlayheadVisible(bounded);
    }

    private void UpdatePreviewAt(double timelineSeconds, bool forceSeek)
    {
        if (forceSeek || _previewPresenter.State is KadrStudio.Application.Preview.PreviewState.Idle or
            KadrStudio.Application.Preview.PreviewState.Paused or KadrStudio.Application.Preview.PreviewState.Failed)
            _ = UpdatePreviewEngineAsync(timelineSeconds, forceSeek);
        UpdateTextOverlayPreview(timelineSeconds);
    }

    private async Task UpdatePreviewEngineAsync(double timelineSeconds, bool forceSeek)
    {
        try
        {
            await _previewPresenter.UpdateAsync(timelineSeconds, forceSeek, _isPlaying);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _viewModel.StatusText = $"Предпросмотр недоступен: {exception.Message}";
        }
    }

    private void UpdateTextOverlayPreview(double timelineSeconds)
    {
        var active = _viewModel.Project.TextOverlays
            .Where(item => timelineSeconds >= item.Start && timelineSeconds < item.End)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .ToArray();
        var selectedId = (TextOverlayList.SelectedItem as TextOverlay)?.Id ?? TimelineEditor.SelectedTextOverlayId;
        var storedOverlay = active.LastOrDefault(item => item.Id == selectedId) ?? active.LastOrDefault();
        RenderPassiveTextOverlays(active.Where(item => item.Id != storedOverlay?.Id));
        var overlay = ResolvePreviewTextDraft(storedOverlay);
        if (overlay is null)
        {
            FinishPreviewTextEditing(commit: true, refresh: false);
            PreviewTextBorder.Visibility = Visibility.Collapsed;
            return;
        }

        PreviewTextBlock.Text = overlay.Text;
        PreviewTextBlock.FontFamily = new FontFamily(overlay.FontFamily);
        PreviewTextBlock.FontSize = overlay.FontSize;
        PreviewTextEditor.FontFamily = PreviewTextBlock.FontFamily;
        PreviewTextEditor.FontSize = overlay.FontSize;
        try
        {
            PreviewTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(overlay.Color));
        }
        catch
        {
            PreviewTextBlock.Foreground = Brushes.White;
        }
        PreviewTextEditor.Foreground = PreviewTextBlock.Foreground;
        PreviewTextBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        PreviewTextBorder.RenderTransform = new RotateTransform(overlay.Rotation);
        var width = Math.Clamp(overlay.BoxWidth * 960, 80, 960);
        var height = Math.Clamp(overlay.BoxHeight * 540, 36, 540);
        PreviewTextBorder.Width = width;
        PreviewTextBorder.Height = height;
        Canvas.SetLeft(PreviewTextBorder, Math.Clamp(overlay.X * 960 - width / 2, 0, 960 - width));
        Canvas.SetTop(PreviewTextBorder, Math.Clamp(overlay.Y * 540 - height / 2, 0, 540 - height));
        var selected = selectedId == overlay.Id;
        PreviewTextSelectionOutline.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        PreviewTextResizeHandles.Visibility = selected && !_isEditingPreviewText ? Visibility.Visible : Visibility.Collapsed;
        PreviewTextBorder.Visibility = Visibility.Visible;
    }

    private void PreviewTextBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = GetDisplayedTextOverlay();
        if (overlay is null)
        {
            return;
        }
        if (e.OriginalSource is TextBox)
        {
            return;
        }
        if (FindResizeHandle(e.OriginalSource as DependencyObject) is { } resizeHandle)
        {
            BeginPreviewTextResize(overlay, resizeHandle, e.GetPosition(PreviewTextCanvas));
            PreviewTextBorder.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ClickCount >= 2)
        {
            BeginPreviewTextEditing(overlay);
            e.Handled = true;
            return;
        }
        if (_isEditingPreviewText || _isResizingPreviewText)
        {
            return;
        }
        _previewDraggedOverlay = overlay.Clone();
        _previewTextDragOffset = e.GetPosition(PreviewTextBorder);
        _isDraggingPreviewText = true;
        TextOverlayList.SelectedItem = overlay;
        TimelineEditor.SelectedTextOverlayId = overlay.Id;
        PreviewTextBorder.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewTextBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isResizingPreviewText && _previewDraggedOverlay is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            ResizePreviewTextTo(e.GetPosition(PreviewTextCanvas));
            e.Handled = true;
            return;
        }
        if (!_isDraggingPreviewText || _previewDraggedOverlay is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var point = e.GetPosition(PreviewTextCanvas);
        var width = Math.Max(1, PreviewTextBorder.ActualWidth);
        var height = Math.Max(1, PreviewTextBorder.ActualHeight);
        var left = Math.Clamp(point.X - _previewTextDragOffset.X, 0, 960 - width);
        var top = Math.Clamp(point.Y - _previewTextDragOffset.Y, 0, 540 - height);
        _previewDraggedOverlay.X = Math.Clamp((left + width / 2) / 960, 0, 1);
        _previewDraggedOverlay.Y = Math.Clamp((top + height / 2) / 540, 0, 1);
        UpdateTextOverlayPreview(_viewModel.Playhead);
        e.Handled = true;
    }

    private void PreviewTextBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizingPreviewText)
        {
            CompletePreviewTextResize();
            PreviewTextBorder.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (!_isDraggingPreviewText)
        {
            return;
        }
        var draft = _previewDraggedOverlay?.Clone();
        _isDraggingPreviewText = false;
        _previewDraggedOverlay = null;
        PreviewTextBorder.ReleaseMouseCapture();
        if (draft is not null)
        {
            _viewModel.UpdateTextOverlay(draft, "Положение текста изменено");
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == draft.Id);
        }
        TimelineEditor.InvalidateVisual();
        e.Handled = true;
    }

    private void BeginPreviewTextEditing(TextOverlay overlay)
    {
        if (_isEditingPreviewText && _previewEditedOverlay?.Id == overlay.Id)
        {
            PreviewTextEditor.Focus();
            return;
        }
        FinishPreviewTextEditing(commit: true, refresh: false);
        StopPlayback();
        _previewEditedOverlay = overlay.Clone();
        _previewTextBeforeEdit = overlay.Text;
        _isEditingPreviewText = true;
        TextOverlayList.SelectedItem = overlay;
        TimelineEditor.SelectedTextOverlayId = overlay.Id;
        PreviewTextEditor.Text = overlay.Text;
        PreviewTextBlock.Visibility = Visibility.Collapsed;
        PreviewTextEditor.Visibility = Visibility.Visible;
        PreviewTextSelectionOutline.Visibility = Visibility.Visible;
        PreviewTextResizeHandles.Visibility = Visibility.Collapsed;
        PreviewTextEditor.Focus();
        PreviewTextEditor.CaretIndex = PreviewTextEditor.Text.Length;
    }

    private void FinishPreviewTextEditing(bool commit, bool refresh = true)
    {
        if (!_isEditingPreviewText)
        {
            return;
        }
        var overlay = _previewEditedOverlay;
        _isEditingPreviewText = false;
        _previewEditedOverlay = null;
        PreviewTextEditor.Visibility = Visibility.Collapsed;
        PreviewTextBlock.Visibility = Visibility.Visible;
        if (commit && overlay is not null && overlay.Text != _previewTextBeforeEdit)
        {
            _viewModel.UpdateTextOverlay(overlay, "Текст изменён");
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == overlay.Id);
        }
        TimelineEditor.InvalidateVisual();
        if (refresh)
        {
            UpdateTextOverlayPreview(_viewModel.Playhead);
        }
    }

    private void PreviewTextEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isEditingPreviewText || _previewEditedOverlay is null)
        {
            return;
        }
        _previewEditedOverlay.Text = PreviewTextEditor.Text;
        PreviewTextBlock.Text = PreviewTextEditor.Text;
        TimelineEditor.InvalidateVisual();
    }

    private void PreviewTextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FinishPreviewTextEditing(commit: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FinishPreviewTextEditing(commit: true);
            e.Handled = true;
        }
    }

    private void PreviewTextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isEditingPreviewText && !PreviewTextEditor.IsKeyboardFocusWithin)
        {
            FinishPreviewTextEditing(commit: true);
        }
    }

    private TextOverlay? GetDisplayedTextOverlay()
    {
        var selectedId = (TextOverlayList.SelectedItem as TextOverlay)?.Id ?? TimelineEditor.SelectedTextOverlayId;
        var active = _viewModel.Project.TextOverlays
            .Where(item => _viewModel.Playhead >= item.Start && _viewModel.Playhead < item.End)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Id)
            .ToArray();
        return active.LastOrDefault(item => item.Id == selectedId) ?? active.LastOrDefault();
    }

    private TextOverlay? ResolvePreviewTextDraft(TextOverlay? stored)
    {
        if (stored is null) return null;
        if (_previewEditedOverlay?.Id == stored.Id) return _previewEditedOverlay;
        if (_previewDraggedOverlay?.Id == stored.Id) return _previewDraggedOverlay;
        return stored;
    }

    private void RenderPassiveTextOverlays(IEnumerable<TextOverlay> overlays)
    {
        PreviewPassiveTextLayer.Children.Clear();
        foreach (var overlay in overlays)
        {
            var width = Math.Clamp(overlay.BoxWidth * 960, 80, 960);
            var height = Math.Clamp(overlay.BoxHeight * 540, 36, 540);
            var text = new TextBlock
            {
                Text = overlay.Text,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily(overlay.FontFamily),
                FontSize = overlay.FontSize,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(8),
                Foreground = ParseTextBrush(overlay.Color)
            };
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(102, 0, 0, 0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(overlay.Rotation),
                Child = text
            };
            Canvas.SetLeft(border, Math.Clamp(overlay.X * 960 - width / 2, 0, 960 - width));
            Canvas.SetTop(border, Math.Clamp(overlay.Y * 540 - height / 2, 0, 540 - height));
            PreviewPassiveTextLayer.Children.Add(border);
        }
    }

    private static Brush ParseTextBrush(string color)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        catch (FormatException) { return Brushes.White; }
        catch (NotSupportedException) { return Brushes.White; }
    }

    private static bool TextOverlayPresentationEquals(TextOverlay left, TextOverlay right)
        => left.Id == right.Id && left.Text == right.Text && left.FontFamily == right.FontFamily &&
           left.Color == right.Color && left.IsSubtitle == right.IsSubtitle &&
           Math.Abs(left.Start - right.Start) < 0.0001 &&
           Math.Abs(left.Duration - right.Duration) < 0.0001 &&
           Math.Abs(left.FontSize - right.FontSize) < 0.0001 &&
           Math.Abs(left.X - right.X) < 0.0001 && Math.Abs(left.Y - right.Y) < 0.0001 &&
           Math.Abs(left.Rotation - right.Rotation) < 0.0001 &&
           Math.Abs(left.BoxWidth - right.BoxWidth) < 0.0001 &&
           Math.Abs(left.BoxHeight - right.BoxHeight) < 0.0001;

    private static FrameworkElement? FindResizeHandle(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.Tag is string handle &&
                handle is "Left" or "Right" or "Top" or "Bottom" or "TopLeft" or "TopRight" or "BottomLeft" or "BottomRight")
            {
                return element;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void BeginPreviewTextResize(TextOverlay overlay, FrameworkElement handle, Point startPoint)
    {
        FinishPreviewTextEditing(commit: true, refresh: false);
        _isResizingPreviewText = true;
        _previewDraggedOverlay = overlay.Clone();
        _previewTextResizeHandle = handle.Tag as string ?? "BottomRight";
        _previewTextResizeStartPoint = startPoint;
        _previewTextResizeStartBounds = new Rect(
            Canvas.GetLeft(PreviewTextBorder),
            Canvas.GetTop(PreviewTextBorder),
            PreviewTextBorder.Width,
            PreviewTextBorder.Height);
    }

    private void ResizePreviewTextTo(Point point)
    {
        if (!_isResizingPreviewText || _previewDraggedOverlay is null)
        {
            return;
        }
        const double minWidth = 80;
        const double minHeight = 36;
        var deltaX = point.X - _previewTextResizeStartPoint.X;
        var deltaY = point.Y - _previewTextResizeStartPoint.Y;
        var width = _previewTextResizeStartBounds.Width;
        var height = _previewTextResizeStartBounds.Height;
        var left = _previewTextResizeStartBounds.Left;
        var top = _previewTextResizeStartBounds.Top;

        var resizeLeft = _previewTextResizeHandle is "Left" or "TopLeft" or "BottomLeft";
        var resizeRight = _previewTextResizeHandle is "Right" or "TopRight" or "BottomRight";
        var resizeTop = _previewTextResizeHandle is "Top" or "TopLeft" or "TopRight";
        var resizeBottom = _previewTextResizeHandle is "Bottom" or "BottomLeft" or "BottomRight";

        if (resizeLeft)
        {
            var nextLeft = Math.Clamp(left + deltaX, 0, left + width - minWidth);
            width += left - nextLeft;
            left = nextLeft;
        }
        else if (resizeRight)
        {
            width = Math.Clamp(width + deltaX, minWidth, 960 - left);
        }

        if (resizeTop)
        {
            var nextTop = Math.Clamp(top + deltaY, 0, top + height - minHeight);
            height += top - nextTop;
            top = nextTop;
        }
        else if (resizeBottom)
        {
            height = Math.Clamp(height + deltaY, minHeight, 540 - top);
        }

        _previewDraggedOverlay.BoxWidth = width / 960;
        _previewDraggedOverlay.BoxHeight = height / 540;
        _previewDraggedOverlay.X = (left + width / 2) / 960;
        _previewDraggedOverlay.Y = (top + height / 2) / 540;
        UpdateTextOverlayPreview(_viewModel.Playhead);
    }

    private void CompletePreviewTextResize()
    {
        if (!_isResizingPreviewText)
        {
            return;
        }
        var draft = _previewDraggedOverlay?.Clone();
        _isResizingPreviewText = false;
        _previewDraggedOverlay = null;
        if (draft is not null)
        {
            _viewModel.UpdateTextOverlay(draft, "Размер текстового блока изменён");
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == draft.Id);
        }
        TimelineEditor.InvalidateVisual();
    }

    private TimelineClip? FindActiveClip(TrackKind track, double time)
    {
        var clips = _viewModel.Project.Clips
            .Where(clip => clip.Track == track)
            .OrderByDescending(clip => clip.TrackIndex)
            .ThenBy(clip => clip.Start)
            .ToList();
        var active = clips.FirstOrDefault(clip => time >= clip.Start && time < clip.End);
        if (active is null && clips.Count > 0 && Math.Abs(time - _viewModel.Project.Duration) < 0.02)
        {
            active = clips.LastOrDefault(clip => time >= clip.Start && time <= clip.End + 0.02);
        }
        return active;
    }


    private void PreviewQuality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _useHalfQualityPreview = (PreviewQualityComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Original";
        _previewPresenter.SetProject(_viewModel.Project, _useHalfQualityPreview);
        _ = _previewPresenter.InvalidateAsync(video: true, audio: false, overlay: false);
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        _viewModel.StatusText = _useHalfQualityPreview
            ? "Предпросмотр: 1/2 качества"
            : "Предпросмотр: оригинальное качество";
    }

    private void PreviewZoomIn_Click(object sender, RoutedEventArgs e) => SetPreviewScale(_previewScale + 0.25);

    private void PreviewZoomOut_Click(object sender, RoutedEventArgs e) => SetPreviewScale(_previewScale - 0.25);

    private void SetPreviewScale(double scale)
    {
        _previewScale = Math.Clamp(scale, 0.5, 2.5);
        PreviewScaleTransform.ScaleX = _previewScale;
        PreviewScaleTransform.ScaleY = _previewScale;
        PreviewZoomLabel.Text = $"{_previewScale:P0}";
    }

    private void UpdateAudioMeters(TimelineClip? clip)
    {
        if (!_isPlaying || clip is null || clip.IsMuted)
        {
            AudioLeftMeter.Value = 0;
            AudioRightMeter.Value = 0;
            return;
        }

        // Values are updated from the exact mixed PCM emitted by Kadr.MediaHost.
    }

    private void KeepPlayheadVisible(double seconds)
    {
        var x = TimelineControl.LeftGutterWidth + seconds * TimelineEditor.PixelsPerSecond;
        var left = TimelineScrollViewer.HorizontalOffset;
        var right = left + TimelineScrollViewer.ViewportWidth;
        if (x > right - 45)
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(x - TimelineScrollViewer.ViewportWidth + 80);
        }
        else if (x < left + TimelineControl.LeftGutterWidth)
        {
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x - 120));
        }
    }

    private void ResetPreviewState()
    {
        _previewPresenter.SetProject(_viewModel.Project, _useHalfQualityPreview);
        TimelineEditor.Project = _viewModel.Project;
        TimelineEditor.SelectedClipId = _viewModel.SelectedClip?.Id;
        TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Project):
                ResetPreviewState();
                RefreshTransitionList();
                break;
            case nameof(MainViewModel.TimelinePresentationRevision):
                TimelineEditor.Project = _viewModel.Project;
                break;
            case nameof(MainViewModel.SelectedClip):
                TimelineEditor.SelectedClipId = _viewModel.SelectedClip?.Id;
                break;
            case nameof(MainViewModel.Playhead):
                TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
                break;
            case nameof(MainViewModel.ProjectTitle):
                UpdateWindowTitle();
                break;
        }
    }

    private void UpdateWindowTitle() => Title = $"{_viewModel.ProjectTitle} — Kadr Studio";

    private sealed record TransitionListItem(Guid Id, string Label);

    private async Task<bool> ConfirmCanLoseChangesAsync()
    {
        if (!await ConfirmPendingEditReviewAsync())
        {
            return false;
        }

        if (!_viewModel.IsDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "Сохранить изменения в текущем проекте?",
            "Kadr Studio",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            return await SaveProjectInternalAsync(forceSaveAs: false);
        }

        if (result == MessageBoxResult.No)
        {
            await _viewModel.DiscardAutosaveAsync();
            return true;
        }

        return false;
    }

    private async Task<bool> ConfirmPendingEditReviewAsync()
    {
        if (!_viewModel.HasPendingEditReview)
        {
            return true;
        }

        var result = MessageBox.Show(
            this,
            "Сейчас применён непроверенный черновик ИИ.\n\nДа — принять изменения.\nНет — вернуть проект к исходному состоянию.\nОтмена — остаться в редакторе.",
            "Черновик ИИ",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.Yes)
        {
            await _viewModel.AcceptEditPlanReviewAsync();
        }
        else
        {
            _viewModel.RejectEditPlanReview();
        }
        EditReviewPanel.Visibility = Visibility.Collapsed;
        ApplyEditPromptButton.IsEnabled = true;
        RunAnalysisButton.IsEnabled = true;
        ResetPreviewState();
        return true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _analysisCancellation?.Cancel();
        if (_isShutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_isCloseConfirmationPending)
        {
            return;
        }

        _isCloseConfirmationPending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(ConfirmCloseAfterClosingEvent));
    }

    private async void ConfirmCloseAfterClosingEvent()
    {
        try
        {
            if (!_allowClose && !await ConfirmCanLoseChangesAsync())
            {
                return;
            }

            _allowClose = true;
            await ShutdownAsync();
            _isShutdownComplete = true;
            Close();
        }
        catch (Exception exception)
        {
            ShowError("Не удалось закрыть проект", exception);
        }
        finally
        {
            _isCloseConfirmationPending = false;
        }
    }

    private async Task ShutdownAsync()
    {
        StopPlayback();
        await _previewPresenter.DisposeAsync();
        await _viewModel.DisposeAsync();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            PlayPause_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None)
        {
            Split_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetInPoint_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.None)
        {
            SetOutPoint_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            DeleteClip_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            SeekTo(_viewModel.Playhead - (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / _viewModel.Project.FrameRateValue.FramesPerSecond));
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            SeekTo(_viewModel.Playhead + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / _viewModel.Project.FrameRateValue.FramesPerSecond));
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SaveProject_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ProjectHistory_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo_Click(sender, e);
            e.Handled = true;
        }
        else if ((e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) ||
                 (e.Key == Key.Z && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            Redo_Click(sender, e);
            e.Handled = true;
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Новый проект" : cleaned;
    }

    private static bool TryParseEditorTime(string? value, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace(',', '.');
        if (!normalized.Contains(':'))
        {
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds >= 0;
        }

        var parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var numbers = parts.Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        seconds = numbers.Length == 2
            ? numbers[0] * 60 + numbers[1]
            : numbers[0] * 3600 + numbers[1] * 60 + numbers[2];
        return seconds >= 0;
    }

    private static string FormatEditorTime(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return seconds >= 3600
            ? duration.ToString(@"h\:mm\:ss\.fff")
            : duration.ToString(@"m\:ss\.fff");
    }

    private void ShowError(string title, Exception exception)
        => MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
