using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public enum EditorialPipelineStage
{
    Indexing,
    Directing,
    Retrieving,
    RoughCut,
    BoundaryRefining,
    Refining = BoundaryRefining,
    Compiling,
    Verifying,
    ReviewingDraft
}

public sealed record EditorialPipelineProgress(
    EditorialPipelineStage Stage,
    double Progress,
    string Message,
    ImmutableArray<string> AssetIds = default,
    ImmutableArray<string> JobIds = default,
    ImmutableArray<string> ArtifactIds = default)
{
    public ImmutableArray<string> AssetIds { get; init; } = AssetIds.IsDefault ? [] : AssetIds;
    public ImmutableArray<string> JobIds { get; init; } = JobIds.IsDefault ? [] : JobIds;
    public ImmutableArray<string> ArtifactIds { get; init; } = ArtifactIds.IsDefault ? [] : ArtifactIds;
}

public sealed record EditorialPipelineRequest(
    ProjectState Project,
    Guid SourceSequenceId,
    string UserRequest,
    MontageProfile? Profile = null,
    string RevisionFeedback = "",
    Guid? TaskId = null,
    ImmutableArray<Guid> TargetSourceIds = default)
{
    public ImmutableArray<Guid> TargetSourceIds { get; init; } =
        TargetSourceIds.IsDefault ? [] : TargetSourceIds.Distinct().ToImmutableArray();
}

public sealed record MediaUnderstandingCatalog(
    ImmutableArray<MediaUnderstandingIndex> Indexes,
    ImmutableArray<string> AssetIds = default,
    ImmutableArray<string> JobIds = default,
    ImmutableArray<string> ArtifactIds = default)
{
    public ImmutableArray<MediaUnderstandingIndex> Indexes { get; init; } =
        Indexes.IsDefault ? [] : Indexes;
    public ImmutableArray<string> AssetIds { get; init; } = AssetIds.IsDefault ? [] : AssetIds;
    public ImmutableArray<string> JobIds { get; init; } = JobIds.IsDefault ? [] : JobIds;
    public ImmutableArray<string> ArtifactIds { get; init; } = ArtifactIds.IsDefault ? [] : ArtifactIds;

    public MediaUnderstandingIndex? Find(Guid sourceId)
        => Indexes.FirstOrDefault(item => item.SourceId == sourceId);

    public string EvidenceFingerprint()
        => string.Join('|', Indexes
            .OrderBy(item => item.SourceId)
            .Select(RejectedMontageGraphLedger.EvidenceFingerprint));
}

public sealed record EditorialWorkingSet(
    ImmutableArray<Guid> IndexIds,
    ImmutableArray<ChapterNode> Chapters,
    ImmutableArray<SceneNode> Scenes,
    ImmutableArray<ShotNode> Shots,
    ImmutableArray<TemporalFact> Facts,
    ImmutableArray<SemanticHypothesis> Hypotheses,
    ImmutableArray<AudioEvent> AudioEvents,
    ImmutableArray<SpeakerTurn> SpeakerTurns,
    ImmutableArray<TemporalFact> CounterEvidence,
    ImmutableDictionary<Guid, ImmutableArray<CoverageChannel>> AvailableChannels,
    ImmutableArray<SegmentRoleHypothesis> SegmentRoleHypotheses = default)
{
    public int NodeCount => Chapters.Length + Scenes.Length + Shots.Length;

    public ImmutableDictionary<Guid, ImmutableArray<CoverageChannel>> AvailableChannels { get; init; } =
        AvailableChannels ?? ImmutableDictionary<Guid, ImmutableArray<CoverageChannel>>.Empty;
    public ImmutableArray<SegmentRoleHypothesis> SegmentRoleHypotheses { get; init; } =
        SegmentRoleHypotheses.IsDefault ? [] : SegmentRoleHypotheses;
}

public sealed record SemanticRetrievalDocument(
    Guid NodeId,
    Guid SourceId,
    TimeRange SourceRange,
    string Text,
    string EmbeddingReference = "");

