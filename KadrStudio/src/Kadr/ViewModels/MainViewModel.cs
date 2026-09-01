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
using KadrStudio.Application.Automation.Agent.Persistence;
using KadrStudio.Application.Automation.Agent.Recovery;
using KadrStudio.Application.Automation.Agent.Workflow;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Application.Media;
using KadrStudio.Application.Storage;
using KadrStudio.Application.Upscaling;
using KadrStudio.Infrastructure.Media;
using KadrStudio.Application.Caching;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;
using KadrStudio.Services;
using KadrStudio.Services.Agent;
using KadrStudio.Services.Editorial;
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
        AiUpscaleService = services.AiUpscaleService;
        AiServer = services.AiServer;
        _automationScheduler = services.AutomationScheduler;
        AutomationOrchestrator = new AutomationOrchestrator(_automationScheduler, AutoSubtitleService);
        AgentDebugLog = new FileAgentDebugLog();
        AiAgentOrchestrator = new AiAgentOrchestrator();
        AgentRecoveryService = new AgentRecoveryService();
        var editorialTelemetry = new JsonlEditorialTelemetrySink();
        var aiServerV2 = new AiServerV2Client(AiServer, editorialTelemetry);
        var editorialIndexer = new AiServerMediaUnderstandingIndexer(
            aiServerV2,
            new AnalysisProxyBuilder(services.FfmpegLocator, services.ProcessRunner, editorialTelemetry));
        var editorialReasoner = new AiServerEditorialReasoner(aiServerV2);
        EditorialPipeline = new EditorialPipeline(
            editorialIndexer,
            editorialReasoner,
            editorialReasoner,
            editorialIndexer,
            retriever: new HierarchicalMediaRetriever(
                semanticRanker: new AiServerSemanticNodeRanker(aiServerV2)),
            telemetry: editorialTelemetry);
        AgentWorkflow = new AgentWorkflowService(
            AiAgentOrchestrator,
            StartAgentTask,
            RunEditorialAgentAsync,
            AcceptAgentDraft,
            ReviseAgentDraft,
            DiscardAgentDraft,
            CancelAgentTask,
            PersistAgentTaskState);
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
    public IAiUpscaleService AiUpscaleService { get; }
    public AiServerConnection AiServer { get; }
    public AutomationOrchestrator AutomationOrchestrator { get; }
    public IAgentDebugLog AgentDebugLog { get; }
    public AgentRecoveryService AgentRecoveryService { get; }
    public IAgentWorkflowService AgentWorkflow { get; }
    public string? AgentDebugLogPath => AgentDebugLog.CurrentLogPath;
    public AiAgentOrchestrator AiAgentOrchestrator { get; }
    public EditorialPipeline EditorialPipeline { get; }
    public KadrStudio.Core.Domain.MontageProfileKind? RequestedMontageProfileKind { get; set; }
    public ImmutableArray<Guid> RequestedEditorialSourceIds { get; set; } = [];
    public Func<KadrStudio.Core.Domain.BoundaryReviewRequest, CancellationToken,
        Task<ImmutableArray<KadrStudio.Core.Domain.BoundaryConfirmation>>>? BoundaryConfirmationHandler { get; set; }
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

        var source = _editorSession.State.Sources[asset.Id];
        var streams = source.Streams.IsDefault ? [] : source.Streams;
        var additions = new List<(KadrStudio.Core.Domain.TrackKind Kind, int Index, KadrStudio.Core.Domain.MediaClip Clip)>();
        var subtitleAdditions = new List<(int Index, KadrStudio.Core.Domain.SubtitleClip Clip)>();
        var linkGroupId = asset.Kind == MediaKind.Video &&
                          (source.HasAudio || streams.Any(item => item.Kind == KadrStudio.Core.Domain.MediaStreamKind.Subtitle))
            ? Guid.NewGuid()
            : (Guid?)null;
        clip.LinkGroupId = linkGroupId;

        if (asset.Kind != MediaKind.Audio)
        {
            var videoStream = streams.FirstOrDefault(item => item.Kind == KadrStudio.Core.Domain.MediaStreamKind.Video);
            additions.Add((KadrStudio.Core.Domain.TrackKind.Visual, clip.TrackIndex,
                CreateCoreClip(asset.Id, clip.Id, clip.Start, clip.SourceStart, clip.Duration,
                    linkGroupId, video: true, videoStream?.StreamIndex)));
        }

        var audioStreams = streams.Where(item => item.Kind == KadrStudio.Core.Domain.MediaStreamKind.Audio).ToArray();
        if (audioStreams.Length == 0 && source.HasAudio)
            audioStreams = [new KadrStudio.Core.Domain.MediaStreamDescriptor(
                0, KadrStudio.Core.Domain.MediaStreamKind.Audio,
                string.IsNullOrWhiteSpace(source.AudioCodec) ? "unknown" : source.AudioCodec,
                SampleRate: 48_000, Channels: 2)];
        var firstAudioTrack = FindAvailableTrackIndex(TrackKind.Audio, clip.Start, clip.Duration, 0);
        for (var index = 0; index < audioStreams.Length; index++)
        {
            var audioId = asset.Kind == MediaKind.Audio && index == 0 ? clip.Id : Guid.NewGuid();
            additions.Add((KadrStudio.Core.Domain.TrackKind.Audio, firstAudioTrack + index,
                CreateCoreClip(asset.Id, audioId, clip.Start, clip.SourceStart, clip.Duration,
                    linkGroupId, video: false, audioStreams[index].StreamIndex)));
        }

        var subtitleStreams = streams.Where(item => item.Kind == KadrStudio.Core.Domain.MediaStreamKind.Subtitle).ToArray();
        for (var index = 0; index < subtitleStreams.Length; index++)
            subtitleAdditions.Add((index, new KadrStudio.Core.Domain.SubtitleClip(
                Guid.NewGuid(), asset.Id, subtitleStreams[index].StreamIndex, Guid.Empty,
                KadrStudio.Core.Domain.TimelineTime.FromSeconds(clip.Start),
                KadrStudio.Core.Domain.TimelineTime.FromSeconds(clip.SourceStart),
                KadrStudio.Core.Domain.TimelineTime.FromSeconds(clip.Duration), linkGroupId)));

        ExecuteCoreCommand("Клип добавлен на таймлайн",
            new EnsureTracksAndAddMediaStreamsCommand(additions, subtitleAdditions), clip.Id);
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

    public IReadOnlyList<CoreSequenceState> GetSequences()
        => _editorSession.State.Sequences.IsDefaultOrEmpty
            ? [_editorSession.State.EnsureSequenceContainer().ActiveSequence!]
            : _editorSession.State.Sequences;

    public void EnsureSequenceWorkspace()
    {
        if (_editorSession.State.Sequences.IsDefaultOrEmpty)
            ExecuteCoreCommand("Создан исходный вариант монтажа", new InitializeSequenceWorkspaceCommand());
    }

    public KadrStudio.Core.Domain.AiConversation GetAiConversation()
        => _editorSession.State.AiConversation;

    private static string DescribeAgentTaskForDebug(AgentTaskState task)
        =>
            $"project_id={task.ProjectId}\n" +
            $"source_sequence_id={task.SourceSequenceId}\n" +
            $"source_sequence_revision={task.SourceSequenceRevision?.ToString() ?? "null"}\n" +
            $"draft_sequence_id={task.DraftSequenceId?.ToString() ?? "null"}\n" +
            $"checkpoint_stage={task.Checkpoint?.Stage.ToString() ?? "null"}\n" +
            $"failure={task.FailureMessage ?? string.Empty}\n" +
            $"completion={task.CompletionSummary ?? string.Empty}";

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

    public async Task<Guid> RunUpscaleAsync(
        Guid sequenceId,
        IReadOnlyList<Guid> visualTrackIds,
        KadrStudio.Core.Domain.UpscaleScaleMode scaleMode,
        IProgress<KadrStudio.Core.Domain.UpscaleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSequenceWorkspace();
        if (_editorSession.State.ActiveSequenceId != sequenceId ||
            _editorSession.State.FindSequence(sequenceId) is null)
            throw new InvalidOperationException("Открытый таймлайн изменился. Запустите апскейл ещё раз.");
        var ids = visualTrackIds.Distinct().ToImmutableArray();
        if (ids.IsDefaultOrEmpty) throw new InvalidOperationException("Выберите видеодорожки для AnimeSR-X.");
        var activeTrackIds = _editorSession.State.Tracks
            .Where(item => item.Kind == KadrStudio.Core.Domain.TrackKind.Visual)
            .Select(item => item.Id)
            .ToHashSet();
        if (ids.Any(id => !activeTrackIds.Contains(id)))
            throw new InvalidOperationException("Выбранные дорожки не относятся к открытому таймлайну.");
        var now = DateTimeOffset.UtcNow;
        var job = new KadrStudio.Core.Domain.UpscaleJob(
            Guid.NewGuid(), sequenceId, ids, scaleMode,
            KadrStudio.Core.Domain.UpscaleJobState.Running, 0,
            "AnimeSR-X запускается на сервере", string.Empty, now, now);
        ExecuteCoreCommand("AnimeSR-X поставлен в очередь", new UpsertUpscaleJobCommand(job));
        try
        {
            var result = await AiUpscaleService.UpscaleAsync(
                _editorSession.State, job.Id,
                new KadrStudio.Core.Domain.UpscaleRequest(sequenceId, ids, scaleMode),
                progress, cancellationToken).ConfigureAwait(true);
            var completed = job with
            {
                State = KadrStudio.Core.Domain.UpscaleJobState.Succeeded,
                Progress = 1,
                Message = "AnimeSR-X завершён",
                ModelSha256 = result.Tracks.Select(item => item.RenditionGroup.ModelSha256).FirstOrDefault() ?? string.Empty,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var commands = result.Tracks.Select(track => (IEditCommand)new PublishUpscaleRenditionCommand(
                    track.Sources, track.UpscaledTrack, track.Clips, track.RenditionGroup))
                .Append(new UpsertUpscaleJobCommand(completed))
                .ToArray();
            ExecuteCoreCommand("Дорожки AnimeSR-X добавлены", new EditBatchCommand("Опубликовать AnimeSR-X", commands));
            return job.Id;
        }
        catch (OperationCanceledException)
        {
            ExecuteCoreCommand("AnimeSR-X отменён", new UpsertUpscaleJobCommand(job with
            {
                State = KadrStudio.Core.Domain.UpscaleJobState.Cancelled,
                Message = "AnimeSR-X отменён",
                UpdatedAt = DateTimeOffset.UtcNow
            }));
            throw;
        }
        catch (Exception exception)
        {
            ExecuteCoreCommand("Ошибка AnimeSR-X", new UpsertUpscaleJobCommand(job with
            {
                State = KadrStudio.Core.Domain.UpscaleJobState.Failed,
                Message = "AnimeSR-X завершился ошибкой",
                Error = exception.Message,
                UpdatedAt = DateTimeOffset.UtcNow
            }));
            throw;
        }
    }

    public bool SetTrackRendition(Guid groupId, KadrStudio.Core.Domain.TrackRenditionKind rendition)
        => ExecuteCoreCommand("Вариант видеодорожки переключён", new SetTrackRenditionCommand(groupId, rendition));

    public bool ToggleTrackMute(Guid trackId)
    {
        var track = _editorSession.State.FindTrack(trackId);
        if (track is null || track.Kind != KadrStudio.Core.Domain.TrackKind.Audio)
        {
            StatusText = "Заглушить можно только аудиодорожку";
            return false;
        }
        return ExecuteCoreCommand(
            track.IsMuted ? $"Звук дорожки {track.Name} включён" : $"Дорожка {track.Name} заглушена",
            new UpdateTrackCommand(track with { IsMuted = !track.IsMuted }));
    }

    public bool ToggleTrackVisibility(Guid trackId)
    {
        var track = _editorSession.State.FindTrack(trackId);
        if (track is null || track.Kind == KadrStudio.Core.Domain.TrackKind.Audio)
        {
            StatusText = "Скрывать можно видео, текст и субтитры";
            return false;
        }
        return ExecuteCoreCommand(
            track.IsVisible ? $"Дорожка {track.Name} скрыта" : $"Дорожка {track.Name} показана",
            new UpdateTrackCommand(track with { IsVisible = !track.IsVisible }));
    }

    public bool SetActiveSequenceRendition(KadrStudio.Core.Domain.TrackRenditionKind rendition)
    {
        var activeSequenceId = _editorSession.State.ActiveSequenceId;
        var commands = _editorSession.State.RenditionGroups
            .Where(group => group.SequenceId == activeSequenceId && group.ActiveRendition != rendition)
            .Select(group => (IEditCommand)new SetTrackRenditionCommand(group.Id, rendition))
            .ToArray();
        return commands.Length == 0 || ExecuteCoreCommand(
            rendition == KadrStudio.Core.Domain.TrackRenditionKind.Original
                ? "Открыт оригинальный вариант дорожек"
                : "Открыт вариант AnimeSR-X",
            new EditBatchCommand("Переключить все производные дорожки", commands));
    }

    public Task<KadrStudio.Application.Upscaling.UpscaleComparisonFrames> CreateUpscaleComparisonFramesAsync(
        Guid groupId,
        double timelineSeconds,
        CancellationToken cancellationToken = default)
        => AiUpscaleService.CreateComparisonFramesAsync(
            _editorSession.State, groupId,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(Math.Max(0, timelineSeconds)),
            cancellationToken);

    public void PersistAgentTaskState(AgentTaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);
        SaveAiConversation(BuildConversationWithAgentTask(
            GetAiConversation(),
            task));
    }

    private static KadrStudio.Core.Domain.AiConversation BuildConversationWithAgentTask(
        KadrStudio.Core.Domain.AiConversation conversation,
        AgentTaskState task)
    {
        var payload = JsonSerializer.Serialize(
            AgentTaskPersistenceEnvelope.Create(task));
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
        return existing is null
            ? conversation with
            {
                Messages = conversation.Messages.Add(memory),
                UpdatedAt = DateTimeOffset.UtcNow
            }
            : conversation with
            {
                Messages = conversation.Messages.Replace(existing, memory),
                UpdatedAt = DateTimeOffset.UtcNow
            };
    }

    public AgentTaskState StartAgentTask(string userRequest)
    {
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
            sequence.Revision,
            RequestedEditorialSourceIds);
    }

    private async Task<AgentTaskState> RunEditorialAgentAsync(CancellationToken cancellationToken)
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException("Нет активной задачи агента.");
        if (task.Phase is not (AgentTaskPhase.Indexing or AgentTaskPhase.Directing or
                AgentTaskPhase.Retrieving or AgentTaskPhase.RoughCut or AgentTaskPhase.BoundaryRefining or
                AgentTaskPhase.Compiling or AgentTaskPhase.Verifying))
            return task;

        var snapshot = _editorSession.State.EnsureSequenceContainer().SynchronizeActiveSequence();
        if (snapshot.Id != task.ProjectId)
            throw new AgentTaskTransitionException("Проект сменился до запуска режиссёрского конвейера.");
        var source = snapshot.FindSequence(task.SourceSequenceId)
            ?? throw new AgentTaskTransitionException("Исходный таймлайн задачи больше не найден.");
        if (task.SourceSequenceRevision is { } expectedRevision && source.Revision != expectedRevision)
            throw new AgentTaskTransitionException("Исходный таймлайн изменился; перезапустите задачу на новой ревизии.");

        try
        {
            MoveEditorialTaskTo(AgentTaskPhase.Indexing, "Начинаю постоянный мультимодальный индекс.");
            var progress = new InlineProgress<EditorialPipelineProgress>(update =>
            {
                var phase = update.Stage switch
                {
                    EditorialPipelineStage.Indexing => AgentTaskPhase.Indexing,
                    EditorialPipelineStage.Directing => AgentTaskPhase.Directing,
                    EditorialPipelineStage.Retrieving => AgentTaskPhase.Retrieving,
                    EditorialPipelineStage.RoughCut => AgentTaskPhase.RoughCut,
                    EditorialPipelineStage.BoundaryRefining => AgentTaskPhase.BoundaryRefining,
                    EditorialPipelineStage.Compiling => AgentTaskPhase.Compiling,
                    EditorialPipelineStage.Verifying => AgentTaskPhase.Verifying,
                    _ => AiAgentOrchestrator.CurrentTask?.Phase ?? AgentTaskPhase.BoundaryRefining
                };
                MoveEditorialTaskTo(phase, update.Message);
                if (!update.AssetIds.IsDefaultOrEmpty ||
                    !update.JobIds.IsDefaultOrEmpty ||
                    !update.ArtifactIds.IsDefaultOrEmpty)
                {
                    var checkpoint = AiAgentOrchestrator.CurrentTask?.Checkpoint;
                    AiAgentOrchestrator.SaveCheckpoint(new EditorialTaskCheckpoint(
                        phase,
                        (checkpoint?.AssetIds ?? []).Concat(update.AssetIds).Distinct().ToImmutableArray(),
                        (checkpoint?.JobIds ?? []).Concat(update.JobIds).Distinct().ToImmutableArray(),
                        (checkpoint?.ArtifactIds ?? []).Concat(update.ArtifactIds).Distinct().ToImmutableArray(),
                        checkpoint?.ProfileId,
                        checkpoint?.Intent,
                        checkpoint?.EvidenceFingerprint,
                        checkpoint?.GraphFingerprint,
                        DateTimeOffset.UtcNow));
                }
                StatusText = update.Message;
            });
            var previousProfile = snapshot.MontageGraphs
                .Where(item => item.TaskId == task.Id)
                .OrderByDescending(item => item.Revision)
                .Select(item => item.Brief.Profile)
                .FirstOrDefault();
            var requestedProfile = previousProfile ??
                (RequestedMontageProfileKind is { } requestedKind
                    ? KadrStudio.Core.Domain.MontageProfileCatalog.Get(requestedKind)
                    : null);
            var result = await EditorialPipeline.RunAsync(
                new EditorialPipelineRequest(
                    snapshot,
                    source.Id,
                    task.UserRequest,
                    Profile: requestedProfile,
                    RevisionFeedback: task.RevisionFeedback,
                    TaskId: task.Id,
                    TargetSourceIds: task.SafeTargetSourceIds),
                progress,
                cancellationToken);

            result = await ConfirmAnimeBoundariesAsync(snapshot, task, result, cancellationToken);

            AiAgentOrchestrator.SaveCheckpoint(new EditorialTaskCheckpoint(
                AgentTaskPhase.Verifying,
                result.Indexes.AssetIds,
                result.Indexes.JobIds,
                result.Indexes.ArtifactIds,
                result.Profile.Id,
                result.Brief.ScopePolicy?.Intent.ToString(),
                result.Indexes.EvidenceFingerprint(),
                result.Graph.Fingerprint(),
                DateTimeOffset.UtcNow));

            var current = _editorSession.State.EnsureSequenceContainer().SynchronizeActiveSequence();
            var currentSource = current.FindSequence(source.Id);
            if (current.Id != snapshot.Id || currentSource?.Revision != source.Revision)
                throw new AgentTaskTransitionException(
                    "Исходный таймлайн изменился во время анализа; готовый граф не будет применён к другой ревизии.");

            var commands = new List<IEditCommand>();
            commands.AddRange(result.Indexes.Indexes.Select(index =>
                (IEditCommand)new UpsertMediaUnderstandingIndexCommand(index)));
            commands.Add(new UpsertMontageGraphCommand(result.Graph));
            commands.Add(new ReplaceDraftPatchesCommand(result.Graph.Id, result.Patches));
            commands.Add(new CreateSequenceCommand(result.Compilation.Draft.Sequence, Activate: true));
            commands.Add(new ReplaceDraftCommandReceiptsCommand(
                result.Compilation.Draft.Sequence.Id, result.Compilation.Receipts));
            commands.Add(new UpsertDraftQualityReportCommand(result.QualityReport));
            if (!ExecuteAgentCoreTransaction("Kadr AI Editor v2 создал и проверил Agent Draft", commands.ToArray()))
                throw new InvalidOperationException("Не удалось атомарно сохранить результат режиссёрского конвейера.");

            var quality = result.QualityReport.Status switch
            {
                KadrStudio.Core.Domain.DraftQualityStatus.Passed => "QC пройден",
                KadrStudio.Core.Domain.DraftQualityStatus.NeedsReview => "QC требует внимания",
                _ => "QC обнаружил блокирующие проблемы"
            };
            var reviewing = AiAgentOrchestrator.ReadyForCompiledDraftReview(
                result.Compilation.Draft.Sequence.Id,
                $"Agent Draft готов к A/B-просмотру. {quality}. " +
                $"Профиль: {result.Profile.DisplayName}; решений: {result.Graph.Decisions.Length}. " +
                $"Удаления: {FormatRemovedRanges(result.Compilation.RemovedTimelineRanges)}. " +
                $"Diff: A {source.Duration.TotalSeconds:F1} с / {source.MediaClips.Length} клипов → " +
                $"B {result.Compilation.Draft.Sequence.Duration.TotalSeconds:F1} с / " +
                $"{result.Compilation.Draft.Sequence.MediaClips.Length} клипов; " +
                $"QC-проблем: {result.QualityReport.Issues.Length}.");
            StatusText = "Agent Draft готов: сравните результат с исходником";
            return reviewing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var current = AiAgentOrchestrator.CurrentTask;
            if (current is { IsTerminal: false })
                return AiAgentOrchestrator.Fail("Режиссёрский конвейер остановлен: " + exception.Message);
            throw;
        }
    }

    private async Task<EditorialPipelineResult> ConfirmAnimeBoundariesAsync(
        KadrStudio.Core.Domain.ProjectState project,
        AgentTaskState task,
        EditorialPipelineResult result,
        CancellationToken cancellationToken)
    {
        if (result.Profile.Kind != KadrStudio.Core.Domain.MontageProfileKind.AnimeEpisode ||
            result.Brief.ScopePolicy?.Intent != KadrStudio.Core.Domain.EditorialIntentKind.RemoveNamedSections)
            return result;
        var candidates = ImmutableArray.CreateBuilder<KadrStudio.Core.Domain.BoundaryCandidate>();
        var removalGroups = result.Graph.Decisions
            .Where(item => item.Kind == KadrStudio.Core.Domain.EditDecisionKind.Remove)
            .GroupBy(item => new { item.SourceId, item.SegmentRole })
            .OrderBy(group => group.Min(item => item.Order))
            .ToArray();
        foreach (var group in removalGroups)
        {
            var decision = group.First();
            var index = result.Indexes.Find(decision.SourceId)
                ?? throw new InvalidOperationException("Индекс границ аниме-сегмента не найден.");
            var hypothesis = index.SegmentRoleHypotheses
                .Where(item => item.Role == decision.SegmentRole && item.SourceRange.Overlaps(decision.SourceRange))
                .OrderByDescending(item => item.Confidence)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Кандидат границ {decision.SegmentRole} не найден.");
            var source = project.Sources[decision.SourceId];
            var rate = source.FrameRate ?? project.FindSequence(result.Graph.SourceSequenceId)?.Settings.FrameRate
                ?? KadrStudio.Core.Domain.FrameRate.Fps24;
            candidates.Add(CreateBoundaryCandidate(hypothesis, hypothesis.StartBoundary, rate, source.Name, isStart: true));
            candidates.Add(CreateBoundaryCandidate(hypothesis, hypothesis.EndBoundary, rate, source.Name, isStart: false));
        }
        var all = candidates.ToImmutable();
        var selectedEpisodes = removalGroups.Select(group => group.Key.SourceId).Distinct().Count();
        var completeEpisodePairs = removalGroups
            .GroupBy(group => group.Key.SourceId)
            .All(groups =>
                groups.Count(group => group.Key.SegmentRole == KadrStudio.Core.Domain.SegmentRole.Opening) == 1 &&
                groups.Count(group => group.Key.SegmentRole == KadrStudio.Core.Domain.SegmentRole.Ending) == 1);
        if (selectedEpisodes == 0 || !completeEpisodePairs || all.Length != selectedEpisodes * 4)
            throw new InvalidOperationException(
                "Для каждой выбранной серии нужны четыре границы: начало/конец OP и ED.");
        if (!all.Any(item => item.RequiresConfirmation)) return result;
        if (BoundaryConfirmationHandler is null)
            throw new InvalidOperationException("Найдены границы средней уверенности; требуется покадровое подтверждение пользователя.");

        var confirmations = await BoundaryConfirmationHandler(
            new KadrStudio.Core.Domain.BoundaryReviewRequest(task.Id, all), cancellationToken);
        if (confirmations.Length != all.Length || confirmations.Any(item => !item.Accepted) ||
            all.Any(candidate => confirmations.All(item => item.CandidateId != candidate.Id)))
            throw new OperationCanceledException("Покадровое подтверждение границ отменено.", cancellationToken);

        var updated = result.Graph.Decisions.Select(decision =>
        {
            if (decision.Kind != KadrStudio.Core.Domain.EditDecisionKind.Remove) return decision;
            var pair = all.Where(item => item.SourceId == decision.SourceId && item.Role == decision.SegmentRole).ToArray();
            var startCandidate = pair.Single(item => item.IsStart);
            var endCandidate = pair.Single(item => !item.IsStart);
            var start = confirmations.Single(item => item.CandidateId == startCandidate.Id).ConfirmedTime;
            var end = confirmations.Single(item => item.CandidateId == endCandidate.Id).ConfirmedTime;
            if (end <= start) throw new InvalidOperationException($"Границы {decision.SegmentRole} расположены в неверном порядке.");
            return decision with
            {
                SourceRange = new KadrStudio.Core.Domain.TimeRange(start, end - start),
                Target = null,
                Parameters = decision.Parameters
                    .SetItem("boundaryConfirmed", "true")
                    .SetItem("boundaryStartTicks", start.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .SetItem("boundaryEndTicks", end.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
        }).ToImmutableArray();
        var graph = new MontageGraphTargetResolver().Resolve(project, result.Graph with
        {
            Decisions = updated,
            Revision = result.Graph.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            ParentFingerprint = result.Graph.Fingerprint()
        });
        var validation = new MontageGraphValidatorV2().Validate(project, result.Indexes, graph);
        if (!validation.IsValid)
            throw new InvalidOperationException("Подтверждённые границы не прошли проверку: " +
                                                string.Join("; ", validation.Errors.Concat(validation.Gaps.Select(item => item.Message))));
        var compilation = new MontageGraphCompilerV2().Compile(project, result.Indexes, graph, result.Patches);
        var quality = new DeterministicDraftQualityAnalyzer().Analyze(project, task.Id, compilation.Draft.Sequence, graph, []);
        return result with { Graph = graph, Compilation = compilation, QualityReport = quality };
    }

    private static KadrStudio.Core.Domain.BoundaryCandidate CreateBoundaryCandidate(
        KadrStudio.Core.Domain.SegmentRoleHypothesis hypothesis,
        KadrStudio.Core.Domain.CandidateBoundary boundary,
        KadrStudio.Core.Domain.FrameRate frameRate,
        string sourceLabel,
        bool isStart)
        => new(
            boundary.Id, hypothesis.SourceId, hypothesis.Role, isStart,
            boundary.Time.SnapToFrame(frameRate), frameRate,
            Math.Min(hypothesis.Confidence, boundary.Confidence),
            hypothesis.EvidenceFactIds.Concat(boundary.EvidenceFactIds).Distinct().ToImmutableArray(),
            hypothesis.EvidenceChannels, hypothesis.AnalyzerId, hypothesis.AnalyzerVersion, sourceLabel);

    private void MoveEditorialTaskTo(AgentTaskPhase phase, string message)
    {
        var current = AiAgentOrchestrator.CurrentTask;
        if (current is null || current.IsTerminal) return;
        if (current.Phase != phase)
        {
            var transitioned = AiAgentOrchestrator.BeginEditorialStage(phase, message);
            var previous = transitioned.Checkpoint;
            AiAgentOrchestrator.SaveCheckpoint(new EditorialTaskCheckpoint(
                phase,
                previous?.AssetIds ?? [],
                previous?.JobIds ?? [],
                previous?.ArtifactIds ?? [],
                previous?.ProfileId,
                previous?.Intent,
                previous?.EvidenceFingerprint,
                previous?.GraphFingerprint,
                DateTimeOffset.UtcNow));
        }
        else
            AiAgentOrchestrator.RecordProgress(message);
    }

    public AgentTaskState CancelAgentTask(string? reason = null)
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException(
                "Нет активной задачи агента.");

        return task.IsTerminal
            ? task
            : AiAgentOrchestrator.Cancel(
                string.IsNullOrWhiteSpace(reason)
                    ? "Задача отменена пользователем."
                    : reason);
    }

    public AgentTaskState AcceptAgentDraft()
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException("Нет активной задачи агента.");
        if (task.Phase != AgentTaskPhase.ReviewingDraft || task.DraftSequenceId is not { } draftId)
            throw new AgentTaskTransitionException("Нет Agent Draft, ожидающего принятия.");
        var blockingQuality = _editorSession.State.DraftQualityReports
            .Where(report => report.TaskId == task.Id && report.DraftSequenceId == draftId)
            .OrderByDescending(report => report.CreatedAt)
            .FirstOrDefault()?.Status == KadrStudio.Core.Domain.DraftQualityStatus.Failed;
        if (blockingQuality)
            throw new AgentTaskTransitionException(
                "Agent Draft не прошёл обязательный QC. Запросите переработку или удалите Draft.");
        if (!ExecuteAgentCoreTransaction(
                "Agent Draft принят",
                new SetSequenceStatusCommand(draftId, KadrStudio.Core.Domain.SequenceStatus.Accepted)))
            throw new InvalidOperationException("Не удалось принять Agent Draft.");
        return AiAgentOrchestrator.AcceptDraft();
    }

    public AgentTaskState DiscardAgentDraft()
    {
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException("Нет активной задачи агента.");
        if (task.Phase != AgentTaskPhase.ReviewingDraft || task.DraftSequenceId is not { } draftId)
            throw new AgentTaskTransitionException("Нет Agent Draft, ожидающего удаления.");
        if (!ExecuteAgentCoreTransaction(
                "Agent Draft удалён",
                new DeleteDraftSequenceCommand(draftId)))
            throw new InvalidOperationException("Не удалось удалить Agent Draft.");
        return AiAgentOrchestrator.DiscardDraft();
    }

    public AgentTaskState ReviseAgentDraft(string feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback))
            throw new ArgumentException("Укажите, что изменить в монтаже.", nameof(feedback));
        var task = AiAgentOrchestrator.CurrentTask
            ?? throw new AgentTaskTransitionException("Нет активной задачи агента.");
        if (task.Phase != AgentTaskPhase.ReviewingDraft || task.DraftSequenceId is not { } draftId)
            throw new AgentTaskTransitionException("Переработать можно только готовый Agent Draft.");
        if (!ExecuteAgentCoreTransaction(
                "Предыдущий Agent Draft удалён перед переработкой",
                new DeleteDraftSequenceCommand(draftId)))
            throw new InvalidOperationException("Не удалось подготовить переработку Agent Draft.");
        return AiAgentOrchestrator.ReviseDraft(feedback);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(T value)
        {
            if (_context is null || ReferenceEquals(_context, SynchronizationContext.Current))
                report(value);
            else
                _context.Send(_ => report(value), null);
        }
    }

    public bool ActivateSequence(Guid sequenceId)
        => ExecuteCoreCommand("Вариант монтажа открыт", new ActivateSequenceCommand(sequenceId));

    public void UpsertSourceAnnotation(CoreSourceAnnotation annotation)
        => ExecuteCoreCommand("Указание для ИИ сохранено", new UpsertSourceAnnotationCommand(annotation));

    public void DeleteSourceAnnotation(Guid annotationId)
        => ExecuteCoreCommand("Указание для ИИ удалено", new DeleteSourceAnnotationCommand(annotationId));

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
        await _projectService.DeleteAutosaveAsync(cancellationToken);
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
            await _projectService.DeleteAutosaveAsync(cancellationToken);
            var project = await _projectService.OpenAsync(path, cancellationToken);
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
        _timelineMediaPreparationCancellation.Cancel();
        Task[] pendingTimelineMedia;
        lock (_timelineMediaPreparationGate) pendingTimelineMedia = _timelineMediaPreparationTasks.Values.ToArray();
        try { await Task.WhenAll(pendingTimelineMedia); } catch (OperationCanceledException) { }
        _timelineMediaPreparationCancellation.Dispose();
        await _automationScheduler.DisposeAsync();
        await ThumbnailService.DisposeAsync();
        await TimelineMediaCacheService.DisposeAsync();
        await _renderCoordinator.DisposeAsync();
        await _artifactStore.DisposeAsync();
        _projectService.Dispose();
        AiServer.Dispose();
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
        bool video,
        int? streamIndex = null)
        => new(
            clipId,
            sourceId,
            Guid.Empty,
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(start),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(sourceStart),
            KadrStudio.Core.Domain.TimelineTime.FromSeconds(duration),
            linkGroupId,
            video ? new KadrStudio.Core.Domain.VideoParameters() : null,
            video ? null : new KadrStudio.Core.Domain.AudioParameters(),
            streamIndex);

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
            AgentTaskState? task;
            var formatVersion = 1;
            try
            {
                var envelope = JsonSerializer.Deserialize<AgentTaskPersistenceEnvelope>(memory.Text);
                task = envelope?.Task;
                if (task is not null)
                {
                    formatVersion = envelope!.FormatVersion;
                }
            }
            catch (JsonException)
            {
                task = null;
            }
            task ??= JsonSerializer.Deserialize<AgentTaskState>(memory.Text);
            if (task is null || task.ProjectId != _editorSession.State.Id)
            {
                return;
            }

            task = AgentRecoveryService.Reconcile(task, formatVersion);
            AiAgentOrchestrator.RestoreTask(task);
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

    private bool ExecuteAgentCoreTransaction(
        string description,
        params IEditCommand[] commands)
    {
        _agentMutationDepth++;
        try
        {
            var result = _editorSession.Execute(new EditTransaction(
                description,
                commands));
            if (!result.Changed) return false;
            RestoreFromCoreState(result.State, null, description);
            StatusText = description;
            return true;
        }
        finally
        {
            _agentMutationDepth--;
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
            KadrStudio.Core.Domain.MediaSourceFingerprint.Stable(source));
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
                !KadrStudio.Core.Domain.MediaSourceFingerprint.Stable(currentSource)
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

    private static string FormatRemovedRanges(ImmutableArray<KadrStudio.Core.Domain.TimeRange> ranges)
        => ranges.IsDefaultOrEmpty
            ? "нет"
            : string.Join("; ", ranges.Select(range =>
                $"{TimeSpan.FromTicks(range.Start.Ticks):hh\\:mm\\:ss\\.fff}–" +
                $"{TimeSpan.FromTicks(range.End.Ticks):hh\\:mm\\:ss\\.fff}"));
}
