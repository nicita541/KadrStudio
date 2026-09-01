using System.Collections.Immutable;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Editorial;

public sealed class MontageGraphValidatorV2 : IMontageGraphValidatorV2
{
    public MontageGraphValidationResult Validate(
        ProjectState project,
        MediaUnderstandingCatalog indexes,
        MontageGraph graph)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(graph);
        var gaps = ImmutableArray.CreateBuilder<EvidenceGap>();
        var errors = ImmutableArray.CreateBuilder<string>();
        var sequence = project.FindSequence(graph.SourceSequenceId);
        if (sequence is null)
            errors.Add("Source sequence does not exist.");
        else if (sequence.Revision != graph.SourceSequenceRevision)
            errors.Add("Source sequence revision changed after directing.");
        if (graph.Decisions.IsDefaultOrEmpty)
            errors.Add("Montage graph contains no editorial decisions.");
        if (graph.Decisions.GroupBy(item => item.Order).Any(group => group.Count() > 1))
            errors.Add("Montage graph decision order must be unique.");
        var scope = graph.Brief.ScopePolicy ?? graph.Brief.Profile.DefaultScopePolicy ?? EditScopePolicy.General;
        foreach (var disallowed in graph.Decisions.Where(item => !scope.AllowedDecisionKinds.Contains(item.Kind)))
            errors.Add($"Decision {disallowed.Id} kind {disallowed.Kind} is forbidden by the editorial scope.");
        if (scope.Intent == EditorialIntentKind.RemoveNamedSections)
        {
            foreach (var decision in graph.Decisions.Where(item =>
                         item.Kind == EditDecisionKind.Remove &&
                         !scope.RemovableSegmentRoles.Contains(item.SegmentRole)))
                errors.Add($"Decision {decision.Id} removes role {decision.SegmentRole}, which is outside the request.");
            var removals = graph.Decisions.Where(item => item.Kind == EditDecisionKind.Remove).ToArray();
            foreach (var sourceGroup in removals.GroupBy(item => item.SourceId))
            {
                var roleCounts = scope.RemovableSegmentRoles
                    .Select(role => sourceGroup.Count(item => item.SegmentRole == role))
                    .ToArray();
                if (roleCounts.Any(count => count == 0) || roleCounts.Distinct().Count() != 1)
                    errors.Add($"Every selected episode occurrence must have one matched decision per requested removable role; source {sourceGroup.Key} is incomplete.");
            }
            if (scope.Preservation == PreservationPolicy.ExactComplement &&
                (removals.Length != graph.Decisions.Length || removals.Length == 0 ||
                 removals.Length % scope.RemovableSegmentRoles.Length != 0))
                errors.Add("Exact named-section removal must contain matched Remove pairs for the selected episodes and no other decisions.");
        }

