using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KadrStudio.Core.Domain;

public enum MontageProfileKind
{
    Generic,
    FilmSeries,
    AnimeEpisode,
    TalkingHead,
    Podcast,
    ShortFormHighlights
}

public enum EditorialIntentKind
{
    GeneralMontage,
    RemoveNamedSections
}

public enum PreservationPolicy
{
    Flexible,
    ExactComplement
}

public enum SegmentRole
{
    Unknown,
    Opening,
    Ending,
    EpisodeBody,
    PostCredits,
    Preview,
    Recap,
    SponsorCard
}

public enum CoverageChannel
{
    Frames,
    Motion,
    Audio,
    Transcript,
    Ocr
}

public enum TemporalFactKind
{
    VisualDescription,
    Motion,
    Ocr,
    ShotBoundary,
    TechnicalDefect,
    TranscriptWord,
    SpeakerTurn,
    SpeechActivity,
    Music,
    AudioEvent,
    Loudness,
    Embedding
}

public enum EditDecisionKind
{
    Keep,
    Remove,
    Reorder,
    SelectTake,
    InsertBroll,
    ApplyDialogueCut,
    ApplyJlCut,
    ApplyAudioMix,
    ApplyCaptionTrack,
    AutoReframe,
    Retime
}

public enum EditorialPassKind
{
    StoryContinuity,
    Rhythm,
    DialogueAudio,
    CompositionReframe,
    Captions,
    QualityControl
}

public enum EvidenceGapReason
{
    MissingCoverage,
    InsufficientDensity,
    MissingBoundaryProbe,
    ConflictingFacts,
    StaleAnalysis,
    UnsupportedCapability
}

public enum DraftQualityStatus
{
    Passed,
    NeedsReview,
    Failed
}

/// <summary>
/// One actually measured interval. Sparse sampling is intentionally distinct
/// from continuous coverage and can never silently prove the time between samples.
/// </summary>
public sealed record CoverageInterval(
    TimeRange Range,
    int SampleCount,
    double SamplingDensityHz,
    bool IsContinuous,
    string AnalyzerId,
    string AnalyzerVersion,
    double Confidence = 1)
{
    public static CoverageInterval Create(
        TimeRange range,
        int sampleCount,
        bool isContinuous,
        string analyzerId,
        string analyzerVersion,
        double confidence = 1)
    {
        if (sampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (string.IsNullOrWhiteSpace(analyzerId)) throw new ArgumentException("Analyzer id is required.", nameof(analyzerId));
        if (string.IsNullOrWhiteSpace(analyzerVersion)) throw new ArgumentException("Analyzer version is required.", nameof(analyzerVersion));
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        return new CoverageInterval(
            range,
            sampleCount,
            sampleCount / Math.Max(range.Duration.TotalSeconds, 0.000001),
            isContinuous,
            analyzerId.Trim(),
            analyzerVersion.Trim(),
            confidence);
    }
}

/// <summary>
/// Per-channel measured coverage for one immutable source fingerprint.
/// </summary>
public sealed record CoverageMap(
    Guid SourceId,
    string SourceFingerprint,
    TimelineTime SourceDuration,
    ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>> Channels)
{
    private static readonly TimelineTime Tolerance = TimelineTime.FromSeconds(0.001);

    public ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>> Channels { get; init; } =
        Channels ?? ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty;

    public bool Covers(
        CoverageChannel channel,
        TimeRange requiredRange,
        double minimumDensityHz = 0,
        bool requireContinuous = false)
        => MissingRanges(channel, requiredRange, minimumDensityHz, requireContinuous).IsEmpty;

    public ImmutableArray<TimeRange> MissingRanges(
        CoverageChannel channel,
        TimeRange requiredRange,
        double minimumDensityHz = 0,
        bool requireContinuous = false)
    {
        if (!double.IsFinite(minimumDensityHz) || minimumDensityHz < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDensityHz));

        var eligible = Channels.GetValueOrDefault(channel, [])
            .Where(item =>
                item.Confidence > 0 &&
                item.SamplingDensityHz + 0.000001 >= minimumDensityHz &&
                (!requireContinuous || item.IsContinuous) &&
                item.Range.Overlaps(requiredRange))
            .Select(item => new TimeRange(
                item.Range.Start > requiredRange.Start ? item.Range.Start : requiredRange.Start,
                (item.Range.End < requiredRange.End ? item.Range.End : requiredRange.End) -
                (item.Range.Start > requiredRange.Start ? item.Range.Start : requiredRange.Start)))
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ToArray();

        if (eligible.Length == 0) return [requiredRange];

        var missing = ImmutableArray.CreateBuilder<TimeRange>();
        var cursor = requiredRange.Start;
        foreach (var interval in eligible)
        {
            if (interval.Start > cursor + Tolerance)
                missing.Add(new TimeRange(cursor, interval.Start - cursor));
            if (interval.End > cursor) cursor = interval.End;
            if (cursor >= requiredRange.End - Tolerance) break;
        }
        if (cursor < requiredRange.End - Tolerance)
            missing.Add(new TimeRange(cursor, requiredRange.End - cursor));
        return missing.ToImmutable();
    }

    public CoverageMap Add(CoverageChannel channel, CoverageInterval interval)
    {
        if (interval.Range.End > SourceDuration + Tolerance)
            throw new ArgumentOutOfRangeException(nameof(interval), "Coverage exceeds source duration.");
        var existing = Channels.GetValueOrDefault(channel, []);
        return this with { Channels = Channels.SetItem(channel, existing.Add(interval)) };
    }
}

