using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using KadrStudio.Models;
using KadrStudio.Services;

namespace KadrStudio.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly FfmpegLocator _ffmpegLocator = new();
    private readonly ProcessRunner _processRunner = new();
    private readonly ProjectService _projectService = new();
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private readonly List<TimelineClip> _subscribedClips = new();
    private readonly List<TextOverlay> _subscribedTextOverlays = new();
    private CancellationTokenSource? _autosaveCancellation;
    private EditorProject _project;
    private ICollectionView _mediaView = null!;
    private TimelineClip? _selectedClip;
    private MediaAsset? _selectedAsset;
    private string _searchText = string.Empty;
    private string _statusText = "Готово";
    private bool _isBusy;
    private bool _isDirty;
    private double _playhead;
    private string? _pendingEditSnapshot;
    private string? _editReviewSnapshot;
    private string? _editReviewReason;
    private Guid? _editReviewSelectedClipId;
    private double _editReviewPlayhead;
    private bool _editReviewWasDirty;
    private bool _suppressDirtyTracking;

    public MainViewModel()
    {
        _project = EditorProject.CreateNew();
        MediaProbeService = new MediaProbeService(_ffmpegLocator, _processRunner);
        ThumbnailService = new ThumbnailService(_ffmpegLocator, _processRunner);
        PreviewCompositionService = new PreviewCompositionService(_ffmpegLocator, _processRunner);
        TimelineMediaCacheService = new TimelineMediaCacheService(_ffmpegLocator, _processRunner);
        ExportService = new ExportService(_ffmpegLocator, _processRunner);
        ProjectHistoryService = new ProjectHistoryService();
        AutoSubtitleService = new AutoSubtitleService(_ffmpegLocator, _processRunner);
        VideoAnalysisService = new VideoAnalysisService(_ffmpegLocator, _processRunner);
        OllamaVideoAnalysisService = new OllamaVideoAnalysisService(_ffmpegLocator, _processRunner);
        AttachProject(_project);
        BuildMediaView();
    }

    public MediaProbeService MediaProbeService { get; }
    public ThumbnailService ThumbnailService { get; }
    public PreviewCompositionService PreviewCompositionService { get; }
    public TimelineMediaCacheService TimelineMediaCacheService { get; }
    public ExportService ExportService { get; }
    public ProjectHistoryService ProjectHistoryService { get; }
    public AutoSubtitleService AutoSubtitleService { get; }
    public VideoAnalysisService VideoAnalysisService { get; }
    public OllamaVideoAnalysisService OllamaVideoAnalysisService { get; }

    public EditorProject Project
    {
        get => _project;
        private set
        {
            if (ReferenceEquals(_project, value))
            {
                return;
            }

            DetachProject(_project);
            _project = value;
            AttachProject(_project);
            BuildMediaView();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProjectTitle));
            OnPropertyChanged(nameof(TimelineDurationLabel));
            OnPropertyChanged(nameof(CanExport));
        }
    }

    public ICollectionView MediaView => _mediaView;

    public TimelineClip? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (SetProperty(ref _selectedClip, value))
            {
                OnPropertyChanged(nameof(SelectedClipAsset));
                OnPropertyChanged(nameof(SelectedClipName));
                OnPropertyChanged(nameof(SelectedClipTrackLabel));
                OnPropertyChanged(nameof(IsSelectedClipLinked));
                OnPropertyChanged(nameof(HasSelectedClip));
            }
        }
    }

    public MediaAsset? SelectedAsset
    {
        get => _selectedAsset;
        set => SetProperty(ref _selectedAsset, value);
    }

    public MediaAsset? SelectedClipAsset => SelectedClip is null ? null : Project.FindAsset(SelectedClip.AssetId);
    public string SelectedClipName => SelectedClipAsset?.Name ?? "Клип не выбран";
    public string SelectedClipTrackLabel => SelectedClip is null
        ? string.Empty
        : $"{(SelectedClip.Track == TrackKind.Visual ? "Видео" : "Аудио")} • дорожка {SelectedClip.TrackIndex + 1}";
    public bool IsSelectedClipLinked => SelectedClip?.LinkGroupId is Guid groupId &&
                                        Project.Clips.Count(clip => clip.LinkGroupId == groupId) > 1;
    public bool HasSelectedClip => SelectedClip is not null;
    public bool CanExport => Project.GetVisualClips().Count > 0 && !IsBusy;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                MediaView.Refresh();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(ProjectTitle));
            }
        }
    }

    public string ProjectTitle => $"{Project.Name}{(IsDirty ? " •" : string.Empty)}";

    public double Playhead
    {
        get => _playhead;
        set
        {
            var bounded = Math.Clamp(value, 0, Math.Max(0, Project.TimelineDisplayDuration));
            if (SetProperty(ref _playhead, bounded))
            {
                OnPropertyChanged(nameof(PlayheadLabel));
            }
        }
    }

    public string PlayheadLabel => FormatTime(Playhead);
    public string TimelineDurationLabel => FormatTime(Project.Duration);
    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public bool HasAutosave => _projectService.AutosaveExists;
    public bool HasPendingEditReview => _editReviewSnapshot is not null;

    public async Task<IReadOnlyList<string>> ImportFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var uniquePaths = filePaths
            .Select(Path.GetFullPath)
            .Where(path => Project.Media.All(asset => !asset.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniquePaths.Count == 0)
        {
            StatusText = "Выбранные файлы уже находятся в медиатеке";
            return Array.Empty<string>();
        }

        BeginEdit();
        IsBusy = true;
        var errors = new List<string>();
        try
        {
            for (var index = 0; index < uniquePaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = uniquePaths[index];
                StatusText = $"Импорт {index + 1} из {uniquePaths.Count}: {Path.GetFileName(path)}";
                try
                {
                    var asset = await MediaProbeService.ProbeAsync(path, cancellationToken);
                    asset.ThumbnailPath = await ThumbnailService.CreateAsync(asset, cancellationToken);
                    Project.Media.Add(asset);
                    PrepareTimelineMedia(asset);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{Path.GetFileName(path)} — {exception.Message}");
                }
            }

            CommitEdit();
            StatusText = errors.Count == 0
                ? $"Импортировано файлов: {uniquePaths.Count}"
                : $"Импорт завершён с ошибками: {errors.Count}";
            return errors;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddAssetToTimeline(
        Guid assetId,
        double? requestedStart = null,
        TrackKind? requestedTrack = null,
        int requestedTrackIndex = 0)
    {
        var asset = Project.FindAsset(assetId);
        if (asset is null || asset.IsMissing)
        {
            return;
        }

        CaptureUndoPoint();
        var clip = new TimelineClip
        {
            AssetId = asset.Id,
            Track = asset.Kind == MediaKind.Audio ? TrackKind.Audio : TrackKind.Visual,
            TrackIndex = Math.Max(0, requestedTrackIndex),
            SourceStart = 0,
            Duration = asset.Kind == MediaKind.Image ? 5 : Math.Max(0.1, asset.Duration),
            Volume = 1
        };

        var isFirstVisual = clip.Track == TrackKind.Visual && Project.GetVisualClips().Count == 0;
        var isFirstAudio = clip.Track == TrackKind.Audio && Project.GetAudioClips().Count == 0;
        var desiredStart = isFirstVisual || isFirstAudio
            ? 0
            : Math.Max(
                0,
                requestedStart ?? Project.GetTrackClips(clip.Track, clip.TrackIndex).Select(item => item.End).DefaultIfEmpty(0).Max());
        clip.Start = FindAvailableTrackStart(clip.Track, clip.TrackIndex, desiredStart, clip.Duration);

        if (asset.Kind == MediaKind.Video && asset.HasAudio)
        {
            var linkGroupId = Guid.NewGuid();
            clip.LinkGroupId = linkGroupId;
            var audioTrackIndex = FindAvailableTrackIndex(TrackKind.Audio, clip.Start, clip.Duration, 0);
            Project.Clips.Add(new TimelineClip
            {
                AssetId = asset.Id,
                Track = TrackKind.Audio,
                TrackIndex = audioTrackIndex,
                LinkGroupId = linkGroupId,
                Start = clip.Start,
                SourceStart = clip.SourceStart,
                Duration = clip.Duration,
                Volume = 1
            });
        }
        Project.Clips.Add(clip);

        SubscribeClip(clip);
        SelectedClip = clip;
        CommitChange("Клип добавлен на таймлайн");
    }

    public void DeleteSelectedClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        CaptureUndoPoint();
        var clipsToDelete = SelectedClip.LinkGroupId is Guid groupId
            ? Project.Clips.Where(clip => clip.LinkGroupId == groupId).ToList()
            : [SelectedClip];
        foreach (var clip in clipsToDelete)
        {
            Project.Clips.Remove(clip);
        }
        SelectedClip = null;

        CommitChange("Клип удалён");
    }

    public bool SplitSelectedAtPlayhead()
    {
        var clip = SelectedClip;
        if (clip is null || Playhead <= clip.Start + 0.1 || Playhead >= clip.End - 0.1)
        {
            return false;
        }

        CaptureUndoPoint();
        var linkedClips = clip.LinkGroupId is Guid linkGroupId
            ? Project.Clips.Where(item => item.LinkGroupId == linkGroupId && Playhead > item.Start + 0.1 && Playhead < item.End - 0.1).ToList()
            : [clip];
        var rightLinkGroup = linkedClips.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        TimelineClip? selectedRight = null;
        foreach (var linkedClip in linkedClips)
        {
            var firstDuration = Playhead - linkedClip.Start;
            var second = linkedClip.Clone();
            second.Id = Guid.NewGuid();
            second.LinkGroupId = rightLinkGroup;
            second.Start = Playhead;
            second.SourceStart = linkedClip.SourceStart + firstDuration;
            second.Duration = linkedClip.Duration - firstDuration;
            linkedClip.Duration = firstDuration;
            Project.Clips.Add(second);
            if (linkedClip.Id == clip.Id)
            {
                selectedRight = second;
            }
        }

        SelectedClip = selectedRight;
        CommitChange("Клип разделён");
        return true;
    }

    public bool UnlinkSelectedClip()
    {
        if (SelectedClip?.LinkGroupId is not Guid groupId)
        {
            return false;
        }

        var linked = Project.Clips.Where(clip => clip.LinkGroupId == groupId).ToList();
        if (linked.Count < 2)
        {
            SelectedClip.LinkGroupId = null;
            return false;
        }

        BeginEdit();
        foreach (var clip in linked)
        {
            clip.LinkGroupId = null;
        }
        CommitEdit("Связь видео и звука разорвана");
        OnPropertyChanged(nameof(IsSelectedClipLinked));
        return true;
    }

    public void NormalizeSelectedClip()
    {
        var clip = SelectedClip;
        if (clip is null)
        {
            return;
        }

        var asset = Project.FindAsset(clip.AssetId);
        if (asset is null)
        {
            return;
        }

        var maximumSourceStart = asset.Kind == MediaKind.Image ? 0 : Math.Max(0, asset.Duration - 0.1);
        clip.SourceStart = Math.Clamp(clip.SourceStart, 0, maximumSourceStart);
        var maximumDuration = asset.Kind == MediaKind.Image
            ? 3600
            : Math.Max(0.1, asset.Duration - clip.SourceStart);
        var otherClips = Project.GetTrackClips(clip.Track, clip.TrackIndex).Where(item => item.Id != clip.Id).ToList();
        var previousEnd = otherClips
            .Where(item => item.Start < clip.Start)
            .Select(item => item.End)
            .DefaultIfEmpty(0)
            .Max();
        var nextStart = otherClips
            .Where(item => item.Start >= clip.Start)
            .Select(item => item.Start)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min();
        clip.Start = Math.Max(previousEnd, clip.Start);
        if (!double.IsPositiveInfinity(nextStart))
        {
            maximumDuration = Math.Min(maximumDuration, Math.Max(0.1, nextStart - clip.Start));
        }
        clip.Duration = Math.Clamp(clip.Duration, 0.1, maximumDuration);
    }

    public void ReplaceAnalysisMarkers(
        Guid assetId,
        double timelineStart,
        double timelineEnd,
        IEnumerable<TimelineMarker> markers)
    {
        BeginEdit();
        var oldMarkers = Project.Markers
            .Where(marker => marker.AssetId == assetId && marker.End > timelineStart && marker.Start < timelineEnd)
            .ToList();
        foreach (var marker in oldMarkers)
        {
            Project.Markers.Remove(marker);
        }

        foreach (var marker in markers.OrderBy(marker => marker.Start))
        {
            Project.Markers.Add(marker);
        }
        CommitEdit("Анализ добавлен на таймлайн");
    }

    public void ClearAnalysisMarkers()
    {
        if (Project.Markers.Count == 0)
        {
            return;
        }

        BeginEdit();
        Project.Markers.Clear();
        CommitEdit("Метки анализа удалены");
    }

    public int BeginEditPlanReview(EditCommandPlan plan)
    {
        if (plan.Commands.Count == 0)
        {
            return 0;
        }

        if (_editReviewSnapshot is not null)
        {
            throw new InvalidOperationException("Сначала примите или отмените предыдущий черновик монтажа.");
        }

        _editReviewSnapshot = _projectService.CreateSnapshot(Project);
        _editReviewReason = plan.Summary;
        _editReviewSelectedClipId = SelectedClip?.Id;
        _editReviewPlayhead = Playhead;
        _editReviewWasDirty = IsDirty;
        BeginEdit();
        var completed = 0;
        foreach (var command in plan.Commands)
        {
            completed += command.Type switch
            {
                EditCommandType.DeleteRange => DeleteTimelineRange(command.Start, command.End),
                EditCommandType.SplitAt => SplitAllAt(command.Start),
                EditCommandType.DeleteSelected => DeleteSelectedWithoutHistory(),
                _ => 0
            };
        }

        if (completed == 0)
        {
            RejectEditPlanReview();
            return 0;
        }

        StatusText = completed == 1
            ? "Черновик ИИ готов — проверьте и примите или верните изменения"
            : $"Черновик ИИ готов: {completed} команд — проверьте результат";
        OnPropertyChanged(nameof(HasPendingEditReview));
        OnPropertyChanged(nameof(TimelineDurationLabel));
        OnPropertyChanged(nameof(CanExport));
        return completed;
    }

    public void AcceptEditPlanReview()
    {
        if (_editReviewSnapshot is null)
        {
            return;
        }

        ProjectHistoryService.CreateCheckpoint(
            Project,
            $"До ИИ: {_editReviewReason ?? "команда монтажа"}",
            _editReviewSnapshot);
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        CommitEdit("Изменения ИИ приняты");
        OnPropertyChanged(nameof(HasPendingEditReview));
    }

    public void RejectEditPlanReview()
    {
        if (_editReviewSnapshot is null)
        {
            return;
        }

        var snapshot = _editReviewSnapshot;
        var selectedClipId = _editReviewSelectedClipId;
        var reviewPlayhead = _editReviewPlayhead;
        var wasDirty = _editReviewWasDirty;
        var filePath = Project.FilePath;
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        _pendingEditSnapshot = null;
        CancelAutosave();
        _suppressDirtyTracking = true;
        try
        {
            SelectedClip = null;
            Project = _projectService.RestoreSnapshot(snapshot, filePath);
            SelectedClip = selectedClipId is Guid clipId ? Project.FindClip(clipId) : null;
            Playhead = Math.Min(reviewPlayhead, Project.Duration);
            IsDirty = wasDirty;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        if (IsDirty)
        {
            ScheduleAutosave();
        }
        StatusText = "Черновик ИИ отменён — проект возвращён к исходному состоянию";
        OnPropertyChanged(nameof(HasPendingEditReview));
        NotifyHistoryChanged();
    }

    public ProjectHistoryEntry CreateHistoryCheckpoint(string message)
    {
        var entry = ProjectHistoryService.CreateCheckpoint(Project, message);
        StatusText = $"Создана контрольная точка: {entry.Message}";
        return entry;
    }

    public IReadOnlyList<ProjectHistoryEntry> GetHistoryCheckpoints()
        => ProjectHistoryService.GetCheckpoints(Project);

    public void RestoreHistoryCheckpoint(ProjectHistoryEntry entry)
    {
        if (entry.ProjectId != Project.Id)
        {
            throw new InvalidOperationException("Эта контрольная точка относится к другому проекту.");
        }
        if (HasPendingEditReview)
        {
            throw new InvalidOperationException("Сначала примите или верните черновик ИИ.");
        }

        var currentSnapshot = _projectService.CreateSnapshot(Project);
        ProjectHistoryService.CreateCheckpoint(Project, $"Авто: перед откатом к «{entry.Message}»", currentSnapshot);
        _undoStack.Push(currentSnapshot);
        TrimHistory(_undoStack);
        _redoStack.Clear();

        var filePath = Project.FilePath;
        _suppressDirtyTracking = true;
        try
        {
            SelectedClip = null;
            Project = ProjectHistoryService.RestoreCheckpoint(entry, filePath);
            Playhead = Math.Min(Playhead, Project.Duration);
            IsDirty = true;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        StatusText = $"Проект восстановлен: {entry.Message}";
        ScheduleAutosave();
        NotifyHistoryChanged();
    }

    public void DeleteHistoryCheckpoint(ProjectHistoryEntry entry)
    {
        if (entry.ProjectId != Project.Id)
        {
            return;
        }
        ProjectHistoryService.DeleteCheckpoint(entry);
        StatusText = $"Контрольная точка удалена: {entry.Message}";
    }

    private int DeleteTimelineRange(double requestedStart, double requestedEnd)
    {
        var start = Math.Clamp(requestedStart, 0, Project.Duration);
        var end = Math.Clamp(requestedEnd, 0, Project.Duration);
        if (end <= start + 0.01)
        {
            return 0;
        }

        var removedDuration = end - start;
        var affected = 0;
        var tailLinkGroups = new Dictionary<Guid, Guid>();
        foreach (var clip in Project.Clips.ToList())
        {
            var oldStart = clip.Start;
            var oldEnd = clip.End;
            if (oldEnd <= start)
            {
                continue;
            }

            if (oldStart >= end)
            {
                clip.Start = oldStart - removedDuration;
                continue;
            }

            affected++;
            if (oldStart >= start && oldEnd <= end)
            {
                if (ReferenceEquals(SelectedClip, clip))
                {
                    SelectedClip = null;
                }
                Project.Clips.Remove(clip);
                continue;
            }

            if (oldStart < start && oldEnd > end)
            {
                var asset = Project.FindAsset(clip.AssetId);
                var tail = clip.Clone();
                tail.Id = Guid.NewGuid();
                if (clip.LinkGroupId is Guid oldGroup)
                {
                    if (!tailLinkGroups.TryGetValue(oldGroup, out var rightGroup))
                    {
                        rightGroup = Guid.NewGuid();
                        tailLinkGroups[oldGroup] = rightGroup;
                    }
                    tail.LinkGroupId = rightGroup;
                }
                tail.Start = start;
                tail.SourceStart = asset?.Kind == MediaKind.Image
                    ? clip.SourceStart
                    : clip.SourceStart + (end - oldStart);
                tail.Duration = oldEnd - end;
                clip.Duration = start - oldStart;
                Project.Clips.Add(tail);
                continue;
            }

            if (oldStart < start)
            {
                clip.Duration = start - oldStart;
                continue;
            }

            var sourceTrim = end - oldStart;
            if (Project.FindAsset(clip.AssetId)?.Kind != MediaKind.Image)
            {
                clip.SourceStart += sourceTrim;
            }
            clip.Start = start;
            clip.Duration = oldEnd - end;
        }

        foreach (var marker in Project.Markers.ToList())
        {
            var oldStart = marker.Start;
            var oldEnd = marker.End;
            if (oldEnd <= start)
            {
                continue;
            }

            if (oldStart >= end)
            {
                marker.Start = oldStart - removedDuration;
            }
            else if (oldStart >= start && oldEnd <= end)
            {
                Project.Markers.Remove(marker);
            }
            else if (oldStart < start && oldEnd > end)
            {
                marker.Duration -= removedDuration;
            }
            else if (oldStart < start)
            {
                marker.Duration = start - oldStart;
            }
            else
            {
                marker.SourceStart += end - oldStart;
                marker.Start = start;
                marker.Duration = oldEnd - end;
            }
        }

        Playhead = Math.Min(Playhead, Project.Duration);
        return affected > 0 ? 1 : 0;
    }

    private int SplitAllAt(double requestedTime)
    {
        var time = Math.Clamp(requestedTime, 0, Project.Duration);
        var targets = Project.Clips
            .Where(clip => time > clip.Start + 0.05 && time < clip.End - 0.05)
            .ToList();
        var rightLinkGroups = targets
            .Where(clip => clip.LinkGroupId.HasValue)
            .Select(clip => clip.LinkGroupId!.Value)
            .Distinct()
            .ToDictionary(groupId => groupId, _ => Guid.NewGuid());
        foreach (var clip in targets)
        {
            var firstDuration = time - clip.Start;
            var second = clip.Clone();
            second.Id = Guid.NewGuid();
            second.LinkGroupId = clip.LinkGroupId is Guid oldGroup ? rightLinkGroups[oldGroup] : null;
            second.Start = time;
            second.SourceStart = Project.FindAsset(clip.AssetId)?.Kind == MediaKind.Image
                ? clip.SourceStart
                : clip.SourceStart + firstDuration;
            second.Duration = clip.Duration - firstDuration;
            clip.Duration = firstDuration;
            Project.Clips.Add(second);
            SelectedClip = second;
        }

        Playhead = time;
        return targets.Count > 0 ? 1 : 0;
    }

    private int DeleteSelectedWithoutHistory()
    {
        if (SelectedClip is null)
        {
            return 0;
        }

        var clipsToDelete = SelectedClip.LinkGroupId is Guid groupId
            ? Project.Clips.Where(clip => clip.LinkGroupId == groupId).ToList()
            : [SelectedClip];
        foreach (var clip in clipsToDelete)
        {
            Project.Clips.Remove(clip);
        }
        SelectedClip = null;
        return 1;
    }

    private double FindAvailableTrackStart(TrackKind kind, int trackIndex, double requestedStart, double duration)
    {
        var candidate = Math.Max(0, requestedStart);
        foreach (var clip in Project.GetTrackClips(kind, trackIndex))
        {
            if (candidate + duration <= clip.Start + 0.0001)
            {
                break;
            }

            if (candidate < clip.End - 0.0001)
            {
                candidate = clip.End;
            }
        }

        return candidate;
    }

    private int FindAvailableTrackIndex(TrackKind kind, double start, double duration, int preferredIndex)
    {
        var maximum = kind == TrackKind.Visual ? Project.VisualTrackCount : Project.AudioTrackCount;
        foreach (var index in Enumerable.Range(Math.Max(0, preferredIndex), maximum + 1))
        {
            var occupied = Project.GetTrackClips(kind, index)
                .Any(clip => start < clip.End - 0.0001 && start + duration > clip.Start + 0.0001);
            if (!occupied)
            {
                return index;
            }
        }
        return maximum;
    }

    public void BeginEdit() => _pendingEditSnapshot ??= _projectService.CreateSnapshot(Project);

    public void CommitEdit(string status = "Изменения сохранены в проекте")
    {
        if (_pendingEditSnapshot is null)
        {
            return;
        }

        var current = _projectService.CreateSnapshot(Project);
        if (!string.Equals(_pendingEditSnapshot, current, StringComparison.Ordinal))
        {
            _undoStack.Push(_pendingEditSnapshot);
            TrimHistory(_undoStack);
            _redoStack.Clear();
            MarkChanged();
            StatusText = status;
        }

        _pendingEditSnapshot = null;
        NotifyHistoryChanged();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var current = _projectService.CreateSnapshot(Project);
        _redoStack.Push(current);
        RestoreFromHistory(_undoStack.Pop());
        StatusText = "Изменение отменено";
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        var current = _projectService.CreateSnapshot(Project);
        _undoStack.Push(current);
        RestoreFromHistory(_redoStack.Pop());
        StatusText = "Изменение повторено";
    }

    public void NewProject()
    {
        CancelAutosave();
        _projectService.DeleteAutosave();
        _undoStack.Clear();
        _redoStack.Clear();
        _pendingEditSnapshot = null;
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        SelectedClip = null;
        SelectedAsset = null;
        Playhead = 0;
        Project = EditorProject.CreateNew();
        IsDirty = false;
        StatusText = "Создан новый проект";
        NotifyHistoryChanged();
    }

    public async Task OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            CancelAutosave();
            _projectService.DeleteAutosave();
            var project = await _projectService.OpenAsync(path, cancellationToken);
            _undoStack.Clear();
            _redoStack.Clear();
            _pendingEditSnapshot = null;
            _editReviewSnapshot = null;
            _editReviewReason = null;
            _editReviewSelectedClipId = null;
            SelectedClip = null;
            SelectedAsset = null;
            Playhead = 0;
            Project = project;
            IsDirty = false;
            StatusText = $"Открыт проект: {Path.GetFileName(path)}";
            NotifyHistoryChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SaveProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            CancelAutosave();
            await _projectService.SaveAsync(Project, path, cancellationToken);
            IsDirty = false;
            StatusText = $"Проект сохранён: {Path.GetFileName(path)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RecoverAutosaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_projectService.AutosaveExists)
        {
            return;
        }

        CancelAutosave();
        var project = await _projectService.OpenAutosaveAsync(cancellationToken);
        _undoStack.Clear();
        _redoStack.Clear();
        SelectedClip = null;
        SelectedAsset = null;
        Playhead = 0;
        Project = project;
        IsDirty = true;
        StatusText = "Несохранённый проект восстановлен";
        NotifyHistoryChanged();
    }

    public void DiscardAutosave()
    {
        CancelAutosave();
        _projectService.DeleteAutosave();
    }

    public void MarkChanged()
    {
        if (_suppressDirtyTracking)
        {
            return;
        }

        IsDirty = true;
        Project.UpdatedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(TimelineDurationLabel));
        OnPropertyChanged(nameof(CanExport));
        ScheduleAutosave();
    }

    public void Dispose()
    {
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        OllamaVideoAnalysisService.Dispose();
        DetachProject(Project);
    }

    private void BuildMediaView()
    {
        _mediaView = CollectionViewSource.GetDefaultView(Project.Media);
        _mediaView.Filter = item =>
        {
            if (item is not MediaAsset asset || string.IsNullOrWhiteSpace(SearchText))
            {
                return true;
            }

            return asset.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);
        };
        OnPropertyChanged(nameof(MediaView));
    }

    private void CaptureUndoPoint()
    {
        _undoStack.Push(_projectService.CreateSnapshot(Project));
        TrimHistory(_undoStack);
        _redoStack.Clear();
        NotifyHistoryChanged();
    }

    private void CommitChange(string status)
    {
        MarkChanged();
        StatusText = status;
        NotifyHistoryChanged();
    }

    private void RestoreFromHistory(string snapshot)
    {
        var filePath = Project.FilePath;
        _suppressDirtyTracking = true;
        try
        {
            SelectedClip = null;
            Project = _projectService.RestoreSnapshot(snapshot, filePath);
            Playhead = Math.Min(Playhead, Project.Duration);
            IsDirty = true;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        ScheduleAutosave();
        NotifyHistoryChanged();
    }

    private void AttachProject(EditorProject project)
    {
        project.PropertyChanged += OnProjectPropertyChanged;
        project.Clips.CollectionChanged += OnClipsCollectionChanged;
        project.TextOverlays.CollectionChanged += OnTextOverlaysCollectionChanged;
        foreach (var clip in project.Clips)
        {
            SubscribeClip(clip);
        }
        foreach (var overlay in project.TextOverlays)
        {
            SubscribeTextOverlay(overlay);
        }
        foreach (var asset in project.Media)
        {
            PrepareTimelineMedia(asset);
        }
    }

    private async void PrepareTimelineMedia(MediaAsset asset)
    {
        try
        {
            await TimelineMediaCacheService.PrepareAsync(asset);
        }
        catch
        {
            // Монтаж остаётся доступным, даже если FFmpeg не смог построить визуальный кэш дорожки.
        }
    }

    private void DetachProject(EditorProject project)
    {
        project.PropertyChanged -= OnProjectPropertyChanged;
        project.Clips.CollectionChanged -= OnClipsCollectionChanged;
        project.TextOverlays.CollectionChanged -= OnTextOverlaysCollectionChanged;
        foreach (var clip in _subscribedClips.ToList())
        {
            clip.PropertyChanged -= OnClipPropertyChanged;
        }
        _subscribedClips.Clear();
        foreach (var overlay in _subscribedTextOverlays.ToList())
        {
            overlay.PropertyChanged -= OnTextOverlayPropertyChanged;
        }
        _subscribedTextOverlays.Clear();
    }

    private void SubscribeClip(TimelineClip clip)
    {
        if (_subscribedClips.Contains(clip))
        {
            return;
        }

        _subscribedClips.Add(clip);
        clip.PropertyChanged += OnClipPropertyChanged;
    }

    private void OnClipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var visualChanged = false;
        var audioChanged = false;
        if (e.OldItems is not null)
        {
            foreach (TimelineClip clip in e.OldItems)
            {
                visualChanged |= clip.Track == TrackKind.Visual;
                audioChanged |= clip.Track == TrackKind.Audio;
                clip.PropertyChanged -= OnClipPropertyChanged;
                _subscribedClips.Remove(clip);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (TimelineClip clip in e.NewItems)
            {
                visualChanged |= clip.Track == TrackKind.Visual;
                audioChanged |= clip.Track == TrackKind.Audio;
                SubscribeClip(clip);
            }
        }

        if (visualChanged) Project.InvalidatePreview(TrackKind.Visual);
        if (audioChanged) Project.InvalidatePreview(TrackKind.Audio);

        OnPropertyChanged(nameof(TimelineDurationLabel));
        OnPropertyChanged(nameof(CanExport));
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TimelineClip clip) Project.InvalidatePreview(clip.Track);
        OnPropertyChanged(nameof(TimelineDurationLabel));
        if (ReferenceEquals(sender, SelectedClip))
        {
            OnPropertyChanged(nameof(SelectedClip));
            OnPropertyChanged(nameof(SelectedClipTrackLabel));
        }

        MarkChanged();
    }

    private void SubscribeTextOverlay(TextOverlay overlay)
    {
        if (_subscribedTextOverlays.Contains(overlay))
        {
            return;
        }
        _subscribedTextOverlays.Add(overlay);
        overlay.PropertyChanged += OnTextOverlayPropertyChanged;
    }

    private void OnTextOverlaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TextOverlay overlay in e.OldItems)
            {
                overlay.PropertyChanged -= OnTextOverlayPropertyChanged;
                _subscribedTextOverlays.Remove(overlay);
            }
        }
        if (e.NewItems is not null)
        {
            foreach (TextOverlay overlay in e.NewItems)
            {
                SubscribeTextOverlay(overlay);
            }
        }
        OnPropertyChanged(nameof(TimelineDurationLabel));
        MarkChanged();
    }

    private void OnTextOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TimelineDurationLabel));
        MarkChanged();
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorProject.CanvasWidth) or nameof(EditorProject.CanvasHeight) or nameof(EditorProject.FrameRate))
            Project.InvalidatePreview(TrackKind.Visual);
        if (e.PropertyName == nameof(EditorProject.Name))
        {
            OnPropertyChanged(nameof(ProjectTitle));
            MarkChanged();
        }
    }

    private void ScheduleAutosave()
    {
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        _autosaveCancellation = new CancellationTokenSource();
        _ = AutosaveAfterDelayAsync(_autosaveCancellation.Token);
    }

    private void CancelAutosave()
    {
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        _autosaveCancellation = null;
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken);
            await _projectService.SaveAutosaveAsync(Project, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Автосохранение не должно прерывать монтаж. Ручное сохранение сообщит об ошибке явно.
        }
    }

    private static void TrimHistory(Stack<string> stack)
    {
        if (stack.Count <= 50)
        {
            return;
        }

        var newest = stack.Take(50).Reverse().ToArray();
        stack.Clear();
        foreach (var item in newest)
        {
            stack.Push(item);
        }
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private static string FormatTime(double seconds)
        => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss\.f" : @"mm\:ss\.f");
}