        var facts = indexes.Indexes.SelectMany(item => item.Facts).ToDictionary(item => item.Id);
        foreach (var decision in graph.Decisions)
        {
            if (!project.Sources.TryGetValue(decision.SourceId, out var source))
            {
                errors.Add($"Decision {decision.Id} references an unknown source.");
                continue;
            }
            var index = indexes.Find(decision.SourceId);
            if (index is null)
            {
                errors.Add($"Decision {decision.Id} has no media-understanding index.");
                continue;
            }
            if (decision.SourceRange.Start < TimelineTime.Zero ||
                decision.SourceRange.Duration <= TimelineTime.Zero ||
                decision.SourceRange.End > source.Duration)
            {
                errors.Add($"Decision {decision.Id} is outside its media source.");
                continue;
            }
            if (decision.Confidence is < 0 or > 1)
                errors.Add($"Decision {decision.Id} has invalid confidence.");
            if (decision.Target is not { } target)
            {
                if (scope.Preservation == PreservationPolicy.ExactComplement)
                    errors.Add($"Decision {decision.Id} has no occurrence-aware timeline target.");
            }
            else
            {
                if (target.SequenceId != graph.SourceSequenceId ||
                    target.SequenceRevision != graph.SourceSequenceRevision)
                    errors.Add($"Decision {decision.Id} targets another sequence revision.");
                if (sequence is not null &&
                    (target.TimelineRange.Start < TimelineTime.Zero ||
                     target.TimelineRange.Duration <= TimelineTime.Zero ||
                     target.TimelineRange.End > sequence.Duration))
                    errors.Add($"Decision {decision.Id} timeline target is outside the source sequence.");
                if (target.SourceId != decision.SourceId || target.SourceRange != decision.SourceRange)
                    errors.Add($"Decision {decision.Id} source and target ranges disagree.");
                if (sequence is not null &&
                    target.TimelineObjectIds.Any(id =>
                        !sequence.MediaClips.Any(clip => clip.Id == id) &&
                        !sequence.SubtitleClips.Any(clip => clip.Id == id)))
                    errors.Add($"Decision {decision.Id} references a stale timeline occurrence.");
            }

            var referencedFacts = decision.EvidenceFactIds
                .Where(facts.ContainsKey)
                .Select(id => facts[id])
                .ToArray();
            if (referencedFacts.Length != decision.EvidenceFactIds.Distinct().Count())
                errors.Add($"Decision {decision.Id} references missing facts.");
            if (referencedFacts.Any(item => item.SourceId != decision.SourceId))
                errors.Add($"Decision {decision.Id} references evidence from another source.");
            if ((decision.Kind is EditDecisionKind.Keep or EditDecisionKind.Remove or
                    EditDecisionKind.Reorder or EditDecisionKind.SelectTake or
                    EditDecisionKind.InsertBroll) && referencedFacts.Length == 0)
            {
                var evidenceChannel = source.Kind == MediaKind.Audio
                    ? graph.Brief.Profile.RequiredChannels.Contains(CoverageChannel.Transcript)
                        ? CoverageChannel.Transcript
                        : CoverageChannel.Audio
                    : CoverageChannel.Frames;
                AddGap(
                    gaps, EvidenceGapReason.MissingCoverage, evidenceChannel,
                    decision,
                    evidenceChannel == CoverageChannel.Audio ? 20 : evidenceChannel == CoverageChannel.Transcript ? 1 : 0.2,
                    evidenceChannel is CoverageChannel.Audio or CoverageChannel.Transcript,
                    evidenceChannel == CoverageChannel.Transcript ? "asr-align" :
                        evidenceChannel == CoverageChannel.Audio ? "audio-events" : "video-understanding",
                    "Semantic selection requires measured source facts.");
            }

            if (scope.Intent == EditorialIntentKind.RemoveNamedSections &&
                decision.Kind == EditDecisionKind.Remove)
            {
                var channels = referencedFacts.Select(item => item.Channel).Distinct().ToArray();
                if (channels.Length < 2)
                {
                    AddGap(
                        gaps, EvidenceGapReason.MissingCoverage, CoverageChannel.Audio,
                        decision, 20, true, "audio-events",
                        "Opening/ending removal requires evidence from at least two independent modalities.");
                }
                var matchingRole = index.SegmentRoleHypotheses.Any(item =>
                    item.Role == decision.SegmentRole &&
                    item.SourceRange.Overlaps(decision.SourceRange) &&
                    item.EvidenceChannels.Distinct().Count() >= 2 &&
                    item.Confidence >= 0.50 &&
                    item.StartBoundary.Confidence >= 0.50 &&
                    item.EndBoundary.Confidence >= 0.50);
                if (!matchingRole)
                {
                    AddGap(
                        gaps, EvidenceGapReason.ConflictingFacts, CoverageChannel.Frames,
                        decision, 0.5, false, "video-understanding",
                        "Remove decision has no multimodal segment-role hypothesis.");
                }
                if (decision.Confidence < 0.50)
                    errors.Add($"Decision {decision.Id} confidence is below the minimum boundary-review threshold.");
                var protectedOverlap = index.SegmentRoleHypotheses.FirstOrDefault(item =>
                    scope.ProtectedSegmentRoles.Contains(item.Role) &&
                    item.Confidence >= 0.65 &&
                    item.SourceRange.Overlaps(decision.SourceRange));
                if (protectedOverlap is not null)
                    errors.Add($"Decision {decision.Id} overlaps protected segment role {protectedOverlap.Role}.");
            }

            foreach (var requirement in Requirements(decision.Kind, source, graph.Brief.Profile))
            {
                if (!IsPhysicallyAvailable(source, requirement.Channel))
                {
                    errors.Add($"Decision {decision.Id} requires unavailable {requirement.Channel} media.");
                    continue;
                }
                foreach (var missing in index.Coverage.MissingRanges(
                             requirement.Channel,
                             decision.SourceRange,
                             requirement.MinimumDensityHz,
                             requirement.RequireContinuous))
                {
                    gaps.Add(new EvidenceGap(
                        Guid.NewGuid(),
                        requirement.MinimumDensityHz > 0
                            ? EvidenceGapReason.InsufficientDensity
                            : EvidenceGapReason.MissingCoverage,
                        requirement.Channel,
                        decision.SourceId,
                        missing,
                        requirement.MinimumDensityHz,
                        requirement.RequireContinuous,
                        requirement.Analyzer,
                        requirement.Message));
                }
            }

            if (NeedsExactBoundaries(decision.Kind))
            {
                if (source.Kind is MediaKind.Video or MediaKind.Image)
                {
                    var frameTolerance = source.FrameRate?.FrameDuration ?? TimelineTime.FromSeconds(1d / 25);
                    AddBoundaryGapIfMissing(index, decision, decision.SourceRange.Start, frameTolerance, gaps);
                    AddBoundaryGapIfMissing(index, decision, decision.SourceRange.End, frameTolerance, gaps);
                }
                else if (graph.Brief.Profile.RequiredChannels.Contains(CoverageChannel.Transcript))
                {
                    AddWordBoundaryGapIfMissing(index, decision, decision.SourceRange.Start, gaps);
                    AddWordBoundaryGapIfMissing(index, decision, decision.SourceRange.End, gaps);
                }
            }

            if (decision.Kind is EditDecisionKind.ApplyDialogueCut or EditDecisionKind.ApplyJlCut &&
                !index.TranscriptWords.Any(word => word.SourceRange.Overlaps(decision.SourceRange)))
            {
                AddGap(
                    gaps, EvidenceGapReason.MissingCoverage, CoverageChannel.Transcript,
                    decision, 1, true, "asr-align",
                    "Dialogue edits require persisted word-level forced alignment, not transcript coverage alone.");
            }

            if (decision.Kind == EditDecisionKind.Remove &&
                project.SourceAnnotations.Any(annotation =>
                    annotation.SourceId == decision.SourceId &&
                    annotation.Kind == SourceAnnotationKind.Required &&
                    annotation.SourceRange.Overlaps(decision.SourceRange)))
            {
                AddGap(
                    gaps, EvidenceGapReason.ConflictingFacts, CoverageChannel.Frames,
                    decision, 0.5, false, "critic",
                    "Removal intersects a source interval explicitly protected by the user.");
            }
        }