public sealed record TemporalFact(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    CoverageChannel Channel,
    TemporalFactKind Kind,
    string Summary,
    double Confidence,
    string AnalyzerId,
    string AnalyzerVersion,
    ImmutableDictionary<string, string> Attributes,
    string ArtifactReference = "",
    int? StreamIndex = null)
{
    public ImmutableDictionary<string, string> Attributes { get; init; } =
        Attributes ?? ImmutableDictionary<string, string>.Empty;
}

/// <summary>
/// A model interpretation that is explicitly not a measured fact and cannot by
/// itself authorize an edit. EvidenceFactIds must point back to measured facts.
/// </summary>
public sealed record SemanticHypothesis(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    string Summary,
    double Confidence,
    ImmutableArray<Guid> EvidenceFactIds,
    string AnalyzerId,
    string AnalyzerVersion)
{
    public ImmutableArray<Guid> EvidenceFactIds { get; init; } =
        EvidenceFactIds.IsDefault ? [] : EvidenceFactIds;
}

public sealed record CandidateBoundary(
    Guid Id,
    Guid SourceId,
    TimelineTime Time,
    double Confidence,
    ImmutableArray<Guid> EvidenceFactIds)
{
    public ImmutableArray<Guid> EvidenceFactIds { get; init; } =
        EvidenceFactIds.IsDefault ? [] : EvidenceFactIds;
}

public sealed record SegmentRoleHypothesis(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    SegmentRole Role,
    double Confidence,
    CandidateBoundary StartBoundary,
    CandidateBoundary EndBoundary,
    ImmutableArray<Guid> EvidenceFactIds,
    ImmutableArray<CoverageChannel> EvidenceChannels,
    string AnalyzerId,
    string AnalyzerVersion)
{
    public ImmutableArray<Guid> EvidenceFactIds { get; init; } =
        EvidenceFactIds.IsDefault ? [] : EvidenceFactIds;
    public ImmutableArray<CoverageChannel> EvidenceChannels { get; init; } =
        EvidenceChannels.IsDefault ? [] : EvidenceChannels;
}

public sealed record MomentNode(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    ImmutableArray<Guid> FactIds,
    string Summary = "")
{
    public ImmutableArray<Guid> FactIds { get; init; } = FactIds.IsDefault ? [] : FactIds;
}

public sealed record ShotNode(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    ImmutableArray<Guid> MomentIds,
    ImmutableArray<Guid> FactIds,
    string Summary,
    string EmbeddingReference = "")
{
    public ImmutableArray<Guid> MomentIds { get; init; } = MomentIds.IsDefault ? [] : MomentIds;
    public ImmutableArray<Guid> FactIds { get; init; } = FactIds.IsDefault ? [] : FactIds;
}

public sealed record SceneNode(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    ImmutableArray<Guid> ShotIds,
    ImmutableArray<Guid> FactIds,
    string Summary,
    string EmbeddingReference = "")
{
    public ImmutableArray<Guid> ShotIds { get; init; } = ShotIds.IsDefault ? [] : ShotIds;
    public ImmutableArray<Guid> FactIds { get; init; } = FactIds.IsDefault ? [] : FactIds;
}

