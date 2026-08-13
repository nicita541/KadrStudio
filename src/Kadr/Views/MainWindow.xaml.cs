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
    private double _playbackStartSeconds;
    private PreviewPlaybackController? _previewPlayback;
    private TimelinePreviewSession? _previewSession;
    private readonly HashSet<string> _pendingPreviewRequests = [];
    private int _previewSessionGeneration;
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
    private CancellationTokenSource? _stillPreviewCancellation;
    private int _stillPreviewVersion;
    private bool _stillPreviewPending;
    private double _stillPreviewPendingPosition = -1;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialProjectPath)
    {
        _initialProjectPath = initialProjectPath;
        InitializeComponent();
        _viewModel = new MainViewModel();
        _previewSession = new TimelinePreviewSession(_viewModel.PreviewCompositionService);
        _previewPlayback = new PreviewPlaybackController(PreviewVideoView);
        _previewPlayback.VideoPresented += PreviewPlayback_VideoPresented;
        _previewPlayback.VideoEnded += PreviewPlayback_VideoEnded;
        _previewPlayback.VideoFailed += PreviewPlayback_VideoFailed;
        _previewPlayback.AudioFailed += PreviewPlayback_AudioFailed;
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
        TimelineEditor.EditStarted += TimelineEditor_EditStarted;
        TimelineEditor.EditCompleted += TimelineEditor_EditCompleted;
        TimelineEditor.AssetDropped += TimelineEditor_AssetDropped;
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

        if (_viewModel.HasAutosave)
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
                    _viewModel.DiscardAutosave();
                    ShowError("Не удалось восстановить проект", exception);
                }
            }
            else
            {
                _viewModel.DiscardAutosave();
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
        _viewModel.NewProject();
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

    private void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        SetLeftPanel(showAnalysis: false, showHistory: true, showText: false);
        RefreshInlineHistory();
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
            _viewModel.BeginEdit();
            var right = overlay.Clone();
            right.Id = Guid.NewGuid();
            right.Start = _viewModel.Playhead;
            right.Duration = overlay.End - _viewModel.Playhead;
            overlay.Duration = _viewModel.Playhead - overlay.Start;
            _viewModel.Project.TextOverlays.Add(right);
            _viewModel.CommitEdit("Текстовый клип разделён");
            TextOverlayList.SelectedItem = right;
            TimelineEditor.SelectedTextOverlayId = right.Id;
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
            _viewModel.BeginEdit();
            _viewModel.Project.TextOverlays.Remove(overlay);
            _viewModel.CommitEdit("Текст удалён");
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
        _viewModel.BeginEdit();
        _viewModel.Project.InPoint = _viewModel.Playhead;
        if (_viewModel.Project.OutPoint is double outPoint && outPoint <= _viewModel.Playhead)
        {
            _viewModel.Project.OutPoint = null;
        }
        _viewModel.CommitEdit($"Точка входа: {FormatEditorTime(_viewModel.Playhead)}");
        AnalysisStartTextBox.Text = FormatEditorTime(_viewModel.Playhead);
        TimelineEditor.InvalidateVisual();
    }

    private void SetOutPoint_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BeginEdit();
        _viewModel.Project.OutPoint = _viewModel.Playhead;
        if (_viewModel.Project.InPoint is double inPoint && inPoint >= _viewModel.Playhead)
        {
            _viewModel.Project.InPoint = null;
        }
        _viewModel.CommitEdit($"Точка выхода: {FormatEditorTime(_viewModel.Playhead)}");
        AnalysisEndTextBox.Text = FormatEditorTime(_viewModel.Playhead);
        TimelineEditor.InvalidateVisual();
    }

    private void ClearInOut_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Project.InPoint is null && _viewModel.Project.OutPoint is null)
        {
            return;
        }
        _viewModel.BeginEdit();
        _viewModel.Project.InPoint = null;
        _viewModel.Project.OutPoint = null;
        _viewModel.CommitEdit("Точки In/Out очищены");
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
        => SeekTo(_viewModel.Playhead - 1.0 / Math.Max(1, _viewModel.Project.FrameRate));

    private void NextFrame_Click(object sender, RoutedEventArgs e)
        => SeekTo(_viewModel.Playhead + 1.0 / Math.Max(1, _viewModel.Project.FrameRate));

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
        => SetLeftPanel(showAnalysis: false, showHistory: false, showText: true);

    private void SetLeftPanel(bool showAnalysis, bool showHistory, bool showText)
    {
        MediaPanel.Visibility = showAnalysis || showHistory || showText ? Visibility.Collapsed : Visibility.Visible;
        AnalysisPanel.Visibility = showAnalysis ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
        MediaNavButton.Tag = showAnalysis || showHistory || showText ? null : "Selected";
        AnalysisNavButton.Tag = showAnalysis ? "Selected" : null;
        HistoryNavButton.Tag = showHistory ? "Selected" : null;
        TextNavButton.Tag = showText ? "Selected" : null;
    }

    private void RefreshInlineHistory(Guid? selectedId = null)
    {
        _inlineHistoryEntries.Clear();
        foreach (var entry in _viewModel.GetHistoryCheckpoints())
        {
            _inlineHistoryEntries.Add(entry);
        }
        InlineHistoryList.ItemsSource = _inlineHistoryEntries;
        InlineHistoryEmptyText.Visibility = _inlineHistoryEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InlineHistoryList.SelectedItem = selectedId is Guid id
            ? _inlineHistoryEntries.FirstOrDefault(entry => entry.Id == id)
            : _inlineHistoryEntries.FirstOrDefault();
    }

    private void CreateInlineCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entry = _viewModel.CreateHistoryCheckpoint(InlineHistoryMessageTextBox.Text);
            RefreshInlineHistory(entry.Id);
            InlineHistoryMessageTextBox.SelectAll();
        }
        catch (Exception exception)
        {
            ShowError("Не удалось создать контрольную точку", exception);
        }
    }

    private void RestoreInlineCheckpoint_Click(object sender, RoutedEventArgs e) => RestoreInlineCheckpoint();

    private void InlineHistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RestoreInlineCheckpoint();

    private void RestoreInlineCheckpoint()
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
            _viewModel.RestoreHistoryCheckpoint(entry);
            ResetPreviewState();
            RefreshInlineHistory();
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
        _viewModel.BeginEdit();
        _viewModel.Project.TextOverlays.Add(overlay);
        _viewModel.CommitEdit("Текст добавлен");
        TextOverlayList.SelectedItem = overlay;
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
        _viewModel.BeginEdit();
        _viewModel.Project.TextOverlays.Remove(overlay);
        _viewModel.CommitEdit("Текст удалён");
        TimelineEditor.SelectedTextOverlayId = null;
        UpdateTextOverlayPreview(_viewModel.Playhead);
    }

    private void TextOverlayEdit_Begin(object sender, RoutedEventArgs e)
    {
        if (TextOverlayList.SelectedItem is not null) _viewModel.BeginEdit();
    }

    private void TextOverlayEdit_End(object sender, RoutedEventArgs e)
    {
        _viewModel.CommitEdit("Текст изменён");
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

    private void ImportSrt_Click(object sender, RoutedEventArgs e)
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
            _viewModel.CreateHistoryCheckpoint("Авто: перед импортом субтитров");
            _viewModel.BeginEdit();
            foreach (var cue in cues)
            {
                _viewModel.Project.TextOverlays.Add(CreateSubtitleOverlay(offset + cue.Start, cue.End - cue.Start, cue.Text));
            }
            _viewModel.CommitEdit($"Импортировано субтитров: {cues.Count}");
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.LastOrDefault();
        }
        catch (Exception exception)
        {
            ShowError("Не удалось импортировать субтитры", exception);
        }
    }

    private async void AutoSubtitles_Click(object sender, RoutedEventArgs e)
    {
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
            _viewModel.CreateHistoryCheckpoint("Авто: перед созданием автосубтитров");
            _viewModel.BeginEdit();
            foreach (var cue in cues)
            {
                _viewModel.Project.TextOverlays.Add(CreateSubtitleOverlay(
                    audioClip.Start + cue.Start,
                    cue.End - cue.Start,
                    cue.Text));
            }
            _viewModel.CommitEdit($"Создано автосубтитров: {cues.Count}");
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
#if false
            string? legacyLocalAiWarning = null;
            if (false && UseLocalAiCheckBox.IsChecked == true && LocalAiModelComboBox.SelectedItem is OllamaModelInfo model)
            {
                try
                {
                    var enhancement = await _viewModel.OllamaVideoAnalysisService.EnhanceAsync(
                        asset,
                        result,
                        query,
                        model.Name,
                        progress,
                        _analysisCancellation.Token);
                    result = MergeLocalAiEnhancement(result, enhancement);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    localAiWarning = $"Локальный ИИ пропущен: {exception.Message}";
                }
            }

#endif
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
            _viewModel.CreateHistoryCheckpoint("Авто: перед AI-анализом видео");
            _viewModel.ReplaceAnalysisMarkers(asset.Id, mappedStart, mappedEnd, mappedMarkers);
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

    private static VideoAnalysisResult MergeLocalAiEnhancement(
        VideoAnalysisResult baseline,
        OllamaAnalysisEnhancement enhancement)
    {
        var refinedKinds = enhancement.Ranges.Select(range => range.Kind).ToHashSet();
        var ranges = baseline.Ranges
            .Where(range => !refinedKinds.Contains(range.Kind))
            .Concat(enhancement.Ranges)
            .OrderBy(range => range.SourceStart)
            .ThenBy(range => range.Kind)
            .ToList();
        var detail = enhancement.UsedVision ? "с просмотром кадров" : "по технической сводке";
        var summary = string.IsNullOrWhiteSpace(enhancement.Summary)
            ? $"{baseline.Summary} Локальный ИИ {enhancement.Model} выполнен {detail}."
            : $"{baseline.Summary} Локальный ИИ {enhancement.Model} ({detail}): {enhancement.Summary}";
        return baseline with { Summary = summary, Ranges = ranges };
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

    private void AcceptEditReview_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AcceptEditPlanReview();
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

    private void TimelineEditor_EditStarted(object? sender, TimelineEditEventArgs e) => _viewModel.BeginEdit();

    private void TimelineEditor_EditCompleted(object? sender, TimelineEditEventArgs e)
    {
        var editingText = TimelineEditor.SelectedTextOverlayId.HasValue;
        if (!editingText)
        {
            _viewModel.NormalizeSelectedClip();
        }
        _viewModel.CommitEdit(e.Changed ? (editingText ? "Текстовый клип изменён" : "Клип изменён") : "Готово");
        _viewModel.Playhead = Math.Min(_viewModel.Playhead, _viewModel.Project.Duration);
        TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
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
        _previewPlayback?.SetTimelinePosition(_viewModel.Playhead);
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
        _previewPlayback?.SetPlaying(true);
        _playbackTimer.Start();
        PlayPauseButton.Content = "Ⅱ";
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
        _previewPlayback?.SetPlaying(false);
        UpdateAudioMeters(null);
        PlayPauseButton.Content = "▶";
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void StopPlayback()
    {
        PausePlayback();
        _previewPlayback?.SetPlaying(false);
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        var next = _playbackStartSeconds + _playbackClock.Elapsed.TotalSeconds;
        if (next >= _viewModel.Project.Duration)
        {
            SeekTo(_viewModel.Project.Duration);
            PausePlayback();
            return;
        }

        _viewModel.Playhead = next;
        TimelineEditor.PlayheadSeconds = next;
        UpdatePreviewAt(next, forceSeek: false);
        UpdateAudioMeters(FindActiveClip(TrackKind.Audio, next));
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
            _previewPlayback?.SetTimelinePosition(bounded);
            _previewPlayback?.SetPlaying(true);
        }
        KeepPlayheadVisible(bounded);
    }

    private void UpdatePreviewAt(double timelineSeconds, bool forceSeek)
    {
        _previewPlayback?.SetTimelinePosition(timelineSeconds);
        UpdateCompositedVideoPreview(timelineSeconds, forceSeek);
        UpdateMixedAudioPreview(timelineSeconds, forceSeek);
        UpdateTextOverlayPreview(timelineSeconds);
    }

    private async Task EnsureVideoPreviewAsync(double timelinePosition)
    {
        if (_previewSession is null) return;
        var generation = _previewSessionGeneration;
        var bucket = Math.Floor(Math.Max(0, timelinePosition) / PreviewCompositionService.SegmentStep);
        var requestKey = $"v:{bucket:0}";
        if (!_pendingPreviewRequests.Add(requestKey)) return;
        try
        {
            var segment = await _previewSession.EnsureVideoAsync(
                _viewModel.Project, timelinePosition, _useHalfQualityPreview);
            if (generation != _previewSessionGeneration) return;
            if (_isPlaying && segment.Contains(_viewModel.Playhead))
                ActivateVideoSegment(segment, _viewModel.Playhead, forceSeek: true);
            else if (segment.TimelineStart > _viewModel.Playhead &&
                     segment.TimelineStart - _viewModel.Playhead <= PreviewCompositionService.SegmentOverlap + 1)
                ActivateVideoSegment(segment, _viewModel.Playhead, forceSeek: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _viewModel.StatusText = $"Видеопредпросмотр недоступен: {exception.Message}";
        }
        finally
        {
            _pendingPreviewRequests.Remove(requestKey);
        }
    }

    private async Task EnsureAudioPreviewAsync(double timelinePosition)
    {
        if (_previewSession is null) return;
        var generation = _previewSessionGeneration;
        var bucket = Math.Floor(Math.Max(0, timelinePosition) / PreviewCompositionService.SegmentStep);
        var requestKey = $"a:{bucket:0}";
        if (!_pendingPreviewRequests.Add(requestKey)) return;
        try
        {
            var segment = await _previewSession.EnsureAudioAsync(_viewModel.Project, timelinePosition);
            if (generation != _previewSessionGeneration) return;
            if (segment.Contains(_viewModel.Playhead))
                ActivateAudioSegment(segment, _viewModel.Playhead, forceSeek: true);
            else if (segment.TimelineStart > _viewModel.Playhead &&
                     segment.TimelineStart - _viewModel.Playhead <= PreviewCompositionService.SegmentOverlap + 1)
                ActivateAudioSegment(segment, _viewModel.Playhead, forceSeek: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _viewModel.StatusText = $"Аудиопредпросмотр недоступен: {exception.Message}";
        }
        finally
        {
            _pendingPreviewRequests.Remove(requestKey);
        }
    }

    private void ActivateVideoSegment(TimelinePreviewSegment segment, double timelinePosition, bool forceSeek)
    {
        _previewPlayback?.UpdateVideo(segment, timelinePosition, forceSeek);
    }

    private void ActivateAudioSegment(TimelinePreviewSegment segment, double timelinePosition, bool forceSeek)
    {
        _previewPlayback?.UpdateAudio(segment, timelinePosition, forceSeek);
    }

    private void PreviewPlayback_VideoPresented(object? sender, EventArgs e)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Collapsed;
    }

    private void PreviewPlayback_VideoEnded(object? sender, EventArgs e)
        => _ = EnsureVideoPreviewAsync(_viewModel.Playhead);

    private async void PreviewPlayback_VideoFailed(object? sender, PreviewPlaybackFailedEventArgs e)
    {
        _previewSession?.InvalidateVideo(e.Segment?.Path);
        await UpdatePausedStillFrameAsync(_viewModel.Playhead, allowDuringPlayback: true);
        _viewModel.StatusText = DecoderRecoveryStatus(
            "Видеодекодер перезапущен без остановки звука", e.Error);
        await EnsureVideoPreviewAsync(_viewModel.Playhead);
    }

    private async void PreviewPlayback_AudioFailed(object? sender, PreviewPlaybackFailedEventArgs e)
    {
        _previewSession?.InvalidateAudio(e.Segment?.Path);
        _viewModel.StatusText = DecoderRecoveryStatus(
            "Аудиодекодер перезапущен независимо от видео", e.Error);
        await EnsureAudioPreviewAsync(_viewModel.Playhead);
    }

    private static string DecoderRecoveryStatus(string message, Exception? error)
    {
        var detail = error?.Message?.Trim();
        if (string.IsNullOrWhiteSpace(detail)) return message;
        return $"{message}: {detail}";
    }

    private void ClearVideoPlayers() => _previewPlayback?.ClearVideo();

    private void ClearAudioPlayers() => _previewPlayback?.ClearAudio();

    private void UpdateCompositedVideoPreview(double timelineSeconds, bool forceSeek)
    {
        if (!_viewModel.PreviewCompositionService.HasRenderableVideo(_viewModel.Project))
        {
            ClearVideoPlayers();
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Visible;
            return;
        }

        EmptyPreview.Visibility = Visibility.Collapsed;
        if (!_isPlaying)
        {
            _previewPlayback?.SetPlaying(false);
            if (forceSeek || PreviewImage.Source is null)
                _ = UpdatePausedStillFrameAsync(timelineSeconds);
            if (_previewSession?.TryGetVideo(_viewModel.Project, timelineSeconds, _useHalfQualityPreview) is null)
                _ = EnsureVideoPreviewAsync(timelineSeconds);
            return;
        }

        var segment = _previewSession?.TryGetVideo(_viewModel.Project, timelineSeconds, _useHalfQualityPreview);
        if (segment is null)
        {
            if (PreviewImage.Source is null)
                _ = UpdatePausedStillFrameAsync(timelineSeconds, allowDuringPlayback: true);
            _ = EnsureVideoPreviewAsync(timelineSeconds);
            return;
        }

        ActivateVideoSegment(segment, timelineSeconds, forceSeek);
        if (segment.TimelineEnd - timelineSeconds < PreviewCompositionService.SegmentOverlap + 1 &&
            segment.TimelineStart + PreviewCompositionService.SegmentStep < _viewModel.Project.Duration)
        {
            _ = EnsureVideoPreviewAsync(segment.TimelineStart + PreviewCompositionService.SegmentStep + 0.001);
        }
    }

    private void UpdateMixedAudioPreview(double timelineSeconds, bool forceSeek)
    {
        if (!_viewModel.PreviewCompositionService.HasRenderableAudio(_viewModel.Project))
        {
            ClearAudioPlayers();
            return;
        }

        var segment = _previewSession?.TryGetAudio(_viewModel.Project, timelineSeconds);
        if (segment is null)
        {
            _ = EnsureAudioPreviewAsync(timelineSeconds);
            return;
        }

        ActivateAudioSegment(segment, timelineSeconds, forceSeek);
        if (segment.TimelineEnd - timelineSeconds < PreviewCompositionService.SegmentOverlap + 1 &&
            segment.TimelineStart + PreviewCompositionService.SegmentStep < _viewModel.Project.Duration)
        {
            _ = EnsureAudioPreviewAsync(segment.TimelineStart + PreviewCompositionService.SegmentStep + 0.001);
        }
    }

    private async Task UpdatePausedStillFrameAsync(double timelinePosition, bool allowDuringPlayback = false)
    {
        if (_stillPreviewPending && allowDuringPlayback &&
            Math.Abs(timelinePosition - _stillPreviewPendingPosition) < 0.75)
        {
            return;
        }
        var version = ++_stillPreviewVersion;
        _stillPreviewPending = true;
        _stillPreviewPendingPosition = timelinePosition;
        _stillPreviewCancellation?.Cancel();
        _stillPreviewCancellation?.Dispose();
        _stillPreviewCancellation = new CancellationTokenSource();
        try
        {
            if (_previewSession is null) return;
            var still = await _previewSession.EnsureStillAsync(
                _viewModel.Project, timelinePosition, _useHalfQualityPreview, _stillPreviewCancellation.Token);
            if (version != _stillPreviewVersion || (_isPlaying && !allowDuringPlayback) ||
                !_previewSession.IsCurrentVideo(_viewModel.Project, _useHalfQualityPreview, still.Signature)) return;
            PreviewImage.Source = LoadBitmap(still.Path);
            PreviewImage.Visibility = Visibility.Visible;
            if (!_isPlaying)
            {
                _previewPlayback?.HideVideo();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (version == _stillPreviewVersion) _viewModel.StatusText = $"Кадр предпросмотра недоступен: {exception.Message}";
        }
        finally
        {
            if (version == _stillPreviewVersion) _stillPreviewPending = false;
        }
    }

    private void UpdateTextOverlayPreview(double timelineSeconds)
    {
        var overlay = _viewModel.Project.TextOverlays
            .LastOrDefault(item => timelineSeconds >= item.Start && timelineSeconds < item.End);
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
        var selected = TextOverlayList.SelectedItem == overlay;
        PreviewTextSelectionOutline.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        PreviewTextResizeHandles.Visibility = selected && !_isEditingPreviewText ? Visibility.Visible : Visibility.Collapsed;
        PreviewTextBorder.Visibility = Visibility.Visible;
    }

    private void PreviewTextBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var overlay = _viewModel.Project.TextOverlays
            .LastOrDefault(item => _viewModel.Playhead >= item.Start && _viewModel.Playhead < item.End);
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
        _viewModel.BeginEdit();
        _previewDraggedOverlay = overlay;
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
        _isDraggingPreviewText = false;
        _previewDraggedOverlay = null;
        PreviewTextBorder.ReleaseMouseCapture();
        _viewModel.CommitEdit("Положение текста изменено");
        TimelineEditor.InvalidateVisual();
        e.Handled = true;
    }

    private void BeginPreviewTextEditing(TextOverlay overlay)
    {
        if (_isEditingPreviewText && _previewEditedOverlay == overlay)
        {
            PreviewTextEditor.Focus();
            return;
        }
        FinishPreviewTextEditing(commit: true, refresh: false);
        StopPlayback();
        _viewModel.BeginEdit();
        _previewEditedOverlay = overlay;
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
        if (!commit && overlay is not null)
        {
            overlay.Text = _previewTextBeforeEdit;
        }
        _viewModel.CommitEdit(commit ? "Текст изменён" : "Редактирование текста отменено");
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
        _previewDraggedOverlay = overlay;
        _previewTextResizeHandle = handle.Tag as string ?? "BottomRight";
        _previewTextResizeStartPoint = startPoint;
        _previewTextResizeStartBounds = new Rect(
            Canvas.GetLeft(PreviewTextBorder),
            Canvas.GetTop(PreviewTextBorder),
            PreviewTextBorder.Width,
            PreviewTextBorder.Height);
        _viewModel.BeginEdit();
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
        _isResizingPreviewText = false;
        _previewDraggedOverlay = null;
        _viewModel.CommitEdit("Размер текстового блока изменён");
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
        ResetCompositionPreviewSources(clearImage: false);
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

        var pulse = 0.72 + Math.Abs(Math.Sin(_playbackClock.Elapsed.TotalSeconds * 7.3)) * 0.25;
        var level = Math.Clamp(clip.Volume, 0, 1) * pulse;
        AudioLeftMeter.Value = level * (clip.Pan > 0 ? 1 - clip.Pan : 1);
        AudioRightMeter.Value = level * (clip.Pan < 0 ? 1 + clip.Pan : 1);
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
        _stillPreviewCancellation?.Cancel();
        _stillPreviewPending = false;
        _stillPreviewPendingPosition = -1;
        ResetCompositionPreviewSources(clearImage: true);
        TimelineEditor.Project = _viewModel.Project;
        TimelineEditor.SelectedClipId = _viewModel.SelectedClip?.Id;
        TimelineEditor.PlayheadSeconds = _viewModel.Playhead;
        UpdatePreviewAt(_viewModel.Playhead, forceSeek: true);
    }

    private void ResetCompositionPreviewSources(bool clearImage)
    {
        _previewSession?.Reset();
        _previewSessionGeneration++;
        _pendingPreviewRequests.Clear();
        ClearVideoPlayers();
        ClearAudioPlayers();
        if (clearImage) PreviewImage.Source = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Project):
                ResetPreviewState();
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

    private async Task<bool> ConfirmCanLoseChangesAsync()
    {
        if (!ConfirmPendingEditReview())
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
            _viewModel.DiscardAutosave();
            return true;
        }

        return false;
    }

    private bool ConfirmPendingEditReview()
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
            _viewModel.AcceptEditPlanReview();
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
        if (_allowClose || !_viewModel.IsDirty)
        {
            StopPlayback();
            if (_previewPlayback is not null)
            {
                _previewPlayback.VideoPresented -= PreviewPlayback_VideoPresented;
                _previewPlayback.VideoEnded -= PreviewPlayback_VideoEnded;
                _previewPlayback.VideoFailed -= PreviewPlayback_VideoFailed;
                _previewPlayback.AudioFailed -= PreviewPlayback_AudioFailed;
                _previewPlayback.Dispose();
                _previewPlayback = null;
            }
            _previewSession?.Dispose();
            _previewSession = null;
            _viewModel.Dispose();
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
            if (!await ConfirmCanLoseChangesAsync())
            {
                return;
            }

            _allowClose = true;
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
            SeekTo(_viewModel.Playhead - (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / _viewModel.Project.FrameRate));
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            SeekTo(_viewModel.Playhead + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 1.0 / _viewModel.Project.FrameRate));
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
