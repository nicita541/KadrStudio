using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Data;
using KadrStudio.Adapters;
using KadrStudio.Application.Editing;
using KadrStudio.Application.Automation;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.Editing;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;
using KadrStudio.Application.Media;
using KadrStudio.Application.Storage;
using KadrStudio.Infrastructure.Media;
using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;
using KadrStudio.Services;
using KadrStudio.Services.Agent;
using CoreGameEditingProfile = KadrStudio.Core.Domain.GameEditingProfile;
using CoreMediaAnalysisManifest = KadrStudio.Core.Domain.MediaAnalysisManifest;
using CoreMontagePlan = KadrStudio.Core.Domain.MontagePlan;
using CoreMontageRequest = KadrStudio.Core.Domain.MontageRequest;
using CoreSequenceState = KadrStudio.Core.Domain.SequenceState;
using CoreSourceAnnotation = KadrStudio.Core.Domain.SourceAnnotation;

namespace KadrStudio.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly TimelineRenderCoordinator _renderCoordinator;
    private readonly KadrStudio.Infrastructure.Jobs.BackgroundJobScheduler _automationScheduler;
    private readonly ProjectService _projectService;
    private readonly WorkspaceSettingsService _settingsService;
    private readonly ProjectViewMapper _projectMapper = new();
    private readonly AutomationProposalApplier _automationProposalApplier = new();
    private readonly AutomationProposalValidator _automationProposalValidator = new();
    private readonly IMediaRegistry _mediaRegistry;
    private readonly IArtifactStore _artifactStore;
    private EditorSession _editorSession;
    private CancellationTokenSource? _autosaveCancellation;
    private CancellationTokenSource _backgroundAnalysisCancellation = new();
    private readonly object _backgroundAnalysisGate = new();
    private readonly HashSet<Task> _backgroundAnalysisTasks = [];
    private readonly object _timelineMediaPreparationGate = new();
    private readonly Dictionary<TimelineMediaPreparationKey, Task> _timelineMediaPreparationTasks = [];
    private readonly CancellationTokenSource _timelineMediaPreparationCancellation = new();
    private string _pendingAutosaveReason = "Изменение проекта";
    private ProjectViewState _project;
    private ICollectionView _mediaView = null!;
    private TimelineClip? _selectedClip;
    private MediaAsset? _selectedAsset;
    private string _searchText = string.Empty;
    private string _statusText = "Готово";
    private bool _isBusy;
    private bool _isDirty;
    private double _playhead;
    private KadrStudio.Core.Domain.ProjectState? _editReviewSnapshot;
    private string? _editReviewReason;
    private Guid? _editReviewSelectedClipId;
    private double _editReviewPlayhead;
    private bool _editReviewWasDirty;
    private bool _suppressDirtyTracking;
    private long _timelinePresentationRevision;
    private int _agentMutationDepth;
    private int _disposeState;

    public MainViewModel() : this(EditorWorkspaceCompositionRoot.Create())
    {
    }

    public MainViewModel(EditorWorkspaceServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var initialState = KadrStudio.Core.Domain.ProjectState.CreateNew();
        _project = _projectMapper.ToUi(initialState);
        _editorSession = new EditorSession(initialState);
        _projectService = services.ProjectService;
        _settingsService = services.SettingsService;
        _artifactStore = services.ArtifactStore;
        _mediaRegistry = services.MediaRegistry;
        MediaProbeService = services.MediaProbeService;
        ThumbnailService = services.ThumbnailService;
        _renderCoordinator = services.RenderCoordinator;
        TimelineMediaCacheService = services.TimelineMediaCacheService;
        ExportService = services.ExportService;
        ProjectHistoryService = services.ProjectHistoryService;
        AutoSubtitleService = services.AutoSubtitleService;
        VideoAnalysisService = services.VideoAnalysisService;
        AiVideoAnalysisService = services.AiVideoAnalysisService;
        _automationScheduler = services.AutomationScheduler;
        AutomationOrchestrator = new AutomationOrchestrator(
            _automationScheduler, VideoAnalysisService, AiVideoAnalysisService, AutoSubtitleService);
        var recurringSectionFingerprints = new RecurringSectionFingerprintService(
            services.FfmpegLocator, services.ProcessRunner, _artifactStore);
        AiMontageAnalysisService = new AiMontageAnalysisService(
            AutomationOrchestrator, AutoSubtitleService, AiVideoAnalysisService, _artifactStore);
        AiMontageCoordinator = new AiMontageCoordinator(
            AiMontageAnalysisService,
            new AiServerMontagePlanningProvider(AiVideoAnalysisService));

        AgentDebugLog = new FileAgentDebugLog();
        AiAgentOrchestrator = new AiAgentOrchestrator();
        var agentRangeInspector = new AgentMediaRangeInspector(
            AutomationOrchestrator,
            AutoSubtitleService,
            AiVideoAnalysisService,
            _artifactStore);
        AgentReadOnlyToolBackend = new KadrAgentReadOnlyToolBackend(
            () => _editorSession.State,
            agentRangeInspector,
            () =>
            {
                var state = _editorSession.State;
                var active = state.Sequences.First(sequence =>
                    sequence.Id == (state.ActiveSequenceId ?? state.Sequences[0].Id));
                return new AgentEditorContextSnapshot(
                    active.Id,
                    active.Revision,
                    Playhead,
                    SelectedClip?.Id,
                    active.InPoint?.TotalSeconds,
                    active.OutPoint?.TotalSeconds);
            },
            recurringSectionFingerprints);
        AgentToolRegistry = AgentReadOnlyToolSet.Create(AgentReadOnlyToolBackend);
        AgentEditingToolBackend = new KadrAgentEditingToolBackend(
            () => _editorSession.State,
            ExecuteAgentCoreCommand);
        AgentEditingToolSet.RegisterDefaults(
            AgentToolRegistry,
            AgentEditingToolBackend);
        AgentToolExecutor = new AgentToolExecutor(
            AgentToolRegistry,
            debugLog: AgentDebugLog);
        AgentModel = new AiServerAgentModel(
            AiVideoAnalysisService,
            AgentDebugLog);
        AgentPlanningLoop = new AgentPlanningLoop(
            AiAgentOrchestrator,
            AgentToolRegistry,
            AgentToolExecutor,
            AgentModel,
            conversationProvider: BuildAgentConversationContext,
            debugLog: AgentDebugLog);
        AgentExecutionLoop = new AgentExecutionLoop(
            AiAgentOrchestrator,
            AgentToolRegistry,
            AgentToolExecutor,
            AgentModel,
            conversationProvider: BuildAgentConversationContext,
            seedObservationProvider: () => AgentPlanningLoop.Observations,
            debugLog: AgentDebugLog);
        AiAgentOrchestrator.TaskChanged += (_, args) =>
        {
            AgentDebugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "orchestrator",
                "task_changed",
                args.State.Id,
                args.State.Phase.ToString(),
                Message: "Agent task state changed.",
                Details: DescribeAgentTaskForDebug(args.State)));

            OnPropertyChanged(nameof(IsAgentDraftEditingLocked));
            OnPropertyChanged(nameof(CurrentAgentTask));
        };

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
    public AiVideoAnalysisService AiVideoAnalysisService { get; }
    public AutomationOrchestrator AutomationOrchestrator { get; }
    public AiMontageAnalysisService AiMontageAnalysisService { get; }
    public IAiMontageCoordinator AiMontageCoordinator { get; }
    public IAgentDebugLog AgentDebugLog { get; }
    public string? AgentDebugLogPath => AgentDebugLog.CurrentLogPath;
    public AiAgentOrchestrator AiAgentOrchestrator { get; }
    public KadrAgentReadOnlyToolBackend AgentReadOnlyToolBackend { get; }
    public KadrAgentEditingToolBackend AgentEditingToolBackend { get; }
    public AgentToolRegistry AgentToolRegistry { get; }
    public AgentToolExecutor AgentToolExecutor { get; }
    public IAgentModel AgentModel { get; }
    public AgentPlanningLoop AgentPlanningLoop { get; }
    public AgentExecutionLoop AgentExecutionLoop { get; }
    public AgentTaskState? CurrentAgentTask => AiAgentOrchestrator.CurrentTask;
    public bool IsAgentDraftEditingLocked =>
        AiAgentOrchestrator.CurrentTask?.IsDraftReadOnlyForUser == true;
    public IArtifactStore ArtifactStore => _artifactStore;
    public KadrStudio.Core.Domain.ProjectState CoreState => _editorSession.State;
    public long TimelinePresentationRevision => _timelinePresentationRevision;

    public ProjectViewState Project
    {
        get => _project;
        private set
        {
            if (ReferenceEquals(_project, value))
            {
                return;
            }

            _project = value;
            AttachProject(_project);
            BuildMediaView();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectTitle));
            OnPropertyChanged(nameof(TimelineDurationLabel));
            OnPropertyChanged(nameof(CanExport));
            OnPropertyChanged(nameof(CoreState));
        }
    }

    public ICollectionView MediaView => _mediaView;

    public string ProjectName
    {
        get => _editorSession.State.Name;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Новый проект" : value.Trim();
            if (string.Equals(_editorSession.State.Name, normalized, StringComparison.Ordinal)) return;
            ExecuteCoreCommand("Проект переименован", new RenameProjectCommand(normalized));
        }
    }

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

    public string ProjectTitle => $"{ProjectName}{(IsDirty ? " •" : string.Empty)}";

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
        if (proposal.CreateCheckpoint)
            await ProjectHistoryService.CreateCheckpointAsync(
                _editorSession.State, Project.FilePath, $"Before: {proposal.Title}",
                _editorSession.State, cancellationToken);
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
        EnsureAgentAllowsManualProjectMutation();

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
                }
                QueueBackgroundAnalysis(imported.Select(item => item.Id));
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

    public bool RegisterImportedMedia(MediaAsset asset)
    {
        EnsureAgentAllowsManualProjectMutation();
        ArgumentNullException.ThrowIfNull(asset);
        if (_editorSession.State.Sources.ContainsKey(asset.Id)) return false;
        var result = _editorSession.Execute(new EditTransaction(
            "Импорт медиа", new AddSourcesCommand([_projectMapper.ToCoreSource(asset)])));
        if (!result.Changed) return false;
        RestoreFromCoreState(result.State, autosaveReason: "Импорт медиа");
        var restored = Project.FindAsset(asset.Id);
        if (restored is not null)
        {
            restored.ThumbnailPath = asset.ThumbnailPath;
            restored.ProbeResult = asset.ProbeResult;
        }
        return true;
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

        // Editing always wins over opportunistic import analysis. The user can
        // run the complete analysis explicitly from the AI workspace later.
        ResetBackgroundAnalysis();

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
        if (Project.FindAsset(asset.Id) is { } timelineAsset)
            QueueTimelineMediaPreparation(timelineAsset);
    }

    public void RefreshMediaOnlineState()
    {
        var refreshed = _mediaRegistry.RefreshOnlineState(_editorSession.State);
        var online = refreshed.Sources.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OnlineState == KadrStudio.Core.Domain.MediaOnlineState.Online);
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

    public bool RippleDeleteSelectedClip()
    {
        if (SelectedClip is null) return false;
        return ExecuteCoreCommand(
            "Клип удалён со сдвигом",
            new RippleDeleteSelectedMediaClipCommand(SelectedClip.Id));
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

    public bool SplitClipAt(Guid clipId, double seconds, bool includeLinked)
    {
        var clip = Project.FindClip(clipId);
        if (clip is null || seconds <= clip.Start + 0.1 || seconds >= clip.End - 0.1)
        {
            return false;
        }

        var rightId = Guid.NewGuid();
        return ExecuteCoreCommand(
            includeLinked ? "Связанные клипы разделены" : "Клип разделён без связи",
            new SplitSelectedMediaClipCommand(
                clip.Id,
                KadrStudio.Core.Domain.TimelineTime.FromSeconds(seconds),
                rightId,
                includeLinked),
            rightId);
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

    public TimelineClip? CreateSelectedClipDraft(TrackKind requestedTrack)
    {
        var selected = SelectedClip;
        if (selected is null) return null;
        var clip = selected.Track == requestedTrack
            ? selected
            : selected.LinkGroupId is Guid groupId
                ? Project.Clips.FirstOrDefault(item => item.Track == requestedTrack && item.LinkGroupId == groupId)
                : null;
        return clip?.Clone();
    }

    public bool CommitClipDraft(TimelineClip draft, string status)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var current = _editorSession.State.FindMediaClip(draft.Id);
        if (current is null) return false;
        NormalizeClipDraft(draft);
        var video = current.Video is null ? null : new KadrStudio.Core.Domain.VideoParameters(
            draft.Brightness, draft.Contrast, draft.Saturation, draft.Temperature,
            draft.PositionX, draft.PositionY, draft.ScaleX, draft.ScaleY, draft.Rotation,
            draft.CropLeft, draft.CropTop, draft.CropRight, draft.CropBottom, draft.Opacity);
        var audio = current.Audio is null ? null : new KadrStudio.Core.Domain.AudioParameters(
            draft.Volume, draft.IsMuted, draft.Pan,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(draft.FadeIn),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(draft.FadeOut),
            draft.Bass, draft.Mid, draft.Treble);
        var updated = current with
        {
            Start = KadrStudio.Core.Domain.TimelineTime.FromSeconds(draft.Start),
            SourceIn = KadrStudio.Core.Domain.TimelineTime.FromSeconds(draft.SourceStart),
            Duration = KadrStudio.Core.Domain.TimelineTime.FromSeconds(draft.Duration),
            Video = video,
            Audio = audio
        };
        return ExecuteCoreCommand(status, new UpsertMediaClipCommand(updated), draft.Id);
    }

    private void NormalizeClipDraft(TimelineClip clip)
    {

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

    public IReadOnlyList<CoreGameEditingProfile> GetGameEditingProfiles()
        => _settingsService.LoadGameEditingProfiles();

    public Task SaveCustomGameEditingProfilesAsync(
        IEnumerable<CoreGameEditingProfile> profiles,
        CancellationToken cancellationToken = default)
        => _settingsService.SaveCustomGameProfilesAsync(profiles, cancellationToken);

    public IReadOnlyList<CoreSequenceState> GetSequences()
        => _editorSession.State.Sequences.IsDefaultOrEmpty
            ? [_editorSession.State.EnsureSequenceContainer().ActiveSequence!]
            : _editorSession.State.Sequences;

    public void EnsureSequenceWorkspace()
    {
        if (_editorSession.State.Sequences.IsDefaultOrEmpty)
            ExecuteCoreCommand("Создан исходный вариант монтажа", new InitializeSequenceWorkspaceCommand());
    }

    public IReadOnlyList<CoreMontagePlan> GetMontagePlans()
        => _editorSession.State.MontagePlans;

    public KadrStudio.Core.Domain.AiConversation GetAiConversation()
        => _editorSession.State.AiConversation;

    private static string DescribeAgentTaskForDebug(AgentTaskState task)
    {
        var plan = task.Plan is null
            ? "none"
            : $"v{task.Plan.Version}; approved={task.Plan.ApprovedAt is not null}; " +
              $"objective={task.Plan.Objective}; steps={task.Plan.Steps.Length}";

        return
            $"project_id={task.ProjectId}\n" +
            $"source_sequence_id={task.SourceSequenceId}\n" +
            $"source_sequence_revision={task.SourceSequenceRevision?.ToString() ?? "null"}\n" +
            $"draft_sequence_id={task.DraftSequenceId?.ToString() ?? "null"}\n" +
            $"resume_phase={task.ResumePhase?.ToString() ?? "null"}\n" +
            $"questions={task.Questions.Length}\n" +
            $"plan={plan}\n" +
            $"failure={task.FailureMessage ?? string.Empty}\n" +
            $"completion={task.CompletionSummary ?? string.Empty}";
    }

    private ImmutableArray<AgentConversationContextMessage> BuildAgentConversationContext()
    {
        var builder = ImmutableArray.CreateBuilder<AgentConversationContextMessage>();

        foreach (var message in _editorSession.State.AiConversation.Messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

            if (message.Role == KadrStudio.Core.Domain.AiChatRole.User)
            {
                builder.Add(new AgentConversationContextMessage(
                    AgentConversationRole.User,
                    message.Text.Trim(),
                    message.CreatedAt));
                continue;
            }

            if (message.Kind is not (
                    KadrStudio.Core.Domain.AiChatMessageKind.Text or
                    KadrStudio.Core.Domain.AiChatMessageKind.Question or
                    KadrStudio.Core.Domain.AiChatMessageKind.Plan or
                    KadrStudio.Core.Domain.AiChatMessageKind.Draft))
            {
                continue;
            }

            builder.Add(new AgentConversationContextMessage(
                AgentConversationRole.Assistant,
                message.Text.Trim(),
                message.CreatedAt));

            if (message.Kind == KadrStudio.Core.Domain.AiChatMessageKind.Question &&
                !string.IsNullOrWhiteSpace(message.Answer) &&
                message.AgentQuestionId is null)
            {
                builder.Add(new AgentConversationContextMessage(
                    AgentConversationRole.User,
                    $"Ответ на вопрос «{message.Text.Trim()}»: {message.Answer.Trim()}",
                    message.CreatedAt));
            }
        }

        // The context builder has dynamic length because messages can be filtered
        // and one chat message may expand into multiple agent-context messages.
        // MoveToImmutable() requires Count == Capacity; ToImmutable() is correct here.
        return builder.ToImmutable();
    }

    public void SaveAiConversation(KadrStudio.Core.Domain.AiConversation conversation)
    {
        var result = _editorSession.Execute(new EditTransaction(
            "Диалог ИИ обновлён",
            [new ReplaceAiConversationCommand(conversation)],
            RecordInHistory: false,
            SynchronizeActiveSequence: false));
        if (!result.Changed) return;
        IsDirty = true;
        ScheduleAutosave("Диалог ИИ обновлён");
        OnPropertyChanged(nameof(CoreState));
    }

    public void PersistAgentTaskState(AgentTaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var normalizedPlan = task.Plan is null
            ? null
            : task.Plan with
            {
                Constraints = task.Plan.Constraints.IsDefault ? [] : task.Plan.Constraints,
                Steps = task.Plan.Steps.Select(step => step with
                {
                    EvidenceObservationSequences = step.EvidenceObservationSequences.IsDefault
                        ? []
                        : step.EvidenceObservationSequences,
                    ProtectedInvariants = step.ProtectedInvariants.IsDefault
                        ? []
                        : step.ProtectedInvariants,
                    VerificationChecks = step.VerificationChecks.IsDefault
                        ? []
                        : step.VerificationChecks
                }).ToImmutableArray()
            };
        var normalized = task with
        {
            Brief = task.Brief is null
                ? null
                : AgentTaskBrief.Create(
                    task.Brief.Kind,
                    task.Brief.Goal,
                    task.Brief.Scope,
                    task.Brief.ProtectedElements.IsDefault ? [] : task.Brief.ProtectedElements,
                    task.Brief.Constraints.IsDefault ? [] : task.Brief.Constraints,
                    task.Brief.AcceptanceCriteria.IsDefault ? [] : task.Brief.AcceptanceCriteria,
                    task.Brief.Assumptions.IsDefault ? [] : task.Brief.Assumptions,
                    task.Brief.MissingInformation.IsDefault ? [] : task.Brief.MissingInformation),
            Plan = normalizedPlan,
            Questions = task.Questions.IsDefault
                ? []
                : task.Questions.Select(question => question with
                {
                    Options = question.AvailableOptions
                }).ToImmutableArray(),
            Journal = task.Journal.IsDefault ? [] : task.Journal,
            EvidenceLedger = task.Evidence.Select(evidence => evidence with
            {
                Facts = evidence.Facts.IsDefault ? [] : evidence.Facts
            }).ToImmutableArray()
        };
        var payload = JsonSerializer.Serialize(normalized);
        var conversation = GetAiConversation();
        var existing = conversation.Messages.LastOrDefault(message =>
            message.Kind == KadrStudio.Core.Domain.AiChatMessageKind.AgentMemory &&
            message.AgentTaskId == task.Id);
        var memory = existing is null
            ? new KadrStudio.Core.Domain.AiChatMessage(
                Guid.NewGuid(),
                KadrStudio.Core.Domain.AiChatRole.Assistant,
                KadrStudio.Core.Domain.AiChatMessageKind.AgentMemory,
                payload,
                DateTimeOffset.UtcNow,
                AgentTaskId: task.Id)
            : existing with { Text = payload };
        SaveAiConversation(existing is null
            ? conversation with
            {
                Messages = conversation.Messages.Add(memory),
                UpdatedAt = DateTimeOffset.UtcNow
            }
            : conversation with
            {
                Messages = conversation.Messages.Replace(existing, memory),
                UpdatedAt = DateTimeOffset.UtcNow
            });
    }

    public AgentTaskState StartAgentTask(string userRequest)
    {
        if (HasPendingEditReview)
        {
            throw new InvalidOperationException(
                "Сначала примите или отмените текущий черновик ИИ.");
        }

        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException(
                "Запрос агенту не может быть пустым.",
                nameof(userRequest));
        }

        EnsureSequenceWorkspace();
        var sequence = _editorSession.State.ActiveSequence
            ?? throw new InvalidOperationException(
                "Для задачи агента нужен активный таймлайн.");

        return AiAgentOrchestrator.StartTask(
            _editorSession.State.Id,
            sequence.Id,
            userRequest.Trim(),
            _editorSession.State.AiConversation.Id,
            sequence.Revision);
    }

    public AgentTaskState BeginAgentPlanRevision()
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException(
                "Нет активной задачи агента.");

        if (task.Phase is not (
                AgentTaskPhase.WaitingForApproval or
                AgentTaskPhase.Approved))
        {
            throw new AgentTaskTransitionException(
                "Исправлять план можно только после его публикации.");
        }

        var source = _editorSession.State
            .EnsureSequenceContainer()
            .SynchronizeActiveSequence()
            .FindSequence(task.SourceSequenceId)
            ?? throw new AgentTaskTransitionException(
                "Исходный таймлайн задачи больше не найден.");

        return AiAgentOrchestrator.BeginInvestigation(
            "Пользователь уточнил план; агент проверяет, что нужно изменить.",
            source.Revision);
    }

    public AgentTaskState AnswerAgentQuestion(
        string answer,
        Guid? questionId = null)
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException(
                "Нет активной задачи агента.");
        var question = questionId is { } requestedId
            ? task.Questions.FirstOrDefault(item => item.Id == requestedId && !item.IsAnswered)
            : task.Questions.LastOrDefault(item => !item.IsAnswered);
        if (question is null)
        {
            throw new AgentTaskTransitionException(
                "У агента нет указанного открытого вопроса.");
        }

        return AiAgentOrchestrator.AnswerQuestion(
            question.Id,
            answer);
    }

    public CoreSequenceState ApproveAgentPlanAndCreateDraft()
    {
        var pending = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException(
                "Нет активной задачи агента.");

        if (pending.Phase != AgentTaskPhase.WaitingForApproval ||
            pending.Plan is null)
        {
            throw new AgentTaskTransitionException(
                "Для выполнения нужен последний неустаревший план агента.");
        }

        var state = _editorSession.State.EnsureSequenceContainer().SynchronizeActiveSequence();
        if (state.Id != pending.ProjectId)
        {
            throw new AgentTaskTransitionException(
                "Проект сменился после подготовки плана.");
        }

        var source = state.FindSequence(pending.SourceSequenceId)
            ?? throw new AgentTaskTransitionException(
                "Исходный таймлайн задачи больше не найден.");

        if (pending.SourceSequenceRevision is { } expectedRevision &&
            source.Revision != expectedRevision)
        {
            throw new AgentTaskTransitionException(
                "Исходный таймлайн изменился после исследования. Напишите агенту, чтобы он обновил план перед выполнением.");
        }

        var approved = AiAgentOrchestrator.ApprovePlan();
        var plan = approved.Plan!;

        var title = string.IsNullOrWhiteSpace(plan.Objective)
            ? "Agent Draft"
            : $"Agent Draft · {plan.Objective.Trim()}";
        if (title.Length > 96)
        {
            title = title[..96].TrimEnd();
        }

        var draft = source with
        {
            Id = Guid.NewGuid(),
            Name = title,
            Revision = 0,
            Status = KadrStudio.Core.Domain.SequenceStatus.Draft,
            ParentSequenceId = source.Id,
            MontagePlanId = null
        };

        AgentEditingToolBackend.Reset(approved.Id);

        if (!ExecuteAgentCoreCommand(
                "Agent Draft создан",
                new CreateSequenceCommand(draft, Activate: true)))
        {
            throw new InvalidOperationException(
                "Не удалось создать отдельный Agent Draft.");
        }

        var executing = AiAgentOrchestrator.BeginExecution(draft.Id);
        StatusText = "Агент выполняет утверждённый план в отдельном черновике";
        OnPropertyChanged(nameof(IsAgentDraftEditingLocked));
        OnPropertyChanged(nameof(CurrentAgentTask));

        return _editorSession.State.FindSequence(executing.DraftSequenceId!.Value)
            ?? throw new InvalidOperationException(
                "Agent Draft не найден после создания.");
    }

    public AgentTaskState StopAgentTask(string? reason = null)
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException(
                "Нет активной задачи агента.");

        return task.IsTerminal
            ? task
            : AiAgentOrchestrator.Stop(
                string.IsNullOrWhiteSpace(reason)
                    ? "Задача остановлена пользователем."
                    : reason);
    }

    public AgentTaskState RetryFailedAgentPlanning()
    {
        var task = AiAgentOrchestrator.RetryFailedPlanning();
        PersistAgentTaskState(task);
        return task;
    }

    public async Task<ImmutableDictionary<Guid, CoreMediaAnalysisManifest>> AnalyzeMontageSourcesAsync(
        MediaAnalysisRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsBackground)
            ResetBackgroundAnalysis();
        var snapshot = _editorSession.State;
        var manifests = await AiMontageCoordinator.AnalyzeSourcesAsync(
            snapshot, request, progress, cancellationToken);
        if (_editorSession.State.Id != snapshot.Id)
            throw new InvalidOperationException("Проект сменился во время анализа.");
        var references = manifests.Values.Select(item => new KadrStudio.Core.Domain.MediaAnalysisReference(
            item.SourceId, item.SourceFingerprint, item.PipelineVersion, item.Model,
            item.ProfileId, item.ProfileVersion, DateTimeOffset.UtcNow)).ToArray();
        if (references.Length > 0)
            ExecuteCoreCommand("Индекс ИИ-анализа обновлён", new ReplaceAnalysisReferencesCommand(references));
        return manifests;
    }

    public async Task<MontagePreparationResult> PrepareMontagePlanAsync(
        MediaAnalysisRequest analysisRequest,
        CoreMontageRequest montageRequest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ResetBackgroundAnalysis();
        var snapshot = _editorSession.State;
        var result = await AiMontageCoordinator.PreparePlanAsync(
            snapshot, analysisRequest, montageRequest, progress, cancellationToken);
        if (_editorSession.State.Id != snapshot.Id)
            throw new InvalidOperationException("Проект сменился во время подготовки плана.");
        var references = result.Manifests.Values.Select(item => new KadrStudio.Core.Domain.MediaAnalysisReference(
            item.SourceId, item.SourceFingerprint, item.PipelineVersion, item.Model,
            item.ProfileId, item.ProfileVersion, DateTimeOffset.UtcNow)).ToArray();
        var commands = new List<IEditCommand>();
        if (references.Length > 0)
            commands.Add(new ReplaceAnalysisReferencesCommand(references));
        commands.Add(new UpsertMontagePlanCommand(result.Plan));
        ExecuteCoreCommand(
            "План ИИ-монтажа подготовлен",
            new EditBatchCommand("Prepare AI montage", commands));
        return result;
    }

    public CoreMontagePlan ResolveMontageDecision(
        CoreMontagePlan plan,
        Guid decisionId,
        string answer,
        KadrStudio.Core.Domain.TimelineTime? resolvedTime = null)
    {
        var updated = AiMontageCoordinator.ResolveDecision(
            _editorSession.State, plan, decisionId, answer, resolvedTime);
        ExecuteCoreCommand("Уточнение ИИ-плана сохранено", new UpsertMontagePlanCommand(updated));
        return updated;
    }

    public async Task<CoreMontagePlan> CreateMontagePlanAsync(
        CoreMontageRequest request,
        ImmutableDictionary<Guid, CoreMediaAnalysisManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        var plan = await AiMontageCoordinator.CreatePlanAsync(
            _editorSession.State, request, manifests, cancellationToken);
        ExecuteCoreCommand("План ИИ-монтажа создан", new UpsertMontagePlanCommand(plan));
        return plan;
    }

    public async Task<CoreMontagePlan> ReviseMontagePlanAsync(
        CoreMontagePlan plan,
        string revisionRequest,
        ImmutableDictionary<Guid, CoreMediaAnalysisManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        var revised = await AiMontageCoordinator.RevisePlanAsync(
            _editorSession.State, plan, revisionRequest, manifests, cancellationToken);
        ExecuteCoreCommand("План ИИ-монтажа скорректирован", new UpsertMontagePlanCommand(revised));
        return revised;
    }

    public void SaveMontagePlan(CoreMontagePlan plan)
        => ExecuteCoreCommand("План ИИ-монтажа изменён", new UpsertMontagePlanCommand(plan));

    public CoreSequenceState CreateMontageDraft(
        CoreMontagePlan plan,
        IReadOnlyDictionary<Guid, CoreMediaAnalysisManifest>? manifests = null)
    {
        EnsureAgentAllowsManualProjectMutation();
        var compilation = AiMontageCoordinator.CreateDraft(_editorSession.State, plan, manifests);
        var compiledPlan = plan with
        {
            Status = KadrStudio.Core.Domain.MontagePlanStatus.Compiled,
            Warnings = plan.Warnings.AddRange(compilation.Warnings),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var result = _editorSession.Execute(new EditTransaction(
            "Черновик ИИ-монтажа создан",
            [
                new UpsertMontagePlanCommand(compiledPlan),
                new CreateSequenceCommand(compilation.Sequence, Activate: true)
            ],
            CreateCheckpoint: true,
            CheckpointName: $"До ИИ-монтажа: {plan.Title}"));
        if (!result.Changed) throw new InvalidOperationException("Черновик не изменил проект.");
        RestoreFromCoreState(result.State, autosaveReason: "Черновик ИИ-монтажа создан");
        StatusText = "Черновик ИИ создан в отдельном варианте таймлайна";
        return result.State.ActiveSequence!;
    }

    public bool ActivateSequence(Guid sequenceId)
        => ExecuteCoreCommand("Вариант монтажа открыт", new ActivateSequenceCommand(sequenceId));

    public bool AcceptActiveMontageDraft()
    {
        var sequence = _editorSession.State.ActiveSequence;
        return sequence is not null && sequence.Status == KadrStudio.Core.Domain.SequenceStatus.Draft &&
               ExecuteCoreCommand("Вариант ИИ-монтажа принят",
                   new SetSequenceStatusCommand(sequence.Id, KadrStudio.Core.Domain.SequenceStatus.Accepted));
    }

    public bool DeleteActiveMontageDraft()
    {
        var sequence = _editorSession.State.ActiveSequence;
        return sequence is not null && sequence.Status == KadrStudio.Core.Domain.SequenceStatus.Draft &&
               ExecuteCoreCommand("Черновик ИИ-монтажа удалён", new DeleteDraftSequenceCommand(sequence.Id));
    }

    public void UpsertSourceAnnotation(CoreSourceAnnotation annotation)
        => ExecuteCoreCommand("Указание для ИИ сохранено", new UpsertSourceAnnotationCommand(annotation));

    public void DeleteSourceAnnotation(Guid annotationId)
        => ExecuteCoreCommand("Указание для ИИ удалено", new DeleteSourceAnnotationCommand(annotationId));

    public int BeginEditPlanReview(EditCommandPlan plan)
    {
        if (IsAgentDraftEditingLocked)
        {
            throw new InvalidOperationException(
                "Пока агент работает с Agent Draft, ручное редактирование заблокировано.");
        }

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
            _editorSession.State,
            Project.FilePath,
            $"До ИИ: {_editReviewReason ?? "команда монтажа"}",
            _editReviewSnapshot,
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
        var entry = await ProjectHistoryService.CreateCheckpointAsync(
            _editorSession.State, Project.FilePath, message, cancellationToken: cancellationToken);
        StatusText = $"Создана контрольная точка: {entry.Message}";
        return entry;
    }

    public Task<IReadOnlyList<ProjectHistoryEntry>> GetHistoryCheckpointsAsync(CancellationToken cancellationToken = default)
        => ProjectHistoryService.GetCheckpointsAsync(_editorSession.State, Project.FilePath, cancellationToken);

    public async Task RestoreHistoryCheckpointAsync(
        ProjectHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        EnsureAgentAllowsManualProjectMutation();

        if (entry.ProjectId != Project.Id)
        {
            throw new InvalidOperationException("Эта контрольная точка относится к другому проекту.");
        }
        if (HasPendingEditReview)
        {
            throw new InvalidOperationException("Сначала примите или верните черновик ИИ.");
        }

        await ProjectHistoryService.CreateCheckpointAsync(
            _editorSession.State, Project.FilePath,
            $"Авто: перед откатом к «{entry.Message}»", _editorSession.State, cancellationToken);

        var filePath = Project.FilePath;
        var restoredCore = await ProjectHistoryService.RestoreCheckpointAsync(entry, cancellationToken);
        _editorSession.Execute(new EditTransaction(
            $"Restore checkpoint: {entry.Message}",
            new RestoreProjectCommand(restoredCore, $"Restore checkpoint: {entry.Message}")));
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

    public void Undo()
    {
        if (IsAgentDraftEditingLocked)
        {
            StatusText = "Undo недоступен, пока агент выполняет или проверяет Agent Draft";
            return;
        }

        if (!_editorSession.Undo())
        {
            return;
        }

        RestoreFromCoreState(_editorSession.State);
        StatusText = "Изменение отменено";
    }

    public void Redo()
    {
        if (IsAgentDraftEditingLocked)
        {
            StatusText = "Redo недоступен, пока агент выполняет или проверяет Agent Draft";
            return;
        }

        if (!_editorSession.Redo())
        {
            return;
        }

        RestoreFromCoreState(_editorSession.State);
        StatusText = "Изменение повторено";
    }

    public async Task NewProjectAsync(CancellationToken cancellationToken = default)
    {
        EnsureAgentAllowsManualProjectMutation();
        CancelAutosave();
        ResetBackgroundAnalysis();
        await _projectService.DeleteAutosaveAsync(cancellationToken);
        _editReviewSnapshot = null;
        _editReviewReason = null;
        _editReviewSelectedClipId = null;
        SelectedClip = null;
        SelectedAsset = null;
        Playhead = 0;
        var state = KadrStudio.Core.Domain.ProjectState.CreateNew();
        _editorSession = new EditorSession(state);
        Project = _projectMapper.ToUi(state);
        IsDirty = false;
        StatusText = "Создан новый проект";
        NotifyHistoryChanged();
    }

    public async Task OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureAgentAllowsManualProjectMutation();
        IsBusy = true;
        try
        {
            CancelAutosave();
            ResetBackgroundAnalysis();
            await _projectService.DeleteAutosaveAsync(cancellationToken);
            var project = await _projectService.OpenAsync(path, cancellationToken);
            _editReviewSnapshot = null;
            _editReviewReason = null;
            _editReviewSelectedClipId = null;
            SelectedClip = null;
            SelectedAsset = null;
            Playhead = 0;
            var refreshed = _mediaRegistry.RefreshOnlineState(project);
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
            await _projectService.SaveAsync(_editorSession.State, path, cancellationToken);
            Project.FilePath = Path.GetFullPath(path);
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
        EnsureAgentAllowsManualProjectMutation();

        if (!await _projectService.HasAutosaveAsync(cancellationToken))
        {
            return;
        }

        CancelAutosave();
        ResetBackgroundAnalysis();
        var project = recovery is null
            ? await _projectService.OpenAutosaveAsync(cancellationToken)
            : await _projectService.OpenAutosaveVersionAsync(
                recovery.ProjectId, recovery.RecoveryId, cancellationToken);
        SelectedClip = null;
        SelectedAsset = null;
        Playhead = 0;
        var refreshed = _mediaRegistry.RefreshOnlineState(project);
        _editorSession = new EditorSession(refreshed);
        Project = _projectMapper.ToUi(refreshed);
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

    private void MarkChanged()
    {
        if (_suppressDirtyTracking)
        {
            return;
        }

        IsDirty = true;
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
        _backgroundAnalysisCancellation.Cancel();
        _timelineMediaPreparationCancellation.Cancel();
        Task[] pendingAnalysis;
        lock (_backgroundAnalysisGate) pendingAnalysis = _backgroundAnalysisTasks.ToArray();
        Task[] pendingTimelineMedia;
        lock (_timelineMediaPreparationGate) pendingTimelineMedia = _timelineMediaPreparationTasks.Values.ToArray();
        try { await Task.WhenAll(pendingAnalysis); } catch (OperationCanceledException) { }
        try { await Task.WhenAll(pendingTimelineMedia); } catch (OperationCanceledException) { }
        _backgroundAnalysisCancellation.Dispose();
        _timelineMediaPreparationCancellation.Dispose();
        await _automationScheduler.DisposeAsync();
        await ThumbnailService.DisposeAsync();
        await TimelineMediaCacheService.DisposeAsync();
        await _renderCoordinator.DisposeAsync();
        await _artifactStore.DisposeAsync();
        _projectService.Dispose();
        AiVideoAnalysisService.Dispose();
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

    private void EnsureAgentAllowsManualProjectMutation()
    {
        if (!IsAgentDraftEditingLocked)
        {
            return;
        }

        throw new InvalidOperationException(
            "Agent Draft сейчас принадлежит агенту. Остановите задачу, прежде чем менять проект вручную.");
    }

    private bool ExecuteCoreCommand(string description, IEditCommand command, Guid? selectedClipId = null)
    {
        if (IsAgentDraftEditingLocked && _agentMutationDepth == 0)
        {
            StatusText = "Agent Draft сейчас принадлежит агенту; ручное редактирование временно заблокировано";
            return false;
        }

        var result = _editorSession.Execute(new EditTransaction(description, command));
        if (!result.Changed) return false;
        RestoreFromCoreState(result.State, selectedClipId, description);
        StatusText = description;
        return true;
    }

    private bool ExecuteAgentCoreCommand(
        string description,
        IEditCommand command)
    {
        _agentMutationDepth++;
        try
        {
            return ExecuteCoreCommand(description, command);
        }
        finally
        {
            _agentMutationDepth--;
        }
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
        if (track.Kind == KadrStudio.Core.Domain.TrackKind.Audio &&
            kind != KadrStudio.Core.Domain.TransitionKind.ConstantPowerAudio ||
            track.Kind == KadrStudio.Core.Domain.TrackKind.Visual &&
            kind == KadrStudio.Core.Domain.TransitionKind.ConstantPowerAudio)
            throw new EditRejectedException("Тип перехода не подходит выбранной дорожке.");
        var duration = KadrStudio.Core.Domain.TimelineTime.FromSeconds(Math.Clamp(durationSeconds, 0.04, 30));
        var transitionId = Guid.NewGuid();
        ExecuteCoreCommand(
            "Переход добавлен",
            new CreateTransitionAtEditCommand(
                transitionId, from.Id, kind, duration,
                track.Kind == KadrStudio.Core.Domain.TrackKind.Visual ? Guid.NewGuid() : null),
            from.Id);
        return transitionId;
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
                asset.ThumbnailPath, asset.Waveform));
        var restored = _projectMapper.ToUi(state, filePath);
        foreach (var asset in restored.Media)
        {
            if (!derivedMedia.TryGetValue(asset.Id, out var derived)) continue;
            asset.ThumbnailPath = derived.ThumbnailPath;
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
        KadrStudio.Application.Caching.WaveformPyramid Waveform);

    private void AttachProject(ProjectViewState project)
    {
        TryRestoreAgentTask();
        var timelineAssetIds = project.Clips.Select(item => item.AssetId).ToHashSet();
        foreach (var asset in project.Media.Where(item => timelineAssetIds.Contains(item.Id)))
        {
            QueueTimelineMediaPreparation(asset);
        }
    }

    private void TryRestoreAgentTask()
    {
        if (AiAgentOrchestrator.CurrentTask is { IsTerminal: false })
        {
            return;
        }

        var memory = _editorSession.State.AiConversation.Messages
            .LastOrDefault(message =>
                message.Kind == KadrStudio.Core.Domain.AiChatMessageKind.AgentMemory &&
                message.AgentTaskId.HasValue);
        if (memory is null || string.IsNullOrWhiteSpace(memory.Text))
        {
            return;
        }

        try
        {
            var task = JsonSerializer.Deserialize<AgentTaskState>(memory.Text);
            if (task is null || task.ProjectId != _editorSession.State.Id)
            {
                return;
            }

            AiAgentOrchestrator.RestoreTask(task);
            if (task.DraftSequenceId is not null)
            {
                AgentEditingToolBackend.Reset(task.Id);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or AgentTaskTransitionException or ArgumentException)
        {
            AgentDebugLog.Write(new AgentDebugLogEntry(
                DateTimeOffset.UtcNow,
                "agent_persistence",
                "restore_failed",
                Message: exception.Message,
                Exception: exception.ToString()));
        }
    }

    private void QueueTimelineMediaPreparation(MediaAsset asset)
    {
        if (asset.Kind == MediaKind.Image || !asset.HasAudio || !asset.Waveform.IsEmpty ||
            !_editorSession.State.Sources.TryGetValue(asset.Id, out var source))
        {
            return;
        }

        var key = new TimelineMediaPreparationKey(
            source.Id,
            MontagePlanValidator.StableFingerprint(source));
        Task task;
        lock (_timelineMediaPreparationGate)
        {
            if (_timelineMediaPreparationTasks.ContainsKey(key)) return;
            task = PrepareTimelineMediaAsync(source, key, _timelineMediaPreparationCancellation.Token);
            _timelineMediaPreparationTasks.Add(key, task);
        }

        _ = task.ContinueWith(
            _ =>
            {
                lock (_timelineMediaPreparationGate)
                {
                    if (_timelineMediaPreparationTasks.TryGetValue(key, out var current) &&
                        ReferenceEquals(current, task))
                    {
                        _timelineMediaPreparationTasks.Remove(key);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PrepareTimelineMediaAsync(
        KadrStudio.Core.Domain.MediaSource source,
        TimelineMediaPreparationKey key,
        CancellationToken cancellationToken)
    {
        var presentationChanged = false;
        try
        {
            var derived = await TimelineMediaCacheService.PrepareAsync(source, cancellationToken);
            if (!_editorSession.State.Sources.TryGetValue(source.Id, out var currentSource) ||
                !MontagePlanValidator.StableFingerprint(currentSource)
                    .Equals(key.Fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var current = Project.FindAsset(source.Id);
            if (current is null) return;
            current.Waveform = derived.Waveform;
            presentationChanged = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Визуальный кэш недоступен: {exception.Message}";
        }
        finally
        {
            if (presentationChanged)
            {
                _timelinePresentationRevision++;
                OnPropertyChanged(nameof(TimelinePresentationRevision));
            }
        }
    }

    private readonly record struct TimelineMediaPreparationKey(Guid SourceId, string Fingerprint);

    private void QueueBackgroundAnalysis(IEnumerable<Guid> sourceIds)
    {
        var ids = sourceIds.Distinct()
            .Where(id => _editorSession.State.Sources.TryGetValue(id, out var source) &&
                         source.Kind == KadrStudio.Core.Domain.MediaKind.Video)
            .ToImmutableArray();
        if (ids.IsDefaultOrEmpty)
        {
            return;
        }

        var snapshot = _editorSession.State;
        var token = _backgroundAnalysisCancellation.Token;
        var task = RunBackgroundAnalysisAsync(snapshot, ids, token);
        lock (_backgroundAnalysisGate) _backgroundAnalysisTasks.Add(task);
        _ = task.ContinueWith(
            completed =>
            {
                lock (_backgroundAnalysisGate) _backgroundAnalysisTasks.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunBackgroundAnalysisAsync(
        KadrStudio.Core.Domain.ProjectState snapshot,
        ImmutableArray<Guid> sourceIds,
        CancellationToken cancellationToken)
    {
        try
        {
            // Most users drag freshly imported media immediately. Keep this
            // cheap idle window so background detection never races that drop.
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            var profile = GameEditingProfiles.Get("universal");
            var manifests = await AiMontageCoordinator.AnalyzeSourcesAsync(
                snapshot,
                new MediaAnalysisRequest(sourceIds, profile, string.Empty, DeepAnalysis: false, IsBackground: true),
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_editorSession.State.Id != snapshot.Id)
            {
                return;
            }

            var references = manifests.Values
                .Where(manifest => _editorSession.State.Sources.TryGetValue(manifest.SourceId, out var current) &&
                                   MontagePlanValidator.StableFingerprint(current)
                                       .Equals(manifest.SourceFingerprint, StringComparison.Ordinal))
                .Select(manifest => new KadrStudio.Core.Domain.MediaAnalysisReference(
                    manifest.SourceId, manifest.SourceFingerprint, manifest.PipelineVersion, manifest.Model,
                    manifest.ProfileId, manifest.ProfileVersion, DateTimeOffset.UtcNow))
                .ToArray();
            if (references.Length > 0)
                ExecuteCoreCommand("Фоновый индекс медиа обновлён", new ReplaceAnalysisReferencesCommand(references));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Background AI analysis skipped: {exception}");
        }
    }

    private void ResetBackgroundAnalysis()
    {
        _backgroundAnalysisCancellation.Cancel();
        _backgroundAnalysisCancellation.Dispose();
        _backgroundAnalysisCancellation = new CancellationTokenSource();
    }

    public Task<string?> GetTimelineThumbnailAsync(
        Guid sourceId,
        KadrStudio.Core.Domain.TimelineTime sourceTime,
        CancellationToken cancellationToken)
        => _editorSession.State.Sources.TryGetValue(sourceId, out var source)
            ? TimelineMediaCacheService.GetThumbnailAsync(source, sourceTime, cancellationToken)
            : Task.FromResult<string?>(null);

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
            await _projectService.SaveAutosaveVersionAsync(
                _editorSession.State, _pendingAutosaveReason, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Автосохранение не выполнено: {exception.Message}";
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