public interface ISemanticNodeRanker
{
    Task<ImmutableDictionary<Guid, double>> RankAsync(
        string query,
        ImmutableArray<SemanticRetrievalDocument> documents,
        CancellationToken cancellationToken);
}

public sealed record MontageGraphValidationResult(
    bool IsValid,
    ImmutableArray<EvidenceGap> Gaps,
    ImmutableArray<string> Errors)
{
    public static MontageGraphValidationResult Valid { get; } = new(true, [], []);
}

public sealed record MontageGraphCompilationResult(
    NativeDraftCompilation Draft,
    ImmutableArray<string> Warnings,
    ImmutableArray<TimeRange> RemovedTimelineRanges = default,
    ImmutableArray<DraftCommandReceipt> Receipts = default,
    DraftTimelineDiff? Diff = null)
{
    public ImmutableArray<TimeRange> RemovedTimelineRanges { get; init; } =
        RemovedTimelineRanges.IsDefault ? [] : RemovedTimelineRanges;
    public ImmutableArray<DraftCommandReceipt> Receipts { get; init; } =
        Receipts.IsDefault ? [] : Receipts;
}

public sealed record NativeDraftCompilation(
    SequenceState Sequence,
    ImmutableArray<string> Warnings);

public sealed record EditorialPipelineResult(
    MontageProfile Profile,
    MediaUnderstandingCatalog Indexes,
    EditorialBrief Brief,
    MontageGraph Graph,
    ImmutableArray<DraftPatch> Patches,
    MontageGraphCompilationResult Compilation,
    DraftQualityReport QualityReport);

public interface IMediaUnderstandingIndexer
{
    Task<MediaUnderstandingCatalog> EnsureIndexesAsync(
        ProjectState project,
        Guid sourceSequenceId,
        MontageProfile profile,
        IProgress<EditorialPipelineProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IEditorialDirector
{
    Task<MontageProfileKind> SuggestProfileAsync(
        ProjectState project,
        Guid sourceSequenceId,
        string userRequest,
        CancellationToken cancellationToken);

    Task<EditorialBrief> CreateBriefAsync(
        ProjectState project,
        Guid sourceSequenceId,
        string userRequest,
        MontageProfile profile,
        string revisionFeedback,
        CancellationToken cancellationToken);

    Task<MontageGraph> CreateRoughCutAsync(
        EditorialBrief brief,
        EditorialWorkingSet workingSet,
        Guid taskId,
        Guid sourceSequenceId,
        long sourceSequenceRevision,
        MontageGraph? previousRejectedGraph,
        CancellationToken cancellationToken);

    Task<DraftPatch> RefineAsync(
        EditorialPassKind pass,
        EditorialBrief brief,
        MontageGraph graph,
        EditorialWorkingSet workingSet,
        int order,
        CancellationToken cancellationToken);
}

public interface IEditorialCritic
{
    Task<ImmutableArray<DraftQualityIssue>> ReviewGraphAsync(
        EditorialBrief brief,
        MontageGraph graph,
        EditorialWorkingSet workingSet,
        CancellationToken cancellationToken);
}

public interface IHierarchicalMediaRetriever
{
    Task<EditorialWorkingSet> RetrieveAsync(
        MediaUnderstandingCatalog indexes,
        EditorialBrief brief,
        ImmutableArray<EvidenceGap> gaps = default,
        CancellationToken cancellationToken = default);
}

public interface IMontageGraphValidatorV2
{
    MontageGraphValidationResult Validate(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        MontageGraph graph);
}

public interface IEvidenceGapResolver
{
    Task<MediaUnderstandingCatalog> ResolveAsync(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        ImmutableArray<EvidenceGap> gaps,
        IProgress<EditorialPipelineProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IMontageGraphCompilerV2
{
    MontageGraphCompilationResult Compile(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        MontageGraph graph,
        ImmutableArray<DraftPatch> patches);
}

public interface IDraftQualityAnalyzer
{
    DraftQualityReport Analyze(
        ProjectState sourceProject,
        Guid taskId,
        SequenceState draft,
        MontageGraph graph,
        ImmutableArray<DraftQualityIssue> criticIssues);
}