public sealed record ChapterNode(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    ImmutableArray<Guid> SceneIds,
    ImmutableArray<Guid> FactIds,
    string Summary,
    string EmbeddingReference = "")
{
    public ImmutableArray<Guid> SceneIds { get; init; } = SceneIds.IsDefault ? [] : SceneIds;
    public ImmutableArray<Guid> FactIds { get; init; } = FactIds.IsDefault ? [] : FactIds;
}

public sealed record AudioEvent(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    string Kind,
    double Confidence,
    ImmutableDictionary<string, double> Measurements,
    int? StreamIndex = null)
{
    public ImmutableDictionary<string, double> Measurements { get; init; } =
        Measurements ?? ImmutableDictionary<string, double>.Empty;
}

public sealed record SpeakerTurn(
    Guid Id,
    Guid SourceId,
    TimeRange SourceRange,
    string SpeakerId,
    string Text,
    double Confidence,
    ImmutableArray<TranscriptWord> Words,
    int? StreamIndex = null)
{
    public ImmutableArray<TranscriptWord> Words { get; init; } = Words.IsDefault ? [] : Words;
}

public sealed record TranscriptWord(
    string Text,
    TimeRange SourceRange,
    double Confidence,
    string SpeakerId = "",
    int? StreamIndex = null);

public sealed record AnalyzerManifest(
    string Id,
    string Version,
    string Model,
    string Runtime,
    ImmutableArray<CoverageChannel> Capabilities,
    DateTimeOffset CreatedAt)
{
    public ImmutableArray<CoverageChannel> Capabilities { get; init; } =
        Capabilities.IsDefault ? [] : Capabilities;
}

public sealed record MediaUnderstandingIndex(
    Guid Id,
    Guid SourceId,
    string SourceFingerprint,
    string PipelineVersion,
    TimelineTime SourceDuration,
    CoverageMap Coverage,
    ImmutableArray<AnalyzerManifest> Analyzers,
    ImmutableArray<ChapterNode> Chapters,
    ImmutableArray<SceneNode> Scenes,
    ImmutableArray<ShotNode> Shots,
    ImmutableArray<MomentNode> Moments,
    ImmutableArray<TemporalFact> Facts,
    ImmutableArray<AudioEvent> AudioEvents,
    ImmutableArray<SpeakerTurn> SpeakerTurns,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ArtifactReference = "",
    ImmutableArray<TranscriptWord> TranscriptWords = default,
    ImmutableArray<SemanticHypothesis> SemanticHypotheses = default,
    ImmutableArray<SegmentRoleHypothesis> SegmentRoleHypotheses = default)
{
    public ImmutableArray<AnalyzerManifest> Analyzers { get; init; } = Analyzers.IsDefault ? [] : Analyzers;
    public ImmutableArray<ChapterNode> Chapters { get; init; } = Chapters.IsDefault ? [] : Chapters;
    public ImmutableArray<SceneNode> Scenes { get; init; } = Scenes.IsDefault ? [] : Scenes;
    public ImmutableArray<ShotNode> Shots { get; init; } = Shots.IsDefault ? [] : Shots;
    public ImmutableArray<MomentNode> Moments { get; init; } = Moments.IsDefault ? [] : Moments;
    public ImmutableArray<TemporalFact> Facts { get; init; } = Facts.IsDefault ? [] : Facts;
    public ImmutableArray<AudioEvent> AudioEvents { get; init; } = AudioEvents.IsDefault ? [] : AudioEvents;
    public ImmutableArray<SpeakerTurn> SpeakerTurns { get; init; } = SpeakerTurns.IsDefault ? [] : SpeakerTurns;
    public ImmutableArray<TranscriptWord> TranscriptWords { get; init; } = TranscriptWords.IsDefault ? [] : TranscriptWords;
    public ImmutableArray<SemanticHypothesis> SemanticHypotheses { get; init; } =
        SemanticHypotheses.IsDefault ? [] : SemanticHypotheses;
    public ImmutableArray<SegmentRoleHypothesis> SegmentRoleHypotheses { get; init; } =
        SegmentRoleHypotheses.IsDefault ? [] : SegmentRoleHypotheses;
}

