using System.Collections.ObjectModel;
using System.Collections.Immutable;
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
using KadrStudio.Application.Automation;
using KadrStudio.Models;
using KadrStudio.Playback;
using KadrStudio.Services;
using KadrStudio.ViewModels;
using Microsoft.Win32;
using CoreAnalysisManifest = KadrStudio.Core.Domain.MediaAnalysisManifest;
using CoreGameProfile = KadrStudio.Core.Domain.GameEditingProfile;
using CoreMontagePlan = KadrStudio.Core.Domain.MontagePlan;
using CoreMontagePlanItem = KadrStudio.Core.Domain.MontagePlanItem;
using CoreSequenceState = KadrStudio.Core.Domain.SequenceState;
using CoreSourceAnnotation = KadrStudio.Core.Domain.SourceAnnotation;

namespace KadrStudio.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly WorkspaceSettingsService _workspaceSettingsService;
    private readonly RecentProjectsService _recentProjectsService = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private Point _mediaDragOrigin;
    private bool _isPlaying;
    private bool _allowClose;
    private bool _isCloseConfirmationPending;
    private bool _isShutdownComplete;
    private bool _isPreviewUpdateActive;
    private bool _hasQueuedPreviewUpdate;
    private bool _queuedPreviewForceSeek;
    private double _queuedPreviewSeconds;
    private double _playbackStartSeconds;
    private readonly PreviewPresenter _previewPresenter;
    private readonly string? _initialProjectPath;
    private CancellationTokenSource? _analysisCancellation;
    private readonly ObservableCollection<OllamaModelInfo> _localAiModels = [];
    private readonly ObservableCollection<ProjectHistoryEntry> _inlineHistoryEntries = [];
    private readonly ObservableCollection<AiPlanItemRow> _aiPlanRows = [];
    private readonly ObservableCollection<AiSequenceRow> _aiSequenceRows = [];
    private readonly ObservableCollection<AiAnnotationRow> _aiAnnotationRows = [];
    private ImmutableDictionary<Guid, CoreAnalysisManifest> _aiManifests = ImmutableDictionary<Guid, CoreAnalysisManifest>.Empty;
    private CoreMontagePlan? _activeMontagePlan;
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
    private TextOverlay? _textPanelDraft;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? initialProjectPath)
    {
        _initialProjectPath = initialProjectPath;
        InitializeComponent();
        var workspace = EditorWorkspaceCompositionRoot.Create();
        _workspaceSettingsService = workspace.SettingsService;
        _viewModel = new MainViewModel(workspace);
        _previewPresenter = new PreviewPresenter(PreviewImage, EmptyPreview,
            workspace.FfmpegLocator, _viewModel.RenderCoordinator, _viewModel.ArtifactStore);
        _previewPresenter.Failed += (_, exception) =>
            Dispatcher.BeginInvoke(() => _viewModel.StatusText = $"Предпросмотр: {exception.Message}");
        _previewPresenter.AudioMeterUpdated += (_, level) => Dispatcher.BeginInvoke(() =>
        {
            AudioLeftMeter.Value = level.LeftPeak;
            AudioRightMeter.Value = level.RightPeak;
        });
        _previewPresenter.SetProject(_viewModel.CoreState, _useHalfQualityPreview);
        DataContext = _viewModel;
        LocalAiModelComboBox.ItemsSource = _localAiModels;
        AiGameProfileComboBox.ItemsSource = _viewModel.GetGameEditingProfiles();
        AiGameProfileComboBox.SelectedItem = _viewModel.GetGameEditingProfiles()
            .FirstOrDefault(item => item.Id == "rust") ?? _viewModel.GetGameEditingProfiles().FirstOrDefault();
        AiPlanItemsListBox.ItemsSource = _aiPlanRows;
        AiSequencesListBox.ItemsSource = _aiSequenceRows;
        AiAnnotationsListBox.ItemsSource = _aiAnnotationRows;
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
        TimelineEditor.ThumbnailRequest = _viewModel.GetTimelineThumbnailAsync;
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

        var recoveries = await _viewModel.ListAutosavesAsync();
        if (recoveries.Count > 0)
        {
            var recoveryWindow = new RecoveryWindow(recoveries) { Owner = this };
            if (recoveryWindow.ShowDialog() == true && recoveryWindow.SelectedRecovery is { } selectedRecovery)
            {
                try
                {
                    await _viewModel.RecoverAutosaveAsync(selectedRecovery);
                    ResetPreviewState();
                }
                catch (Exception exception)
                {
                    await _viewModel.DiscardAutosaveAsync(selectedRecovery);
                    ShowError("Не удалось восстановить проект", exception);
                }
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

    private async void MoveMediaCache_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Новая папка кэша Kadr Studio",
            InitialDirectory = _viewModel.ArtifactStore.Options.Root,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _viewModel.IsBusy = true;
            _viewModel.StatusText = "Перенос кэша медиа…";
            await _viewModel.ArtifactStore.MoveAsync(dialog.FolderName);
            await SaveArtifactSettingsAsync();
            _viewModel.StatusText = $"Кэш перенесён: {dialog.FolderName}";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось перенести кэш", exception);
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private async void ClearMediaCache_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "Удалить прокси, эскизы и waveform? Они автоматически перестроятся; исходники и проект не изменятся.",
                "Очистка кэша",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            _viewModel.IsBusy = true;
            await _viewModel.ArtifactStore.ClearAsync();
            _viewModel.StatusText = "Кэш медиа очищен";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось очистить кэш", exception);
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private async void SetMediaCacheLimit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string gigabytesText } ||
            !long.TryParse(gigabytesText, out var gigabytes))
            return;
        try
        {
            var bytes = checked(gigabytes * 1024 * 1024 * 1024);
            _viewModel.IsBusy = true;
            await _viewModel.ArtifactStore.SetDiskBudgetAsync(bytes);
            await SaveArtifactSettingsAsync();
            _viewModel.StatusText = $"Лимит кэша: {gigabytes} ГБ";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось изменить лимит кэша", exception);
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private Task SaveArtifactSettingsAsync()
    {
        var options = _viewModel.ArtifactStore.Options;
        return _workspaceSettingsService.SaveAsync(new WorkspaceSettings(
            options.Root, options.DiskBudgetBytes));
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
            _previewPresenter.SetProject(_viewModel.CoreState, _useHalfQualityPreview);
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
        var exportWindow = new ExportWindow(_viewModel.CoreState, _viewModel.ExportService)
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
        _viewModel.EnsureSequenceWorkspace();
        SetLeftPanel(showAnalysis: true, showHistory: false, showText: false);
        RefreshAiWorkspaceUi();
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

    private void TextOverlayEdit_End(object sender, RoutedEventArgs e)
    {
        if (_textPanelDraft is not null &&
            TextOverlayList.SelectedItem is TextOverlay stored &&
            !TextOverlayPresentationEquals(stored, _textPanelDraft))
        {
            var id = _textPanelDraft.Id;
            _viewModel.UpdateTextOverlay(_textPanelDraft.Clone());
            TextOverlayList.SelectedItem = _viewModel.Project.TextOverlays.FirstOrDefault(item => item.Id == id);
        }
        UpdateTextOverlayPreview(_viewModel.Playhead);
    }

    private void TextOverlayCombo_Closed(object? sender, EventArgs e)
        => TextOverlayEdit_End(sender ?? this, new RoutedEventArgs());

    private void TextOverlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TextOverlayList.SelectedItem is TextOverlay overlay)
        {
            _textPanelDraft = overlay.Clone();
            TextPropertiesPanel.DataContext = _textPanelDraft;
            TimelineEditor.SelectedTextOverlayId = overlay.Id;
            TimelineEditor.SelectedClipId = null;
            _viewModel.SelectedClip = null;
            SeekTo(overlay.Start);
        }
        else
        {
            _textPanelDraft = null;
            TextPropertiesPanel.DataContext = null;
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
            var selectedCoreClip = _viewModel.SelectedClip is { } selected
                ? _viewModel.CoreState.FindMediaClip(selected.Id)
                : null;
            EditCommandPlan plan;
            if (EditingCommandPlanner.TryCreateDeterministic(
                    _viewModel.CoreState,
                    prompt,
                    selectedCoreClip,
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
                    _viewModel.CoreState,
                    prompt,
                    model.Name,
                    selectedCoreClip,
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

    private void AiTargetFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AiTargetDurationTextBox is null || AiTargetFormatComboBox?.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        AiTargetDurationTextBox.Text = string.Equals(item.Tag?.ToString(), "Shorts", StringComparison.Ordinal)
            ? "45"
            : "720";
    }

    private async void AiAnalyzeSources_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetAiMontageBusy(true, "Подготавливается индекс сцен, речи и игровых событий…");
            _aiManifests = await AnalyzeAiSourcesAsync();
            AnalysisSummaryTextBlock.Text = _aiManifests.Count == 0
                ? "В выбранной области нет видео для анализа."
                : $"Индекс готов: {_aiManifests.Count} исходников, {_aiManifests.Values.Sum(item => item.Segments.Length)} подтверждённых диапазонов.";
            AiPlanSummaryTextBlock.Text = "Анализ готов. Теперь можно составить редактируемый план.";
            AiMontageTabControl.SelectedIndex = 2;
        }
        catch (OperationCanceledException)
        {
            AnalysisSummaryTextBlock.Text = "Анализ отменён.";
        }
        catch (Exception exception)
        {
            AnalysisSummaryTextBlock.Text = exception.Message;
            ShowError("Не удалось проанализировать материал", exception);
        }
        finally
        {
            SetAiMontageBusy(false);
        }
    }

    private async void AiGeneratePlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = BuildAiMontageRequest();
            if (!request.Scope.SourceIds.Any())
                throw new InvalidOperationException("В выбранной области нет видеоисходников.");

            SetAiMontageBusy(true, "ИИ-режиссёр составляет безопасный план из подтверждённых диапазонов…");
            if (request.Scope.SourceIds.Any(id => !_aiManifests.ContainsKey(id)))
                _aiManifests = await AnalyzeAiSourcesAsync(request.Profile, request.Scope.SourceIds);

            _activeMontagePlan = await _viewModel.CreateMontagePlanAsync(request, _aiManifests);
            RefreshAiPlanRows();
            AnalysisSummaryTextBlock.Text = "План создан без изменения таймлайна. Проверьте порядок, границы и причины выбора.";
        }
        catch (OperationCanceledException)
        {
            AnalysisSummaryTextBlock.Text = "Создание плана отменено.";
        }
        catch (Exception exception)
        {
            AiPlanSummaryTextBlock.Text = exception.Message;
            ShowError("Не удалось составить план", exception);
        }
        finally
        {
            SetAiMontageBusy(false);
        }
    }

    private async void AiRevisePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMontagePlan is null)
        {
            return;
        }

        var correction = AiRevisionTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(correction))
        {
            AiPlanSummaryTextBlock.Text = "Опишите, что нужно изменить в плане.";
            return;
        }

        try
        {
            SetAiMontageBusy(true, "План пересобирается; заблокированные и обязательные пункты останутся на месте…");
            _activeMontagePlan = await _viewModel.ReviseMontagePlanAsync(
                _activeMontagePlan, correction, _aiManifests);
            RefreshAiPlanRows();
            AnalysisSummaryTextBlock.Text = "Корректировка применена только к незаблокированным пунктам плана.";
        }
        catch (OperationCanceledException)
        {
            AnalysisSummaryTextBlock.Text = "Корректировка отменена.";
        }
        catch (Exception exception)
        {
            AiPlanSummaryTextBlock.Text = exception.Message;
            ShowError("Не удалось исправить план", exception);
        }
        finally
        {
            SetAiMontageBusy(false);
        }
    }

    private void AiCreateDraft_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMontagePlan is null)
        {
            return;
        }

        try
        {
            var validation = _viewModel.AiMontageCoordinator.ValidatePlan(_viewModel.CoreState, _activeMontagePlan);
            if (!validation.IsValid)
                throw new InvalidOperationException(string.Join("\n", validation.Validation.Errors.Select(item => item.Message)));

            var sequence = _viewModel.CreateMontageDraft(_activeMontagePlan, _aiManifests);
            _activeMontagePlan = _viewModel.GetMontagePlans().FirstOrDefault(item => item.Id == _activeMontagePlan.Id)
                                 ?? _activeMontagePlan;
            RefreshAiWorkspaceUi();
            AiMontageTabControl.SelectedIndex = 3;
            ResetPreviewState();
            TimelineEditor.InvalidateVisual();
            AnalysisSummaryTextBlock.Text = $"Создан отдельный черновик «{sequence.Name}». Исходный монтаж не изменён.";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось создать черновик", exception);
        }
    }

    private void AiPlanSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AiPlanItemsListBox.SelectedItem is not AiPlanItemRow row)
        {
            return;
        }

        AiPlanItemStartTextBox.Text = FormatEditorTime(row.Item.SourceRange.Start.TotalSeconds);
        AiPlanItemEndTextBox.Text = FormatEditorTime(row.Item.SourceRange.End.TotalSeconds);
    }

    private void AiPlanMoveUp_Click(object sender, RoutedEventArgs e) => MoveAiPlanItem(-1);

    private void AiPlanMoveDown_Click(object sender, RoutedEventArgs e) => MoveAiPlanItem(1);

    private void MoveAiPlanItem(int offset)
    {
        if (_activeMontagePlan is null || AiPlanItemsListBox.SelectedItem is not AiPlanItemRow selected)
        {
            return;
        }

        var items = _activeMontagePlan.Items.OrderBy(item => item.Order).ToList();
        var current = items.FindIndex(item => item.Id == selected.Item.Id);
        var target = current + offset;
        if (current < 0 || target < 0 || target >= items.Count)
        {
            return;
        }

        (items[current], items[target]) = (items[target], items[current]);
        SaveAiPlanItems(items, selected.Item.Id);
    }

    private void AiPlanToggleLock_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMontagePlan is null || AiPlanItemsListBox.SelectedItem is not AiPlanItemRow selected)
        {
            return;
        }

        SaveAiPlanItems(
            _activeMontagePlan.Items.Select(item => item.Id == selected.Item.Id
                ? item with { IsLocked = !item.IsLocked }
                : item),
            selected.Item.Id);
    }

    private void AiPlanRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMontagePlan is null || AiPlanItemsListBox.SelectedItem is not AiPlanItemRow selected)
        {
            return;
        }

        if (selected.Item.IsLocked)
        {
            AiPlanSummaryTextBlock.Text = "Сначала снимите блокировку с пункта. Обязательные диапазоны удалить всё равно нельзя.";
            return;
        }

        SaveAiPlanItems(_activeMontagePlan.Items.Where(item => item.Id != selected.Item.Id), null);
    }

    private void AiPlanTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_activeMontagePlan is null || AiPlanItemsListBox.SelectedItem is not AiPlanItemRow selected)
        {
            return;
        }

        if (selected.Item.IsLocked)
        {
            AiPlanSummaryTextBlock.Text = "Заблокированный пункт нельзя обрезать.";
            return;
        }
        if (!TryParseEditorTime(AiPlanItemStartTextBox.Text, out var start) ||
            !TryParseEditorTime(AiPlanItemEndTextBox.Text, out var end) || end <= start)
        {
            AiPlanSummaryTextBlock.Text = "Укажите корректные source-in и source-out.";
            return;
        }

        var source = _viewModel.CoreState.Sources.GetValueOrDefault(selected.Item.SourceId);
        if (source is null || end > source.Duration.TotalSeconds + 0.001)
        {
            AiPlanSummaryTextBlock.Text = "Новые границы выходят за пределы исходника.";
            return;
        }

        var range = new KadrStudio.Core.Domain.TimeRange(
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(start),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(end - start));
        SaveAiPlanItems(
            _activeMontagePlan.Items.Select(item => item.Id == selected.Item.Id
                ? item with { SourceRange = range }
                : item),
            selected.Item.Id);
    }

    private void SaveAiPlanItems(IEnumerable<CoreMontagePlanItem> sourceItems, Guid? selectedId)
    {
        if (_activeMontagePlan is null)
        {
            return;
        }

        var items = sourceItems.Select((item, index) => item with { Order = index }).ToImmutableArray();
        var candidate = _activeMontagePlan with { Items = items, UpdatedAt = DateTimeOffset.UtcNow };
        var validation = _viewModel.AiMontageCoordinator.ValidatePlan(_viewModel.CoreState, candidate);
        if (!validation.IsValid)
        {
            AiPlanSummaryTextBlock.Text = string.Join(" ", validation.Validation.Errors.Select(item => item.Message));
            return;
        }

        _viewModel.SaveMontagePlan(candidate);
        _activeMontagePlan = candidate;
        RefreshAiPlanRows(selectedId);
    }

    private void AiAddRequired_Click(object sender, RoutedEventArgs e)
        => AddAiAnnotation(KadrStudio.Core.Domain.SourceAnnotationKind.Required);

    private void AiAddExcluded_Click(object sender, RoutedEventArgs e)
        => AddAiAnnotation(KadrStudio.Core.Domain.SourceAnnotationKind.Excluded);

    private void AiAddNote_Click(object sender, RoutedEventArgs e)
        => AddAiAnnotation(KadrStudio.Core.Domain.SourceAnnotationKind.Note);

    private void AddAiAnnotation(KadrStudio.Core.Domain.SourceAnnotationKind kind)
    {
        try
        {
            var asset = AiSourceListBox.SelectedItem as MediaAsset
                        ?? AnalysisAssetComboBox.SelectedItem as MediaAsset
                        ?? _viewModel.SelectedClipAsset
                        ?? _viewModel.SelectedAsset
                        ?? throw new InvalidOperationException("Выберите исходник для метки.");
            if (asset.Duration <= 0)
                throw new InvalidOperationException("У исходника нет доступного диапазона времени.");

            var start = 0d;
            var end = 0d;
            var hasRange = TryParseEditorTime(AnalysisStartTextBox.Text, out start) &&
                           TryParseEditorTime(AnalysisEndTextBox.Text, out end) && end > start;
            if (!hasRange && _viewModel.SelectedClip is { AssetId: var assetId } clip && assetId == asset.Id)
            {
                start = clip.SourceStart;
                end = clip.SourceStart + clip.Duration;
                hasRange = true;
            }
            if (!hasRange)
            {
                start = 0;
                end = asset.Duration;
            }
            start = Math.Clamp(start, 0, asset.Duration);
            end = Math.Clamp(end, start, asset.Duration);
            if (end <= start + 0.001)
                throw new InvalidOperationException("Диапазон метки должен иметь ненулевую длительность.");

            var annotation = new CoreSourceAnnotation(
                Guid.NewGuid(),
                asset.Id,
                kind,
                new KadrStudio.Core.Domain.TimeRange(
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(start),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(end - start)),
                AiAnnotationNoteTextBox.Text?.Trim() ?? string.Empty,
                DateTimeOffset.UtcNow);
            _viewModel.UpsertSourceAnnotation(annotation);
            RefreshAiAnnotations();
            AnalysisSummaryTextBlock.Text = kind switch
            {
                KadrStudio.Core.Domain.SourceAnnotationKind.Required => "Диапазон закреплён: ИИ не сможет удалить его из плана.",
                KadrStudio.Core.Domain.SourceAnnotationKind.Excluded => "Диапазон запрещён для ИИ-монтажа.",
                _ => "Заметка добавлена к исходнику."
            };
        }
        catch (Exception exception)
        {
            ShowError("Не удалось добавить указание", exception);
        }
    }

    private void AiDeleteAnnotation_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AiAnnotationsListBox.SelectedItem is not AiAnnotationRow row)
        {
            return;
        }

        _viewModel.DeleteSourceAnnotation(row.Annotation.Id);
        RefreshAiAnnotations();
        AnalysisSummaryTextBlock.Text = "Указание для ИИ удалено.";
    }

    private void MediaAiRequired_Click(object sender, RoutedEventArgs e)
        => AddQuickAiAnnotation(_viewModel.SelectedAsset, null, KadrStudio.Core.Domain.SourceAnnotationKind.Required);

    private void MediaList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(MediaList, e.OriginalSource as DependencyObject) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void MediaAiExcluded_Click(object sender, RoutedEventArgs e)
        => AddQuickAiAnnotation(_viewModel.SelectedAsset, null, KadrStudio.Core.Domain.SourceAnnotationKind.Excluded);

    private void MediaAiNote_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAsset is { } asset)
            OpenAiAnnotationEditor(asset, 0, asset.Duration);
    }

    private void TimelineAiRequired_Click(object sender, RoutedEventArgs e)
        => AddQuickAiAnnotation(
            _viewModel.SelectedClipAsset,
            _viewModel.SelectedClip is { } clip ? (clip.SourceStart, clip.SourceStart + clip.Duration) : null,
            KadrStudio.Core.Domain.SourceAnnotationKind.Required);

    private void TimelineAiExcluded_Click(object sender, RoutedEventArgs e)
        => AddQuickAiAnnotation(
            _viewModel.SelectedClipAsset,
            _viewModel.SelectedClip is { } clip ? (clip.SourceStart, clip.SourceStart + clip.Duration) : null,
            KadrStudio.Core.Domain.SourceAnnotationKind.Excluded);

    private void TimelineAiNote_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedClipAsset is { } asset && _viewModel.SelectedClip is { } clip)
            OpenAiAnnotationEditor(asset, clip.SourceStart, clip.SourceStart + clip.Duration);
    }

    private void AddQuickAiAnnotation(
        MediaAsset? asset,
        (double Start, double End)? requestedRange,
        KadrStudio.Core.Domain.SourceAnnotationKind kind)
    {
        if (asset is null || asset.Duration <= 0)
        {
            AnalysisSummaryTextBlock.Text = "Выберите видео или клип для указания ИИ.";
            return;
        }
        var start = Math.Clamp(requestedRange?.Start ?? 0, 0, asset.Duration);
        var end = Math.Clamp(requestedRange?.End ?? asset.Duration, start, asset.Duration);
        if (end <= start + 0.001)
        {
            return;
        }
        try
        {
            _viewModel.UpsertSourceAnnotation(new CoreSourceAnnotation(
                Guid.NewGuid(), asset.Id, kind,
                new KadrStudio.Core.Domain.TimeRange(
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(start),
                    KadrStudio.Core.Domain.TimelineTime.FromSeconds(end - start)),
                string.Empty,
                DateTimeOffset.UtcNow));
            RefreshAiAnnotations();
            _viewModel.StatusText = kind == KadrStudio.Core.Domain.SourceAnnotationKind.Required
                ? "Диапазон обязателен для ИИ-монтажа"
                : "Диапазон исключён из ИИ-монтажа";
        }
        catch (Exception exception)
        {
            ShowError("Не удалось сохранить указание ИИ", exception);
        }
    }

    private void OpenAiAnnotationEditor(MediaAsset asset, double start, double end)
    {
        _viewModel.EnsureSequenceWorkspace();
        SetLeftPanel(showAnalysis: true, showHistory: false, showText: false);
        RefreshAiWorkspaceUi();
        AiMontageTabControl.SelectedIndex = 0;
        AiSourceListBox.SelectedItem = asset;
        AnalysisAssetComboBox.SelectedItem = asset;
        AnalysisStartTextBox.Text = FormatEditorTime(start);
        AnalysisEndTextBox.Text = FormatEditorTime(end);
        AiAnnotationNoteTextBox.Text = string.Empty;
        AiAnnotationNoteTextBox.Focus();
        AnalysisSummaryTextBlock.Text = "Напишите пояснение и нажмите «Заметка».";
    }

    private void AiOpenSequence_Click(object sender, RoutedEventArgs e)
    {
        if (AiSequencesListBox.SelectedItem is not AiSequenceRow row ||
            !_viewModel.ActivateSequence(row.Sequence.Id))
        {
            return;
        }

        RefreshAiWorkspaceUi();
        ResetPreviewState();
        TimelineEditor.InvalidateVisual();
        AnalysisSummaryTextBlock.Text = $"Открыта версия «{row.Sequence.Name}».";
    }

    private void AiAcceptDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.AcceptActiveMontageDraft())
        {
            AnalysisSummaryTextBlock.Text = "Активная версия не является черновиком.";
            return;
        }

        RefreshAiWorkspaceUi();
        AnalysisSummaryTextBlock.Text = "Активный черновик принят и сохранён как самостоятельная версия.";
    }

    private void AiDeleteDraft_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.DeleteActiveMontageDraft())
        {
            AnalysisSummaryTextBlock.Text = "Удалять можно только активный черновик.";
            return;
        }

        RefreshAiWorkspaceUi();
        ResetPreviewState();
        TimelineEditor.InvalidateVisual();
        AnalysisSummaryTextBlock.Text = "Черновик удалён, открыт его исходный вариант.";
    }

    private async Task<ImmutableDictionary<Guid, CoreAnalysisManifest>> AnalyzeAiSourcesAsync(
        CoreGameProfile? profile = null,
        ImmutableArray<Guid>? sourceIds = null)
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = new CancellationTokenSource();
        var selectedProfile = profile ?? GetSelectedAiProfile();
        var ids = sourceIds ?? ResolveAiScope().SourceIds;
        var model = UseLocalAiCheckBox.IsChecked == true && LocalAiModelComboBox.SelectedItem is OllamaModelInfo selectedModel
            ? selectedModel.Name
            : string.Empty;
        var progress = new Progress<double>(value =>
        {
            AnalysisProgressBar.IsIndeterminate = false;
            AnalysisProgressBar.Value = Math.Clamp(value * 100, 0, 100);
            _viewModel.StatusText = $"ИИ-анализ: {value:P0}";
        });
        return await _viewModel.AnalyzeMontageSourcesAsync(
            new MediaAnalysisRequest(ids, selectedProfile, model, !string.IsNullOrWhiteSpace(model)),
            progress,
            _analysisCancellation.Token);
    }

    private KadrStudio.Core.Domain.MontageRequest BuildAiMontageRequest()
    {
        var scope = ResolveAiScope();
        var profile = GetSelectedAiProfile();
        var format = AiTargetFormatComboBox.SelectedItem is ComboBoxItem formatItem &&
                     string.Equals(formatItem.Tag?.ToString(), "Shorts", StringComparison.Ordinal)
            ? KadrStudio.Core.Domain.MontageTargetFormat.Shorts
            : KadrStudio.Core.Domain.MontageTargetFormat.YouTube;
        var defaultSeconds = format == KadrStudio.Core.Domain.MontageTargetFormat.Shorts ? 45d : 720d;
        if (!double.TryParse(AiTargetDurationTextBox.Text?.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var targetSeconds))
            targetSeconds = defaultSeconds;
        var minimumSeconds = format == KadrStudio.Core.Domain.MontageTargetFormat.Shorts ? 15d : 480d;
        var maximumSeconds = format == KadrStudio.Core.Domain.MontageTargetFormat.Shorts ? 90d : 1200d;
        targetSeconds = Math.Clamp(targetSeconds, minimumSeconds, maximumSeconds);
        AiTargetDurationTextBox.Text = targetSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var sourceIds = scope.SourceIds.ToHashSet();
        var constraints = _viewModel.CoreState.SourceAnnotations
            .Where(item => sourceIds.Contains(item.SourceId))
            .Select(item => new KadrStudio.Core.Domain.MontageConstraint(
                item.Id, item.SourceId, item.Kind, item.SourceRange, item.Note, IsHard: true))
            .ToImmutableArray();
        return new KadrStudio.Core.Domain.MontageRequest(
            Guid.NewGuid(),
            scope,
            format,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(minimumSeconds),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(targetSeconds),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(maximumSeconds),
            AnalysisPromptTextBox.Text?.Trim() ?? string.Empty,
            profile,
            constraints);
    }

    private KadrStudio.Core.Domain.MontageScope ResolveAiScope()
    {
        var kindName = (AiScopeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "MediaLibrary";
        if (!Enum.TryParse<KadrStudio.Core.Domain.MontageScopeKind>(kindName, out var kind))
            kind = KadrStudio.Core.Domain.MontageScopeKind.MediaLibrary;

        IEnumerable<KadrStudio.Core.Domain.MediaClip> clips = _viewModel.CoreState.MediaClips;
        KadrStudio.Core.Domain.TimeRange? timelineRange = null;
        Guid? sequenceId = null;
        switch (kind)
        {
            case KadrStudio.Core.Domain.MontageScopeKind.SelectedClips:
                clips = _viewModel.SelectedClip is { } selected
                    ? clips.Where(item => item.Id == selected.Id)
                    : [];
                sequenceId = _viewModel.CoreState.ActiveSequenceId;
                break;
            case KadrStudio.Core.Domain.MontageScopeKind.InOutRange:
                if (_viewModel.CoreState.InPoint is not { } inPoint || _viewModel.CoreState.OutPoint is not { } outPoint || outPoint <= inPoint)
                    throw new InvalidOperationException("Сначала задайте корректный диапазон In/Out на таймлайне.");
                timelineRange = new KadrStudio.Core.Domain.TimeRange(inPoint, outPoint - inPoint);
                clips = clips.Where(item => item.Range.Overlaps(timelineRange.Value));
                sequenceId = _viewModel.CoreState.ActiveSequenceId;
                break;
            case KadrStudio.Core.Domain.MontageScopeKind.CurrentSequence:
                sequenceId = _viewModel.CoreState.ActiveSequenceId;
                break;
        }

        ImmutableArray<Guid> sourceIds;
        ImmutableArray<Guid> clipIds = [];
        if (kind == KadrStudio.Core.Domain.MontageScopeKind.SelectedSources)
        {
            sourceIds = AiSourceListBox.SelectedItems.OfType<MediaAsset>()
                .Where(item => item.Kind == KadrStudio.Models.MediaKind.Video)
                .Select(item => item.Id).Distinct().ToImmutableArray();
        }
        else if (kind == KadrStudio.Core.Domain.MontageScopeKind.MediaLibrary)
        {
            sourceIds = _viewModel.CoreState.Sources.Values
                .Where(item => item.Kind == KadrStudio.Core.Domain.MediaKind.Video)
                .Select(item => item.Id).ToImmutableArray();
        }
        else
        {
            var materialClips = clips.Where(item =>
                    _viewModel.CoreState.Sources.TryGetValue(item.SourceId, out var source) &&
                    source.Kind == KadrStudio.Core.Domain.MediaKind.Video)
                .ToImmutableArray();
            sourceIds = materialClips.Select(item => item.SourceId).Distinct().ToImmutableArray();
            clipIds = materialClips.Select(item => item.Id).ToImmutableArray();
        }

        return new KadrStudio.Core.Domain.MontageScope(kind, sourceIds, sequenceId, clipIds, timelineRange);
    }

    private CoreGameProfile GetSelectedAiProfile()
        => AiGameProfileComboBox.SelectedItem as CoreGameProfile
           ?? _viewModel.GetGameEditingProfiles().First();

    private void SetAiMontageBusy(bool busy, string? status = null)
    {
        AiAnalyzeSourcesButton.IsEnabled = !busy;
        AiGeneratePlanButton.IsEnabled = !busy;
        AiRevisePlanButton.IsEnabled = !busy && _activeMontagePlan is not null;
        AiCreateDraftButton.IsEnabled = !busy && _activeMontagePlan is not null;
        CancelAnalysisButton.IsEnabled = busy;
        AnalysisProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AnalysisProgressBar.IsIndeterminate = busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            AnalysisSummaryTextBlock.Text = status;
            _viewModel.StatusText = status;
        }
    }

    private void RefreshAiWorkspaceUi()
    {
        RefreshAiAnnotations();
        _aiSequenceRows.Clear();
        foreach (var sequence in _viewModel.GetSequences()
                     .OrderBy(item => item.Status == KadrStudio.Core.Domain.SequenceStatus.Original ? 0 : 1)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            _aiSequenceRows.Add(new AiSequenceRow(sequence));
        AiSequencesListBox.SelectedItem = _aiSequenceRows.FirstOrDefault(item =>
            item.Sequence.Id == _viewModel.CoreState.ActiveSequenceId);

        if (_activeMontagePlan is null || _viewModel.GetMontagePlans().All(item => item.Id != _activeMontagePlan.Id))
            _activeMontagePlan = _viewModel.GetMontagePlans().OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        else
            _activeMontagePlan = _viewModel.GetMontagePlans().First(item => item.Id == _activeMontagePlan.Id);
        RefreshAiPlanRows();
    }

    private void RefreshAiAnnotations()
    {
        _aiAnnotationRows.Clear();
        foreach (var annotation in _viewModel.CoreState.SourceAnnotations.OrderBy(item => item.SourceId).ThenBy(item => item.SourceRange.Start))
        {
            var sourceName = _viewModel.CoreState.Sources.GetValueOrDefault(annotation.SourceId)?.Name ?? "Удалённый исходник";
            _aiAnnotationRows.Add(new AiAnnotationRow(annotation, sourceName));
        }
    }

    private void RefreshAiPlanRows(Guid? selectedId = null)
    {
        _aiPlanRows.Clear();
        if (_activeMontagePlan is null)
        {
            AiPlanSummaryTextBlock.Text = "Сначала проанализируйте материал.";
            AiRevisePlanButton.IsEnabled = false;
            AiCreateDraftButton.IsEnabled = false;
            return;
        }

        foreach (var item in _activeMontagePlan.Items.OrderBy(item => item.Order))
        {
            var sourceName = _viewModel.CoreState.Sources.GetValueOrDefault(item.SourceId)?.Name ?? "Удалённый исходник";
            _aiPlanRows.Add(new AiPlanItemRow(item, sourceName));
        }
        var validation = _viewModel.AiMontageCoordinator.ValidatePlan(_viewModel.CoreState, _activeMontagePlan);
        var details = new[]
        {
            _activeMontagePlan.Summary,
            $"{_activeMontagePlan.Items.Length} фрагментов · {FormatEditorTime(_activeMontagePlan.Duration.TotalSeconds)}",
            string.Join(" ", _activeMontagePlan.Warnings.Concat(validation.Warnings)),
            validation.IsValid ? string.Empty : string.Join(" ", validation.Validation.Errors.Select(item => item.Message))
        };
        AiPlanSummaryTextBlock.Text = string.Join(" ", details.Where(item => !string.IsNullOrWhiteSpace(item)));
        AiRevisePlanButton.IsEnabled = validation.IsValid;
        AiCreateDraftButton.IsEnabled = validation.IsValid;
        if (selectedId is { } id)
            AiPlanItemsListBox.SelectedItem = _aiPlanRows.FirstOrDefault(item => item.Item.Id == id);
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
            QueuePreviewEngineUpdate(timelineSeconds, forceSeek);
        UpdateTextOverlayPreview(timelineSeconds);
    }

    private void QueuePreviewEngineUpdate(double timelineSeconds, bool forceSeek)
    {
        _queuedPreviewSeconds = timelineSeconds;
        _queuedPreviewForceSeek |= forceSeek;
        _hasQueuedPreviewUpdate = true;
        if (_isPreviewUpdateActive) return;
        _isPreviewUpdateActive = true;
        _ = DrainPreviewEngineUpdatesAsync();
    }

    private async Task DrainPreviewEngineUpdatesAsync()
    {
        try
        {
            while (_hasQueuedPreviewUpdate)
            {
                var timelineSeconds = _queuedPreviewSeconds;
                var forceSeek = _queuedPreviewForceSeek;
                _hasQueuedPreviewUpdate = false;
                _queuedPreviewForceSeek = false;
                await _previewPresenter.UpdateAsync(timelineSeconds, forceSeek, _isPlaying);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _viewModel.StatusText = $"Предпросмотр недоступен: {exception.Message}";
            _hasQueuedPreviewUpdate = false;
            _queuedPreviewForceSeek = false;
            if (_isPlaying)
            {
                _isPlaying = false;
                _playbackTimer.Stop();
                _playbackClock.Stop();
                PlayPauseButton.Content = "\uE768";
                UpdateAudioMeters(null);
            }
        }
        finally
        {
            _isPreviewUpdateActive = false;
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
        if (_textPanelDraft?.Id == stored.Id) return _textPanelDraft;
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
        _previewPresenter.SetProject(_viewModel.CoreState, _useHalfQualityPreview);
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
        _previewPresenter.SetProject(_viewModel.CoreState, _useHalfQualityPreview);
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

    private sealed record AiPlanItemRow(CoreMontagePlanItem Item, string SourceName)
    {
        public string Title => $"{Item.Order + 1}. {RoleLabel(Item.Role)} · {SourceName}";
        public string LockLabel => Item.IsLocked ? "ЗАБЛОКИРОВАН" : Item.Confidence < 0.6 ? "ПРОВЕРИТЬ" : string.Empty;
        public string TimeLabel =>
            $"{FormatEditorTime(Item.SourceRange.Start.TotalSeconds)}–{FormatEditorTime(Item.SourceRange.End.TotalSeconds)} · уверенность {Item.Confidence:P0}";
        public string Reason => Item.Reason;

        private static string RoleLabel(KadrStudio.Core.Domain.MontageRole role) => role switch
        {
            KadrStudio.Core.Domain.MontageRole.Hook => "Hook",
            KadrStudio.Core.Domain.MontageRole.Setup => "Setup",
            KadrStudio.Core.Domain.MontageRole.Development => "Development",
            KadrStudio.Core.Domain.MontageRole.Payoff => "Payoff",
            KadrStudio.Core.Domain.MontageRole.Ending => "Ending",
            _ => role.ToString()
        };
    }

    private sealed record AiSequenceRow(CoreSequenceState Sequence)
    {
        public string Name => Sequence.Name;
        public string Details =>
            $"{StatusLabel(Sequence.Status)} · {FormatLabel(Sequence.TargetFormat)} · {FormatEditorTime(Sequence.Duration.TotalSeconds)} · rev {Sequence.Revision}";

        private static string StatusLabel(KadrStudio.Core.Domain.SequenceStatus status) => status switch
        {
            KadrStudio.Core.Domain.SequenceStatus.Original => "Исходная",
            KadrStudio.Core.Domain.SequenceStatus.Draft => "Черновик",
            KadrStudio.Core.Domain.SequenceStatus.Accepted => "Принята",
            _ => status.ToString()
        };

        private static string FormatLabel(KadrStudio.Core.Domain.MontageTargetFormat format) => format switch
        {
            KadrStudio.Core.Domain.MontageTargetFormat.YouTube => "YouTube 16:9",
            KadrStudio.Core.Domain.MontageTargetFormat.Shorts => "Shorts 9:16",
            _ => "Исходный формат"
        };
    }

    private sealed record AiAnnotationRow(CoreSourceAnnotation Annotation, string SourceName)
    {
        public override string ToString()
        {
            var kind = Annotation.Kind switch
            {
                KadrStudio.Core.Domain.SourceAnnotationKind.Required => "Обязательно",
                KadrStudio.Core.Domain.SourceAnnotationKind.Excluded => "Запрещено",
                _ => "Заметка"
            };
            var note = string.IsNullOrWhiteSpace(Annotation.Note) ? string.Empty : $" · {Annotation.Note}";
            return $"{kind}: {SourceName} {FormatEditorTime(Annotation.SourceRange.Start.TotalSeconds)}–{FormatEditorTime(Annotation.SourceRange.End.TotalSeconds)}{note}";
        }
    }

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
            new Action(() => _ = ConfirmCloseAfterClosingEventAsync()));
    }

    private async Task ConfirmCloseAfterClosingEventAsync()
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
