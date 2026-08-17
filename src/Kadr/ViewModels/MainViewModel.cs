using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using KadrStudio.Adapters;
using KadrStudio.Application.Editing;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Media;
using KadrStudio.Application.Storage;
using KadrStudio.Infrastructure.Media;
using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;
using KadrStudio.Services;

namespace KadrStudio.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly FfmpegLocator _ffmpegLocator = new();
    private readonly ProcessRunner _processRunner = new();
    private readonly TimelineRenderCoordinator _renderCoordinator;
    private readonly KadrStudio.Infrastructure.Jobs.BackgroundJobScheduler _automationScheduler;
    private readonly ProjectService _projectService = new();
    private readonly EditorProjectMapper _projectMapper = new();
    private readonly AutomationProposalApplier _automationProposalApplier = new();
    private readonly AutomationProposalValidator _automationProposalValidator = new();
    private readonly IMediaRegistry _mediaRegistry;
    private readonly IArtifactStore _artifactStore;
    private EditorSession _editorSession;
    private readonly List<TimelineClip> _subscribedClips = new();
    private readonly List<TextOverlay> _subscribedTextOverlays = new();
    private CancellationTokenSource? _autosaveCancellation;
    private string _pendingAutosaveReason = "Изменение проекта";
    private EditorProject _project;
    private ICollectionView _mediaView = null!;
    private TimelineClip? _selectedClip;
    private MediaAsset? _selectedAsset;
    private string _searchText = string.Empty;
    private string _statusText = "Готово";
    private bool _isBusy;
    private bool _isDirty;
    private double _playhead;
    private bool _editTransactionActive;
    private KadrStudio.Core.Domain.ProjectState? _editReviewSnapshot;
    private string? _editReviewReason;
    private Guid? _editReviewSelectedClipId;
    private double _editReviewPlayhead;
    private bool _editReviewWasDirty;
    private bool _suppressDirtyTracking;
    private long _timelinePresentationRevision;
    private int _disposeState;

    public MainViewModel()
    {
        _project = EditorProject.CreateNew();
        _editorSession = new EditorSession(_projectMapper.ToCore(_project));
        _artifactStore = new DiskMediaArtifactCache(new ArtifactStoreOptions(
            ThumbnailService.DefaultArtifactRoot(), 8L * 1024 * 1024 * 1024));
        MediaProbeService = new MediaProbeService(_ffmpegLocator, _processRunner);
        _mediaRegistry = new MediaRegistry(MediaProbeService);
        ThumbnailService = new ThumbnailService(_ffmpegLocator, _processRunner, _artifactStore);
        _renderCoordinator = new TimelineRenderCoordinator(_ffmpegLocator);
        TimelineMediaCacheService = new TimelineMediaCacheService(
            _ffmpegLocator, _processRunner, artifacts: _artifactStore);
        ExportService = new ExportService(_ffmpegLocator, _processRunner, _renderCoordinator);
        ProjectHistoryService = new ProjectHistoryService();
        AutoSubtitleService = new AutoSubtitleService(_ffmpegLocator, _processRunner);
        VideoAnalysisService = new VideoAnalysisService(_ffmpegLocator, _processRunner);
        OllamaVideoAnalysisService = new OllamaVideoAnalysisService(_ffmpegLocator, _processRunner);
        _automationScheduler = new KadrStudio.Infrastructure.Jobs.BackgroundJobScheduler();
        AutomationOrchestrator = new AutomationOrchestrator(
            _automationScheduler, VideoAnalysisService, OllamaVideoAnalysisService, AutoSubtitleService);
        AttachProject(_project);
        BuildMediaView();
    }

    public MediaProbeService MediaProbeService { get; }
    public ThumbnailService ThumbnailService { get; }
    public TimelineRenderCoordinator RenderCoordinator => _renderCoordinator;
    public TimelineMediaCacheService TimelineMediaCacheService { get; }
    public ExportService ExportService { get; }
    public ProjectHistoryService ProjectHistoryService { get; }
    public AutoSubtitleService AutoSubtitleService { get; }
    public VideoAnalysisService VideoAnalysisService { get; }
    public OllamaVideoAnalysisService OllamaVideoAnalysisService { get; }
    public AutomationOrchestrator AutomationOrchestrator { get; }
    public IArtifactStore ArtifactStore => _artifactStore;
    public long TimelinePresentationRevision => _timelinePresentationRevision;

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
    public bool CanUndo => _editorSession.CanUndo;
    public bool CanRedo => _editorSession.CanRedo;
    public bool HasPendingEditReview => _editReviewSnapshot is not null;

    public Task<bool> HasAutosaveAsync(CancellationToken cancellationToken = default)
        => _projectService.HasAutosaveAsync(cancellationToken);

    public Task<IReadOnlyList<RecoveryProjectInfo>> ListAutosavesAsync(CancellationToken cancellationToken = default)
        => _projectService.ListAutosavesAsync(cancellationToken);

    public ProjectAutomationSnapshot CaptureAutomationSnapshot()
        => ProposalFactory.Capture(_editorSession.State);

    public bool IsAutomationSnapshotCurrent(ProjectAutomationSnapshot snapshot)
        => snapshot.ProjectId == _editorSession.State.Id && snapshot.BaseRevision == _editorSession.State.Revision;

    public async Task<AutomationApplyResult> ApplyAutomationProposalAsync(
        AutomationProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var validation = _automationProposalValidator.Validate(_editorSession.State, proposal);
        if (!validation.IsValid)
        {
            return new AutomationApplyResult(
                false,
                validation.Errors.Any(item => item.Code == "automation.stale"),
                _editorSession.State,
                string.Join("; ", validation.Errors.Select(item => item.Message)));
        }
        var checkpointSnapshot = _projectService.CreateSnapshot(Project);
        if (proposal.CreateCheckpoint)
            await ProjectHistoryService.CreateCheckpointAsync(
                Project, $"Before: {proposal.Title}", checkpointSnapshot, cancellationToken);
        var result = _automationProposalApplier.Apply(_editorSession, proposal);
        if (!result.Applied) return result;
        _suppressDirtyTracking = true;
        try
        {
            SelectedClip = null;
            Project = _projectMapper.ToUi(result.State, Project.FilePath);
            Playhead = Math.Min(Playhead, Project.Duration);
            IsDirty = true;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }
        ScheduleAutosave();
        StatusText = result.Message;
        NotifyHistoryChanged();
        return result;
    }

    public AutomationProposal CreateSubtitleProposal(
        ProjectAutomationSnapshot snapshot,
        IEnumerable<TextOverlay> overlays,
        string producer)
        => ProposalFactory.ForSubtitles(
            snapshot,
            overlays.Select(item => _projectMapper.ToCoreText(item, snapshot.State)).ToArray(),
            "Auto subtitles",
            $"Created subtitles: {overlays.Count()}",
            producer);

    public AutomationProposal CreateAnalysisProposal(
        ProjectAutomationSnapshot snapshot,
        Guid sourceId,
        double start,
        double end,
        IEnumerable<Models.TimelineMarker> markers,
        string producer)
    {
        var rangeStart = KadrStudio.Core.Domain.TimelineTime.FromSeconds(start);
        var rangeEnd = KadrStudio.Core.Domain.TimelineTime.FromSeconds(end);
        var replacement = snapshot.State.Markers
            .Where(item => item.SourceId != sourceId || item.End <= rangeStart || item.Start >= rangeEnd)
            .Concat(markers.Select(_projectMapper.ToCoreMarker))
            .OrderBy(item => item.Start)
            .ToArray();
        return ProposalFactory.ForMarkers(
            snapshot, replacement, "Video analysis", $"Created analysis markers: {replacement.Length}", producer);
    }

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

        IsBusy = true;
        var errors = new List<string>();
        var imported = new List<MediaAsset>();
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
                    imported.Add(asset);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"{Path.GetFileName(path)} — {exception.Message}");
                }
            }

            if (imported.Count > 0)
            {
                var result = _editorSession.Execute(new EditTransaction(
                    "Импорт медиа",
                    new AddSourcesCommand(imported.Select(_projectMapper.ToCoreSource).ToArray())));
                RestoreFromCoreState(result.State);
                foreach (var importedAsset in imported)
                {
                    var restored = Project.FindAsset(importedAsset.Id);
                    if (restored is null) continue;
                    restored.ThumbnailPath = importedAsset.ThumbnailPath;
                    restored.ProbeResult = importedAsset.ProbeResult;
                    _ = PrepareTimelineMediaAsync(restored);
                }
            }
            StatusText = errors.Count == 0
                ? $"Импортировано файлов: {imported.Count}"
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

        var additions = new List<(KadrStudio.Core.Domain.TrackKind Kind, int Index, KadrStudio.Core.Domain.MediaClip Clip)>();
        if (asset.Kind == MediaKind.Video && asset.HasAudio)
        {
            var linkGroupId = Guid.NewGuid();
            clip.LinkGroupId = linkGroupId;
            var audioTrackIndex = FindAvailableTrackIndex(TrackKind.Audio, clip.Start, clip.Duration, 0);
            additions.Add((KadrStudio.Core.Domain.TrackKind.Audio, audioTrackIndex,
                CreateCoreClip(asset.Id, Guid.NewGuid(), clip.Start, clip.SourceStart, clip.Duration,
                    linkGroupId, video: false)));
        }
        additions.Add((clip.Track == TrackKind.Visual
                ? KadrStudio.Core.Domain.TrackKind.Visual
                : KadrStudio.Core.Domain.TrackKind.Audio,
            clip.TrackIndex,
            CreateCoreClip(asset.Id, clip.Id, clip.Start, clip.SourceStart, clip.Duration,
                clip.LinkGroupId, clip.Track == TrackKind.Visual)));
        ExecuteCoreCommand("Клип добавлен на таймлайн",
            new EnsureTrackAndAddMediaClipsCommand(additions), clip.Id);
    }

    public void RefreshMediaOnlineState()
    {
        var online = _editorSession.State.Sources.ToDictionary(pair => pair.Key, pair => File.Exists(pair.Value.Path));
        ExecuteCoreCommand("Media availability refreshed", new RefreshMediaOnlineStateCommand(online));
    }

    public async Task<RelinkCandidate> RelinkMediaAsync(
        Guid sourceId,
        string candidatePath,
        bool verifyContent = true,
        CancellationToken cancellationToken = default)
    {
        if (!_editorSession.State.Sources.TryGetValue(sourceId, out var source))
            throw new KeyNotFoundException($"Media source {sourceId} was not found.");
        var candidate = await _mediaRegistry.ValidateRelinkAsync(
            source, candidatePath, verifyContent, cancellationToken);
        if (candidate.CanApply)
            ExecuteCoreCommand("Media relinked", new RelinkSourcesCommand([candidate]));
        return candidate;
    }

    public async Task<IReadOnlyList<RelinkCandidate>> FindAndRelinkMissingMediaAsync(
        IEnumerable<string> searchRoots,
        CancellationToken cancellationToken = default)
    {
        var candidates = await _mediaRegistry.FindRelinkCandidatesAsync(
            _editorSession.State, searchRoots, cancellationToken);
        if (!candidates.IsDefaultOrEmpty)
            ExecuteCoreCommand("Missing media relinked", new RelinkSourcesCommand(candidates));
        return candidates;
    }

    public void DeleteSelectedClip()
    {
        if (SelectedClip is null)
        {
            return;
        }

        ExecuteCoreCommand("Клип удалён",
            new DeleteMediaClipsCommand(new HashSet<Guid> { SelectedClip.Id }, IncludeLinked: true));
    }

    public bool SplitSelectedAtPlayhead()
    {
        var clip = SelectedClip;
        if (clip is null || Playhead <= clip.Start + 0.1 || Playhead >= clip.End - 0.1)
        {
            return false;
        }

        var rightId = Guid.NewGuid();
        return ExecuteCoreCommand("Клип разделён",
            new SplitSelectedMediaClipCommand(clip.Id,
                KadrStudio.Core.Domain.TimelineTime.FromSeconds(Playhead), rightId), rightId);
    }

    public bool UnlinkSelectedClip()
    {
        if (SelectedClip?.LinkGroupId is not Guid groupId)
        {
            return false;
        }

        if (Project.Clips.Count(clip => clip.LinkGroupId == groupId) < 2) return false;
        var selectedId = SelectedClip.Id;
        var changed = ExecuteCoreCommand("Связь видео и звука разорвана",
            new UnlinkMediaClipCommand(selectedId), selectedId);
        OnPropertyChanged(nameof(IsSelectedClipLinked));
        return changed;
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

    public void ClearAnalysisMarkers()
    {
        if (Project.Markers.Count == 0)
        {
            return;
        }

        ExecuteCoreCommand("Метки анализа удалены", new ReplaceMarkersCommand([]));
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

        _editReviewSnapshot = _editorSession.State;
        _editReviewReason = plan.Summary;
        _editReviewSelectedClipId = SelectedClip?.Id;
        _editReviewPlayhead = Playhead;
        _editReviewWasDirty = IsDirty;
        var commands = new List<IEditCommand>();
        foreach (var command in plan.Commands)
        {
            switch (command.Type)
            {
                case EditCommandType.DeleteRange:
                    var start = KadrStudio.Core.Domain.TimelineTime.FromSeconds(Math.Max(0, command.Start));
                    var end = KadrStudio.Core.Domain.TimelineTime.FromSeconds(Math.Max(0, command.End));
                    if (end > start)
                        commands.Add(new RippleDeleteRangeCommand(new KadrStudio.Core.Domain.TimeRange(start, end - start)));
                    break;
                case EditCommandType.SplitAt:
                    commands.Add(new SplitMediaClipsCommand(
                        KadrStudio.Core.Domain.TimelineTime.FromSeconds(Math.Max(0, command.Start))));
                    break;
                case EditCommandType.DeleteSelected when SelectedClip is not null:
                    commands.Add(new DeleteMediaClipsCommand(
                        new HashSet<Guid> { SelectedClip.Id }, IncludeLinked: true));
                    break;
            }
        }

        if (commands.Count == 0)
        {
            RejectEditPlanReview();
            return 0;
        }
        var result = _editorSession.Execute(new EditTransaction(
            $"AI draft: {plan.Summary}", commands, CreateCheckpoint: true, CheckpointName: $"Before AI: {plan.Summary}"));
        if (!result.Changed)
        {
            _editReviewSnapshot = null;
            return 0;
        }
        RestoreFromCoreState(result.State, SelectedClip?.Id);
        var completed = commands.Count;

        StatusText = completed == 1
            ? "Черновик ИИ готов — проверьте и примите или верните изменения"
            : $"Черновик ИИ готов: {completed} команд — проверьте результат";
        OnPropertyChanged(nameof(HasPendingEditReview));
        OnPropertyChanged(nameof(TimelineDurationLabel));
        OnPropertyChanged(nameof(CanExport));
        return completed;
    }

    public async Task AcceptEditPlanReviewAsync(CancellationToken cancellationToken = default)
    {
        if (_editReviewSnapshot is null)
        {
            return;
        }

        await ProjectHistoryService.CreateCheckpointAsync(
            Project,
            $"До ИИ: {_editReviewReason ?? "команда монтажа"}",
            _projectService.CreateSnapshot(_projectMapper.ToUi(_editReviewSnapshot, Project.FilePath)),
            cancellationToken);
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        MarkChanged();
        StatusText = "Изменения ИИ приняты";
        NotifyHistoryChanged();
        OnPropertyChanged(nameof(HasPendingEditReview));
    }

    public void RejectEditPlanReview()
    {
        if (_editReviewSnapshot is null)
        {
            return;
        }

        var selectedClipId = _editReviewSelectedClipId;
        var reviewPlayhead = _editReviewPlayhead;
        var wasDirty = _editReviewWasDirty;
        var filePath = Project.FilePath;
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        _editTransactionActive = false;
        CancelAutosave();
        _suppressDirtyTracking = true;
        try
        {
            _editorSession.RollbackLatestTransaction();
            SelectedClip = null;
            Project = _projectMapper.ToUi(_editorSession.State, filePath);
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

    public async Task<ProjectHistoryEntry> CreateHistoryCheckpointAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var entry = await ProjectHistoryService.CreateCheckpointAsync(Project, message, cancellationToken: cancellationToken);
        StatusText = $"Создана контрольная точка: {entry.Message}";
        return entry;
    }

    public Task<IReadOnlyList<ProjectHistoryEntry>> GetHistoryCheckpointsAsync(CancellationToken cancellationToken = default)
        => ProjectHistoryService.GetCheckpointsAsync(Project, cancellationToken);

    public async Task RestoreHistoryCheckpointAsync(
        ProjectHistoryEntry entry,
        CancellationToken cancellationToken = default)
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
        await ProjectHistoryService.CreateCheckpointAsync(
            Project, $"Авто: перед откатом к «{entry.Message}»", currentSnapshot, cancellationToken);

        var filePath = Project.FilePath;
        var restored = await ProjectHistoryService.RestoreCheckpointAsync(entry, filePath, cancellationToken);
        var restoredCore = _projectMapper.ToCore(restored, _editorSession.State.Revision);
        _editorSession.Execute(new EditTransaction(
            $"Restore checkpoint: {entry.Message}",
            new ReplaceProjectStateCommand(restoredCore, $"Restore checkpoint: {entry.Message}")));
        _suppressDirtyTracking = true;
        try
        {
            SelectedClip = null;
            Project = _projectMapper.ToUi(_editorSession.State, filePath);
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

    public async Task DeleteHistoryCheckpointAsync(
        ProjectHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        if (entry.ProjectId != Project.Id)
        {
            return;
        }
        await ProjectHistoryService.DeleteCheckpointAsync(entry, cancellationToken);
        StatusText = $"Контрольная точка удалена: {entry.Message}";
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

    public void BeginEdit() => _editTransactionActive = true;

    public void CommitEdit(string status = "Изменения сохранены в проекте")
    {
        if (!_editTransactionActive)
        {
            return;
        }

        var candidate = _projectMapper.ToCore(Project, _editorSession.State.Revision);
        if (candidate != _editorSession.State)
        {
            _editorSession.Execute(new EditTransaction(
                status,
                new ReplaceProjectStateCommand(candidate, status)));
            MarkChanged();
            StatusText = status;
        }

        _editTransactionActive = false;
        NotifyHistoryChanged();
    }

    public void Undo()
    {
        if (!_editorSession.Undo())
        {
            return;
        }

        RestoreFromCoreState(_editorSession.State);
        StatusText = "Изменение отменено";
    }

    public void Redo()
    {
        if (!_editorSession.Redo())
        {
            return;
        }

        RestoreFromCoreState(_editorSession.State);
        StatusText = "Изменение повторено";
    }

    public async Task NewProjectAsync(CancellationToken cancellationToken = default)
    {
        CancelAutosave();
        await _projectService.DeleteAutosaveAsync(cancellationToken);
        _editTransactionActive = false;
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        SelectedClip = null;
        SelectedAsset = null;
        Playhead = 0;
        Project = EditorProject.CreateNew();
        _editorSession = new EditorSession(_projectMapper.ToCore(Project));
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
            await _projectService.DeleteAutosaveAsync(cancellationToken);
            var project = await _projectService.OpenAsync(path, cancellationToken);
            _editTransactionActive = false;
            _editReviewSnapshot = null;
            _editReviewReason = null;
            _editReviewSelectedClipId = null;
            SelectedClip = null;
            SelectedAsset = null;
            Playhead = 0;
            Project = project;
            var refreshed = _mediaRegistry.RefreshOnlineState(_projectMapper.ToCore(Project));
            _editorSession = new EditorSession(refreshed);
            Project = _projectMapper.ToUi(refreshed, path);
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
            TimelineMediaCacheService.ConfigureProject(Project);
            IsDirty = false;
            StatusText = $"Проект сохранён: {Path.GetFileName(path)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RecoverAutosaveAsync(
        RecoveryProjectInfo? recovery = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _projectService.HasAutosaveAsync(cancellationToken))
        {
            return;
        }

        CancelAutosave();
        var project = recovery is null
            ? await _projectService.OpenAutosaveAsync(cancellationToken)
            : await _projectService.OpenAutosaveVersionAsync(
                recovery.ProjectId, recovery.RecoveryId, cancellationToken);
        _editTransactionActive = false;
        SelectedClip = null;
        SelectedAsset = null;
        Playhead = 0;
        Project = project;
        var refreshed = _mediaRegistry.RefreshOnlineState(_projectMapper.ToCore(Project));
        _editorSession = new EditorSession(refreshed);
        Project = _projectMapper.ToUi(refreshed, project.FilePath);
        IsDirty = true;
        StatusText = "Несохранённый проект восстановлен";
        NotifyHistoryChanged();
    }

    public async Task DiscardAutosaveAsync(CancellationToken cancellationToken = default)
    {
        CancelAutosave();
        await _projectService.DeleteAutosaveAsync(cancellationToken);
    }

    public Task DiscardAutosaveAsync(RecoveryProjectInfo recovery, CancellationToken cancellationToken = default)
        => _projectService.DeleteAutosaveVersionAsync(
            recovery.ProjectId, recovery.RecoveryId, cancellationToken);

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _autosaveCancellation?.Cancel();
        _autosaveCancellation?.Dispose();
        await _automationScheduler.DisposeAsync();
        await ThumbnailService.DisposeAsync();
        await TimelineMediaCacheService.DisposeAsync();
        await _renderCoordinator.DisposeAsync();
        await _artifactStore.DisposeAsync();
        _projectService.Dispose();
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
        BeginEdit();
        NotifyHistoryChanged();
    }

    private void CommitChange(string status)
    {
        CommitEdit(status);
    }

    private bool ExecuteCoreCommand(string description, IEditCommand command, Guid? selectedClipId = null)
    {
        var result = _editorSession.Execute(new EditTransaction(description, command));
        if (!result.Changed) return false;
        RestoreFromCoreState(result.State, selectedClipId, description);
        StatusText = description;
        return true;
    }

    public bool ApplyTimelineEdit(TimelineEditIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent switch
        {
            MediaTimelineEditIntent media => ApplyMediaTimelineEdit(media),
            TextTimelineEditIntent text => ApplyTextTimelineEdit(text),
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };
    }

    private bool ApplyMediaTimelineEdit(MediaTimelineEditIntent intent)
    {
        var clip = _editorSession.State.FindMediaClip(intent.ClipId)
            ?? throw new EditRejectedException("Клип больше не существует.");
        IEditCommand command = intent.EditOperation switch
        {
            TimelineEditOperation.Move => new MoveMediaClipCommand(
                clip.Id,
                _editorSession.State.Tracks
                    .FirstOrDefault(track => track.Kind == intent.TargetTrackKind &&
                                             track.Index == intent.TargetTrackIndex)?.Id
                ?? throw new EditRejectedException("Целевая дорожка больше не существует."),
                intent.Start),
            TimelineEditOperation.TrimLeft => new TrimMediaClipCommand(
                clip.Id, TrimEdge.Left, intent.Start),
            TimelineEditOperation.TrimRight => new TrimMediaClipCommand(
                clip.Id, TrimEdge.Right, intent.Start + intent.Duration),
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };
        var description = intent.EditOperation == TimelineEditOperation.Move
            ? "Клип перемещён"
            : "Клип обрезан";
        return ExecuteCoreCommand(description, command, clip.Id);
    }

    private bool ApplyTextTimelineEdit(TextTimelineEditIntent intent)
    {
        var text = _editorSession.State.FindTextClip(intent.TextClipId)
            ?? throw new EditRejectedException("Текстовый клип больше не существует.");
        return ExecuteCoreCommand(
            intent.EditOperation == TimelineEditOperation.Move
                ? "Текстовый клип перемещён"
                : "Текстовый клип обрезан",
            new UpsertTextClipCommand(text with { Start = intent.Start, Duration = intent.Duration }));
    }

    public bool AddTextOverlay(TextOverlay overlay, string description = "Текст добавлен")
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return ExecuteCoreCommand(description,
            new AddTextClipsCommand([_projectMapper.ToCoreText(overlay, _editorSession.State)]));
    }

    public bool UpdateTextOverlay(TextOverlay overlay, string description = "Текст изменён")
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return ExecuteCoreCommand(description,
            new UpsertTextClipCommand(_projectMapper.ToCoreText(overlay, _editorSession.State)));
    }

    public bool DeleteTextOverlay(Guid overlayId, string description = "Текст удалён")
        => ExecuteCoreCommand(description, new DeleteTextClipsCommand(new HashSet<Guid> { overlayId }));

    public bool SetInOut(double? inPoint, double? outPoint, string description)
        => ExecuteCoreCommand(description, new SetInOutCommand(
            inPoint is null ? null : KadrStudio.Core.Domain.TimelineTime.FromSeconds(inPoint.Value),
            outPoint is null ? null : KadrStudio.Core.Domain.TimelineTime.FromSeconds(outPoint.Value)));

    public IReadOnlyList<KadrStudio.Core.Domain.TimelineTransition> GetTransitions()
        => _editorSession.State.Transitions;

    public Guid AddTransition(Guid fromClipId, KadrStudio.Core.Domain.TransitionKind kind, double durationSeconds)
    {
        var state = _editorSession.State;
        var from = state.FindMediaClip(fromClipId)
            ?? throw new EditRejectedException("Выбранный клип больше не существует.");
        var track = state.FindTrack(from.TrackId)
            ?? throw new EditRejectedException("Дорожка выбранного клипа не найдена.");
        var to = state.MediaClips
            .Where(item => item.TrackId == from.TrackId && item.Start == from.End)
            .OrderBy(item => item.Id)
            .FirstOrDefault()
            ?? throw new EditRejectedException("Справа должен находиться соседний клип без зазора.");
        if (track.Kind == KadrStudio.Core.Domain.TrackKind.Audio &&
            kind != KadrStudio.Core.Domain.TransitionKind.ConstantPowerAudio ||
            track.Kind == KadrStudio.Core.Domain.TrackKind.Visual &&
            kind == KadrStudio.Core.Domain.TransitionKind.ConstantPowerAudio)
            throw new EditRejectedException("Тип перехода не подходит выбранной дорожке.");
        var duration = KadrStudio.Core.Domain.TimelineTime.FromSeconds(Math.Clamp(durationSeconds, 0.04, 30));
        var half = new KadrStudio.Core.Domain.TimelineTime(duration.Ticks / 2);
        var transition = new KadrStudio.Core.Domain.TimelineTransition(
            Guid.NewGuid(), kind, track.Id, from.Id, to.Id, from.End - half, duration);
        ExecuteCoreCommand("Переход добавлен", new UpsertTransitionCommand(transition), from.Id);
        return transition.Id;
    }

    public bool DeleteTransition(Guid transitionId)
        => ExecuteCoreCommand("Переход удалён",
            new DeleteTransitionsCommand(new HashSet<Guid> { transitionId }), SelectedClip?.Id);

    public Guid? SplitTextOverlay(Guid overlayId, double position)
    {
        var current = _editorSession.State.FindTextClip(overlayId);
        var split = KadrStudio.Core.Domain.TimelineTime.FromSeconds(position);
        if (current is null || split <= current.Start || split >= current.End) return null;
        var rightId = Guid.NewGuid();
        var left = current with { Duration = split - current.Start };
        var right = current with
        {
            Id = rightId,
            Start = split,
            Duration = current.End - split
        };
        return ExecuteCoreCommand("Текстовый клип разделён",
            new EditBatchCommand("Split text", [
                new UpsertTextClipCommand(left),
                new AddTextClipsCommand([right])
            ])) ? rightId : null;
    }

    private static KadrStudio.Core.Domain.MediaClip CreateCoreClip(
        Guid sourceId,
        Guid clipId,
        double start,
        double sourceStart,
        double duration,
        Guid? linkGroupId,
        bool video)
        => new(
            clipId,
            sourceId,
            Guid.Empty,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(start),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(sourceStart),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(duration),
            linkGroupId,
            video ? new KadrStudio.Core.Domain.VideoParameters() : null,
            video ? null : new KadrStudio.Core.Domain.AudioParameters());

    private void RestoreFromCoreState(
        KadrStudio.Core.Domain.ProjectState state,
        Guid? selectedClipId = null,
        string? autosaveReason = null)
    {
        var filePath = Project.FilePath;
        var derivedMedia = Project.Media.ToDictionary(
            asset => asset.Id,
            asset => new DerivedMediaState(
                asset.ThumbnailPath, asset.TimelineFramePaths, asset.Waveform));
        var restored = _projectMapper.ToUi(state, filePath);
        foreach (var asset in restored.Media)
        {
            if (!derivedMedia.TryGetValue(asset.Id, out var derived)) continue;
            asset.ThumbnailPath = derived.ThumbnailPath;
            asset.TimelineFramePaths = derived.TimelineFrames;
            asset.Waveform = derived.Waveform;
        }
        _suppressDirtyTracking = true;
        try
        {
            SelectedClip = null;
            Project = restored;
            SelectedClip = selectedClipId is Guid id ? Project.FindClip(id) : null;
            Playhead = Math.Min(Playhead, Project.Duration);
            IsDirty = true;
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        ScheduleAutosave(autosaveReason);
        NotifyHistoryChanged();
    }

    private sealed record DerivedMediaState(
        string? ThumbnailPath,
        IReadOnlyList<string> TimelineFrames,
        KadrStudio.Application.Caching.WaveformPyramid Waveform);

    private void AttachProject(EditorProject project)
    {
        TimelineMediaCacheService.ConfigureProject(project);
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
            _ = PrepareTimelineMediaAsync(asset);
        }
    }

    private async Task PrepareTimelineMediaAsync(MediaAsset asset)
    {
        try
        {
            await TimelineMediaCacheService.PrepareAsync(asset);
        }
        catch
        {
            // Монтаж остаётся доступным, даже если FFmpeg не смог построить визуальный кэш дорожки.
        }
        finally
        {
            _timelinePresentationRevision++;
            OnPropertyChanged(nameof(TimelinePresentationRevision));
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

    private void ScheduleAutosave(string? reason = null)
    {
        if (!string.IsNullOrWhiteSpace(reason)) _pendingAutosaveReason = reason.Trim();
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
            await _projectService.SaveAutosaveVersionAsync(Project, _pendingAutosaveReason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Автосохранение не должно прерывать монтаж. Ручное сохранение сообщит об ошибке явно.
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