public sealed record EditScopePolicy(
    EditorialIntentKind Intent,
    PreservationPolicy Preservation,
    ImmutableArray<EditDecisionKind> AllowedDecisionKinds,
    ImmutableArray<SegmentRole> RemovableSegmentRoles,
    ImmutableArray<SegmentRole> ProtectedSegmentRoles)
{
    public ImmutableArray<EditDecisionKind> AllowedDecisionKinds { get; init; } =
        AllowedDecisionKinds.IsDefault ? [] : AllowedDecisionKinds;
    public ImmutableArray<SegmentRole> RemovableSegmentRoles { get; init; } =
        RemovableSegmentRoles.IsDefault ? [] : RemovableSegmentRoles;
    public ImmutableArray<SegmentRole> ProtectedSegmentRoles { get; init; } =
        ProtectedSegmentRoles.IsDefault ? [] : ProtectedSegmentRoles;

    public static EditScopePolicy General { get; } = new(
        EditorialIntentKind.GeneralMontage,
        PreservationPolicy.Flexible,
        Enum.GetValues<EditDecisionKind>().ToImmutableArray(),
        Enum.GetValues<SegmentRole>().Where(item => item != SegmentRole.Unknown).ToImmutableArray(),
        []);

    public static EditScopePolicy AnimeOpeningEndingOnly { get; } = new(
        EditorialIntentKind.RemoveNamedSections,
        PreservationPolicy.ExactComplement,
        [EditDecisionKind.Remove],
        [SegmentRole.Opening, SegmentRole.Ending],
        [SegmentRole.EpisodeBody, SegmentRole.PostCredits, SegmentRole.Preview,
         SegmentRole.Recap, SegmentRole.SponsorCard]);
}

public sealed record MontageProfile(
    string Id,
    int Version,
    MontageProfileKind Kind,
    string DisplayName,
    ImmutableArray<CoverageChannel> RequiredChannels,
    ImmutableArray<EditorialPassKind> Passes,
    double TargetAverageShotSeconds,
    ImmutableArray<string> ProtectedSemanticTags,
    EditScopePolicy? DefaultScopePolicy = null)
{
    public ImmutableArray<CoverageChannel> RequiredChannels { get; init; } =
        RequiredChannels.IsDefault ? [] : RequiredChannels;
    public ImmutableArray<EditorialPassKind> Passes { get; init; } = Passes.IsDefault ? [] : Passes;
    public ImmutableArray<string> ProtectedSemanticTags { get; init; } =
        ProtectedSemanticTags.IsDefault ? [] : ProtectedSemanticTags;
}

public sealed record EditorialBrief(
    Guid Id,
    string Goal,
    string Audience,
    MontageProfile Profile,
    TimelineTime? TargetDuration,
    string Style,
    ImmutableArray<string> ProtectedContent,
    ImmutableArray<string> AcceptanceCriteria,
    DateTimeOffset CreatedAt,
    EditScopePolicy? ScopePolicy = null,
    ImmutableArray<Guid> TargetSourceIds = default,
    double TargetSelectionConfidence = 1,
    string TargetSelectionRationale = "")
{
    public ImmutableArray<string> ProtectedContent { get; init; } = ProtectedContent.IsDefault ? [] : ProtectedContent;
    public ImmutableArray<string> AcceptanceCriteria { get; init; } = AcceptanceCriteria.IsDefault ? [] : AcceptanceCriteria;
    public ImmutableArray<Guid> TargetSourceIds { get; init; } =
        TargetSourceIds.IsDefault ? [] : TargetSourceIds.Distinct().ToImmutableArray();
}

public sealed record EditDecisionTarget(
    Guid SequenceId,
    long SequenceRevision,
    TimeRange TimelineRange,
    Guid SourceId,
    TimeRange SourceRange,
    ImmutableArray<Guid> TimelineObjectIds)
{
    public ImmutableArray<Guid> TimelineObjectIds { get; init; } =
        TimelineObjectIds.IsDefault ? [] : TimelineObjectIds;
}