        var normalizedGaps = gaps
            .GroupBy(item => new
            {
                item.Reason,
                item.Channel,
                item.SourceId,
                Start = item.SourceRange.Start.Ticks,
                Duration = item.SourceRange.Duration.Ticks,
                item.RecommendedAnalyzer
            })
            .Select(group => group.First())
            .OrderBy(item => item.SourceId)
            .ThenBy(item => item.SourceRange.Start)
            .ThenBy(item => item.Channel)
            .ToImmutableArray();
        return new MontageGraphValidationResult(
            errors.Count == 0 && normalizedGaps.IsEmpty,
            normalizedGaps,
            errors.ToImmutable());
    }

    private static ImmutableArray<CoverageRequirement> Requirements(
        EditDecisionKind kind,
        MediaSource source,
        MontageProfile profile)
        => kind switch
        {
            EditDecisionKind.Keep or EditDecisionKind.Remove or
            EditDecisionKind.Reorder or EditDecisionKind.SelectTake or
            EditDecisionKind.InsertBroll when source.Kind == MediaKind.Audio &&
                                               profile.RequiredChannels.Contains(CoverageChannel.Transcript) =>
            [new(CoverageChannel.Audio, 20, true, "audio-events", "Continuous audio measurement is missing."),
             new(CoverageChannel.Transcript, 1, true, "asr-align", "Word-aligned transcript is missing.")],
            EditDecisionKind.Keep or EditDecisionKind.Remove or
            EditDecisionKind.Reorder or EditDecisionKind.SelectTake or
            EditDecisionKind.InsertBroll when source.Kind == MediaKind.Audio =>
            [new(CoverageChannel.Audio, 20, true, "audio-events", "Continuous audio measurement is missing.")],
            EditDecisionKind.Keep or EditDecisionKind.Remove or
            EditDecisionKind.Reorder or EditDecisionKind.SelectTake or
            EditDecisionKind.InsertBroll =>
            [new(CoverageChannel.Frames, 0.2, false, "video-understanding", "Dense visual probes are missing.")],
            EditDecisionKind.ApplyDialogueCut or EditDecisionKind.ApplyJlCut =>
            [new(CoverageChannel.Audio, 20, true, "asr-align", "Continuous audio measurement is missing."),
             new(CoverageChannel.Transcript, 1, true, "asr-align", "Word-aligned transcript is missing.")],
            EditDecisionKind.ApplyAudioMix =>
            [new(CoverageChannel.Audio, 20, true, "audio-events", "Continuous loudness analysis is missing.")],
            EditDecisionKind.ApplyCaptionTrack =>
            [new(CoverageChannel.Transcript, 1, true, "asr-align", "Word-aligned transcript is missing.")],
            EditDecisionKind.AutoReframe =>
            [new(CoverageChannel.Frames, 0.5, false, "video-understanding", "Reframe needs dense composition samples.")],
            EditDecisionKind.Retime =>
            [new(CoverageChannel.Motion, 5, true, "video-understanding", "Retime needs continuous motion tracking.")],
            _ => []
        };

    private static bool IsPhysicallyAvailable(MediaSource source, CoverageChannel channel)
        => channel switch
        {
            CoverageChannel.Audio or CoverageChannel.Transcript =>
                source.HasAudio || source.Kind == MediaKind.Audio,
            CoverageChannel.Frames or CoverageChannel.Motion or CoverageChannel.Ocr =>
                source.Kind is MediaKind.Video or MediaKind.Image,
            _ => false
        };

    private static bool NeedsExactBoundaries(EditDecisionKind kind)
        => kind is EditDecisionKind.Keep or EditDecisionKind.Remove or
            EditDecisionKind.Reorder or EditDecisionKind.SelectTake or
            EditDecisionKind.InsertBroll or EditDecisionKind.ApplyDialogueCut or
            EditDecisionKind.ApplyJlCut or EditDecisionKind.Retime;

    private static void AddBoundaryGapIfMissing(
        MediaUnderstandingIndex index,
        EditDecision decision,
        TimelineTime boundary,
        TimelineTime tolerance,
        ICollection<EvidenceGap> gaps)
    {
        if (boundary <= TimelineTime.Zero || boundary >= index.SourceDuration) return;
        var hasMeasuredCandidate = index.SegmentRoleHypotheses.Any(item =>
            item.SourceId == decision.SourceId &&
            item.Role == decision.SegmentRole &&
            item.EvidenceChannels.Distinct().Count() >= 2 &&
            item.EvidenceFactIds.Length >= 2 &&
            ((item.StartBoundary.Confidence >= 0.50 &&
              Distance(item.StartBoundary.Time, boundary) <= tolerance) ||
             (item.EndBoundary.Confidence >= 0.50 &&
              Distance(item.EndBoundary.Time, boundary) <= tolerance)));
        if (hasMeasuredCandidate) return;
        var hasBoundary = index.Facts.Any(item =>
            item.SourceId == decision.SourceId &&
            item.Kind == TemporalFactKind.ShotBoundary &&
            Distance(item.SourceRange.Start, boundary) <= tolerance);
        if (hasBoundary) return;
        var start = boundary > TimelineTime.FromSeconds(1) ? boundary - TimelineTime.FromSeconds(1) : TimelineTime.Zero;
        var end = boundary + TimelineTime.FromSeconds(1);
        if (end > index.SourceDuration) end = index.SourceDuration;
        gaps.Add(new EvidenceGap(
            Guid.NewGuid(), EvidenceGapReason.MissingBoundaryProbe,
            CoverageChannel.Frames, decision.SourceId,
            new TimeRange(start, end - start), 5, false,
            "video-understanding", "Frame-accurate boundary probe is required."));
    }

    private static void AddGap(
        ICollection<EvidenceGap> gaps,
        EvidenceGapReason reason,
        CoverageChannel channel,
        EditDecision decision,
        double density,
        bool continuous,
        string analyzer,
        string message)
        => gaps.Add(new EvidenceGap(
            Guid.NewGuid(), reason, channel, decision.SourceId,
            decision.SourceRange, density, continuous, analyzer, message));

    private static void AddWordBoundaryGapIfMissing(
        MediaUnderstandingIndex index,
        EditDecision decision,
        TimelineTime boundary,
        ICollection<EvidenceGap> gaps)
    {
        if (boundary <= TimelineTime.Zero || boundary >= index.SourceDuration) return;
        var tolerance = TimelineTime.FromSeconds(0.25);
        var aligned = index.TranscriptWords.Any(word =>
            Distance(word.SourceRange.Start, boundary) <= tolerance ||
            Distance(word.SourceRange.End, boundary) <= tolerance);
        if (aligned) return;
        var start = boundary > TimelineTime.FromSeconds(1) ? boundary - TimelineTime.FromSeconds(1) : TimelineTime.Zero;
        var end = boundary + TimelineTime.FromSeconds(1);
        if (end > index.SourceDuration) end = index.SourceDuration;
        gaps.Add(new EvidenceGap(
            Guid.NewGuid(), EvidenceGapReason.MissingBoundaryProbe,
            CoverageChannel.Transcript, decision.SourceId,
            new TimeRange(start, end - start), 1, true,
            "asr-align", "Word-level alignment is required around the audio edit boundary."));
    }

    private static TimelineTime Distance(TimelineTime left, TimelineTime right)
        => left >= right ? left - right : right - left;

    private sealed record CoverageRequirement(
        CoverageChannel Channel,
        double MinimumDensityHz,
        bool RequireContinuous,
        string Analyzer,
        string Message);
}