public sealed record EditDecision(
    Guid Id,
    EditDecisionKind Kind,
    Guid SourceId,
    TimeRange SourceRange,
    int Order,
    string Rationale,
    double Confidence,
    ImmutableArray<Guid> EvidenceFactIds,
    ImmutableDictionary<string, string> Parameters,
    EditDecisionTarget? Target = null,
    SegmentRole SegmentRole = SegmentRole.Unknown)
{
    public ImmutableArray<Guid> EvidenceFactIds { get; init; } = EvidenceFactIds.IsDefault ? [] : EvidenceFactIds;
    public ImmutableDictionary<string, string> Parameters { get; init; } =
        Parameters ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record MontageGraph(
    Guid Id,
    Guid TaskId,
    Guid SourceSequenceId,
    long SourceSequenceRevision,
    EditorialBrief Brief,
    ImmutableArray<EditDecision> Decisions,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ParentFingerprint = "")
{
    public ImmutableArray<EditDecision> Decisions { get; init; } = Decisions.IsDefault ? [] : Decisions;

    public string Fingerprint()
    {
        var canonical = JsonSerializer.Serialize(new
        {
            SourceSequenceId,
            SourceSequenceRevision,
            Brief = new
            {
                Brief.Goal,
                Brief.Audience,
                ProfileId = Brief.Profile.Id,
                Brief.TargetDuration,
                Brief.Style,
                ScopePolicy = Brief.ScopePolicy,
                ProtectedContent = Brief.ProtectedContent.OrderBy(item => item),
                AcceptanceCriteria = Brief.AcceptanceCriteria.OrderBy(item => item)
            },
            Decisions = Decisions
                .OrderBy(item => item.Order)
                .ThenBy(item => item.Id)
                .Select(item => new
                {
                    item.Kind,
                    item.SourceId,
                    Start = item.SourceRange.Start.Ticks,
                    End = item.SourceRange.End.Ticks,
                    item.SegmentRole,
                    Target = item.Target is null ? null : new
                    {
                        item.Target.SequenceId,
                        item.Target.SequenceRevision,
                        Start = item.Target.TimelineRange.Start.Ticks,
                        End = item.Target.TimelineRange.End.Ticks,
                        item.Target.TimelineObjectIds
                    },
                    item.Order,
                    Parameters = item.Parameters.OrderBy(pair => pair.Key)
                })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record EvidenceGap(
    Guid Id,
    EvidenceGapReason Reason,
    CoverageChannel Channel,
    Guid SourceId,
    TimeRange SourceRange,
    double MinimumDensityHz,
    bool RequireContinuousCoverage,
    string RecommendedAnalyzer,
    string Message);

public sealed record DraftPatch(
    Guid Id,
    Guid MontageGraphId,
    EditorialPassKind Pass,
    int Order,
    ImmutableArray<EditDecision> Decisions,
    string Summary,
    DateTimeOffset CreatedAt)
{
    public ImmutableArray<EditDecision> Decisions { get; init; } = Decisions.IsDefault ? [] : Decisions;
}

public sealed record DraftQualityIssue(
    string Code,
    string Message,
    TimeRange? TimelineRange,
    bool IsBlocking);

public sealed record DraftQualityReport(
    Guid Id,
    Guid TaskId,
    Guid DraftSequenceId,
    DraftQualityStatus Status,
    ImmutableArray<DraftQualityIssue> Issues,
    ImmutableDictionary<string, double> Metrics,
    DateTimeOffset CreatedAt)
{
    public ImmutableArray<DraftQualityIssue> Issues { get; init; } = Issues.IsDefault ? [] : Issues;
    public ImmutableDictionary<string, double> Metrics { get; init; } =
        Metrics ?? ImmutableDictionary<string, double>.Empty;
}

public sealed record DraftCommandReceipt(
    Guid Id,
    Guid TaskId,
    Guid SourceSequenceId,
    long SourceSequenceRevision,
    Guid DraftSequenceId,
    string CommandType,
    int Order,
    TimeRange AffectedTimelineRange,
    string EvidenceFingerprint,
    string BeforeFingerprint,
    string AfterFingerprint,
    DateTimeOffset AppliedAt);

public sealed record DraftTimelineDiff(
    Guid SourceSequenceId,
    Guid DraftSequenceId,
    ImmutableArray<TimeRange> RemovedRanges,
    int SourceMediaObjectCount,
    int DraftMediaObjectCount,
    int SourceSubtitleObjectCount,
    int DraftSubtitleObjectCount,
    TimelineTime SourceDuration,
    TimelineTime DraftDuration)
{
    public ImmutableArray<TimeRange> RemovedRanges { get; init; } =
        RemovedRanges.IsDefault ? [] : RemovedRanges;
}

public sealed record ExternalReference(
    Guid Id,
    string Url,
    string Title,
    string Citation,
    DateTimeOffset RetrievedAt,
    bool UserRequested,
    string Query);
