using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services.Editorial;

public sealed class AiServerEditorialReasoner(AiServerV2Client client)
    : IEditorialDirector, IEditorialCritic
{
    private static readonly JsonElement ProfileSchema = ParseSchema(
        """{"type":"object","properties":{"profile":{"type":"string","enum":["Generic","FilmSeries","AnimeEpisode","TalkingHead","Podcast","ShortFormHighlights"]}},"required":["profile"],"additionalProperties":false}""");
    private static readonly JsonElement BriefSchema = ParseSchema(
        """{"type":"object","properties":{"goal":{"type":"string"},"audience":{"type":"string"},"target_duration_ticks":{"type":["integer","null"]},"style":{"type":"string"},"protected_content":{"type":"array","items":{"type":"string"}},"acceptance_criteria":{"type":"array","items":{"type":"string"}},"target_source_ids":{"type":"array","items":{"type":"string","format":"uuid"}},"target_selection_confidence":{"type":"number","minimum":0,"maximum":1},"target_selection_rationale":{"type":"string"}},"required":["goal","audience","target_duration_ticks","style","protected_content","acceptance_criteria","target_source_ids","target_selection_confidence","target_selection_rationale"],"additionalProperties":false}""");
    private static readonly JsonElement DecisionsSchema = ParseSchema(
        """{"type":"object","properties":{"summary":{"type":"string"},"decisions":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"kind":{"type":"string","enum":["Keep","Remove","Reorder","SelectTake","InsertBroll","ApplyDialogueCut","ApplyJlCut","ApplyAudioMix","ApplyCaptionTrack","AutoReframe","Retime"]},"segment_role":{"type":"string","enum":["Unknown","Opening","Ending","EpisodeBody","PostCredits","Preview","Recap","SponsorCard"]},"source_id":{"type":"string","format":"uuid"},"start_ticks":{"type":"integer","minimum":0},"duration_ticks":{"type":"integer","minimum":1},"order":{"type":"integer","minimum":0},"rationale":{"type":"string"},"confidence":{"type":"number","minimum":0,"maximum":1},"evidence_fact_ids":{"type":"array","items":{"type":"string","format":"uuid"}},"parameters":{"type":"object","additionalProperties":{"type":"string"}}},"required":["id","kind","segment_role","source_id","start_ticks","duration_ticks","order","rationale","confidence","evidence_fact_ids","parameters"],"additionalProperties":false}}},"required":["summary","decisions"],"additionalProperties":false}""");
    private static readonly JsonElement CriticSchema = ParseSchema(
        """{"type":"object","properties":{"issues":{"type":"array","items":{"type":"object","properties":{"code":{"type":"string"},"message":{"type":"string"},"start_ticks":{"type":["integer","null"]},"duration_ticks":{"type":["integer","null"]},"blocking":{"type":"boolean"}},"required":["code","message","start_ticks","duration_ticks","blocking"],"additionalProperties":false}}},"required":["issues"],"additionalProperties":false}""");

    public async Task<MontageProfileKind> SuggestProfileAsync(
        ProjectState project,
        Guid sourceSequenceId,
        string userRequest,
        CancellationToken cancellationToken)
    {
        var sequence = project.FindSequence(sourceSequenceId)
            ?? throw new InvalidOperationException("Source sequence was not found.");
        var context = JsonSerializer.SerializeToElement(new
        {
            userRequest,
            sourceCount = sequence.MediaClips.Select(item => item.SourceId).Distinct().Count(),
            durationTicks = sequence.Duration.Ticks,
            hasAudio = sequence.MediaClips.Any(item => project.Sources[item.SourceId].HasAudio),
            availableProfiles = MontageProfileCatalog.All.Select(item => new
            {
                id = item.Kind.ToString(),
                item.DisplayName,
                requiredChannels = item.RequiredChannels.Select(channel => channel.ToString())
            })
        });
        var raw = await ReasonWithCompactionAsync(
            "Director", ProfileSchema, context,
            "Suggest one MontageProfile. Do not make edit decisions.", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        var value = document.RootElement.GetProperty("profile").GetString();
        return Enum.TryParse<MontageProfileKind>(value, true, out var profile)
            ? profile
            : MontageProfileSelector.Suggest(userRequest);
    }

    public async Task<EditorialBrief> CreateBriefAsync(
        ProjectState project,
        Guid sourceSequenceId,
        string userRequest,
        MontageProfile profile,
        string revisionFeedback,
        CancellationToken cancellationToken)
    {
        var sequence = project.FindSequence(sourceSequenceId)
            ?? throw new InvalidOperationException("Source sequence was not found.");
        var context = JsonSerializer.SerializeToElement(new
        {
            userRequest,
            revisionFeedback,
            profile = profile.Id,
            sourceDurationTicks = sequence.Duration.Ticks,
            availableEpisodeSources = sequence.MediaClips
                .Where(item => item.Video is not null)
                .GroupBy(item => item.SourceId)
                .OrderBy(group => group.Min(item => item.Start))
                .Select(group => new
                {
                    sourceId = group.Key,
                    name = project.Sources[group.Key].Name,
                    durationTicks = project.Sources[group.Key].Duration.Ticks,
                    timelineOccurrences = group.Select(item => new
                    {
                        startTicks = item.Start.Ticks,
                        durationTicks = item.Duration.Ticks,
                        sourceInTicks = item.SourceIn.Ticks
                    })
                }),
            protectedAnnotations = project.SourceAnnotations
                .Where(item => item.Kind == SourceAnnotationKind.Required)
                .Select(item => new { item.SourceId, startTicks = item.SourceRange.Start.Ticks, durationTicks = item.SourceRange.Duration.Ticks, item.Note }),
            excludedAnnotations = project.SourceAnnotations
                .Where(item => item.Kind == SourceAnnotationKind.Excluded)
                .Select(item => new { item.SourceId, startTicks = item.SourceRange.Start.Ticks, durationTicks = item.SourceRange.Duration.Ticks, item.Note })
        });
        var raw = await ReasonWithCompactionAsync(
            "Director", BriefSchema, context,
            "Create the EditorialBrief. Semantically infer which available episode sources the user means from the whole request and the source names; never use a fixed phrase list. Return their exact source IDs. Select every source only when that is the user's intent. If the target is genuinely ambiguous, return an empty target_source_ids array and low confidence. User annotations are hard constraints.",
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return new EditorialBrief(
            Guid.NewGuid(),
            RequiredString(root, "goal"),
            RequiredString(root, "audience"),
            profile,
            root.GetProperty("target_duration_ticks").ValueKind == JsonValueKind.Null
                ? null
                : new TimelineTime(root.GetProperty("target_duration_ticks").GetInt64()),
            RequiredString(root, "style"),
            Strings(root, "protected_content"),
            Strings(root, "acceptance_criteria"),
            DateTimeOffset.UtcNow,
            profile.DefaultScopePolicy ?? EditScopePolicy.General,
            root.GetProperty("target_source_ids").EnumerateArray()
                .Select(item => Guid.Parse(item.GetString()!))
                .Distinct()
                .ToImmutableArray(),
            root.GetProperty("target_selection_confidence").GetDouble(),
            RequiredString(root, "target_selection_rationale"));
    }

    public async Task<MontageGraph> CreateRoughCutAsync(
        EditorialBrief brief,
        EditorialWorkingSet workingSet,
        Guid taskId,
        Guid sourceSequenceId,
        long sourceSequenceRevision,
        MontageGraph? previousRejectedGraph,
        CancellationToken cancellationToken)
    {
        var context = WorkingContext(brief, workingSet, previousRejectedGraph);
        var raw = await ReasonWithCompactionAsync(
            "RoughCut", DecisionsSchema, context,
            "Build one semantic MontageGraph rough cut. Every semantic decision must reference supplied measured fact IDs. Obey brief.scopePolicy allowed decision kinds and removable segment roles exactly.",
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        var now = DateTimeOffset.UtcNow;
        return new MontageGraph(
            Guid.NewGuid(), taskId, sourceSequenceId, sourceSequenceRevision,
            brief, ParseDecisions(document.RootElement), 1, now, now,
            previousRejectedGraph?.Fingerprint() ?? string.Empty);
    }

    public async Task<DraftPatch> RefineAsync(
        EditorialPassKind pass,
        EditorialBrief brief,
        MontageGraph graph,
        EditorialWorkingSet workingSet,
        int order,
        CancellationToken cancellationToken)
    {
        var context = JsonSerializer.SerializeToElement(new
        {
            pass = pass.ToString(),
            brief,
            graphFingerprint = graph.Fingerprint(),
            decisions = graph.Decisions.Select(DecisionContext),
            facts = workingSet.Facts.Select(FactContext),
            hypotheses = workingSet.Hypotheses.Select(HypothesisContext),
            segmentRoleHypotheses = workingSet.SegmentRoleHypotheses.Select(SegmentRoleContext),
            counterEvidence = workingSet.CounterEvidence.Select(FactContext)
        });
        var role = pass switch
        {
            EditorialPassKind.StoryContinuity => "StoryContinuity",
            EditorialPassKind.Rhythm => "Rhythm",
            EditorialPassKind.DialogueAudio => "DialogueAudio",
            EditorialPassKind.CompositionReframe => "CompositionReframe",
            EditorialPassKind.Captions => "Captions",
            _ => "Critic"
        };
        var raw = await ReasonWithCompactionAsync(
            role, DecisionsSchema, context,
            $"Return only the bounded {pass} DraftPatch. Preserve unrelated decisions and their IDs.",
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        return new DraftPatch(
            Guid.NewGuid(), graph.Id, pass, order,
            ParseDecisions(document.RootElement),
            RequiredString(document.RootElement, "summary"),
            DateTimeOffset.UtcNow);
    }

    public async Task<ImmutableArray<DraftQualityIssue>> ReviewGraphAsync(
        EditorialBrief brief,
        MontageGraph graph,
        EditorialWorkingSet workingSet,
        CancellationToken cancellationToken)
    {
        var context = JsonSerializer.SerializeToElement(new
        {
            brief,
            graphFingerprint = graph.Fingerprint(),
            decisions = graph.Decisions.Select(DecisionContext),
            facts = workingSet.Facts.Select(FactContext),
            hypotheses = workingSet.Hypotheses.Select(HypothesisContext),
            segmentRoleHypotheses = workingSet.SegmentRoleHypotheses.Select(SegmentRoleContext),
            counterEvidence = workingSet.CounterEvidence.Select(FactContext)
        });
        var raw = await ReasonWithCompactionAsync(
            "Critic", CriticSchema, context,
            "Independently inspect the graph for story, continuity and protected-content failures. Do not rewrite it.",
            cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("issues").EnumerateArray()
            .Select(item =>
            {
                TimeRange? range = item.GetProperty("start_ticks").ValueKind == JsonValueKind.Null ||
                                   item.GetProperty("duration_ticks").ValueKind == JsonValueKind.Null
                    ? null
                    : new TimeRange(
                        new TimelineTime(item.GetProperty("start_ticks").GetInt64()),
                        new TimelineTime(item.GetProperty("duration_ticks").GetInt64()));
                return new DraftQualityIssue(
                    RequiredString(item, "code"), RequiredString(item, "message"),
                    range, item.GetProperty("blocking").GetBoolean());
            })
            .ToImmutableArray();
    }

    private static JsonElement WorkingContext(
        EditorialBrief brief,
        EditorialWorkingSet workingSet,
        MontageGraph? previousRejected)
        => JsonSerializer.SerializeToElement(new
        {
            brief,
            chapters = workingSet.Chapters.Select(item => new { item.Id, item.SourceId, startTicks = item.SourceRange.Start.Ticks, durationTicks = item.SourceRange.Duration.Ticks, item.Summary }),
            scenes = workingSet.Scenes.Select(item => new { item.Id, item.SourceId, startTicks = item.SourceRange.Start.Ticks, durationTicks = item.SourceRange.Duration.Ticks, item.Summary }),
            shots = workingSet.Shots.Select(item => new { item.Id, item.SourceId, startTicks = item.SourceRange.Start.Ticks, durationTicks = item.SourceRange.Duration.Ticks, item.Summary }),
            facts = workingSet.Facts.Select(FactContext),
            hypotheses = workingSet.Hypotheses.Select(HypothesisContext),
            segmentRoleHypotheses = workingSet.SegmentRoleHypotheses.Select(SegmentRoleContext),
            availableChannels = workingSet.AvailableChannels.ToDictionary(
                item => item.Key,
                item => item.Value.Select(channel => channel.ToString())),
            speakerTurns = workingSet.SpeakerTurns.Select(item => new { item.Id, item.SourceId, startTicks = item.SourceRange.Start.Ticks, durationTicks = item.SourceRange.Duration.Ticks, item.SpeakerId, item.Text, item.Confidence }),
            counterEvidence = workingSet.CounterEvidence.Select(FactContext),
            rejected = previousRejected is null ? null : new
            {
                fingerprint = previousRejected.Fingerprint(),
                decisions = previousRejected.Decisions.Select(DecisionContext)
            }
        });

    private static object FactContext(TemporalFact item) => new
    {
        item.Id,
        item.SourceId,
        startTicks = item.SourceRange.Start.Ticks,
        durationTicks = item.SourceRange.Duration.Ticks,
        channel = item.Channel.ToString(),
        kind = item.Kind.ToString(),
        item.Summary,
        item.Confidence
    };

    private static object HypothesisContext(SemanticHypothesis item) => new
    {
        item.Id,
        item.SourceId,
        startTicks = item.SourceRange.Start.Ticks,
        durationTicks = item.SourceRange.Duration.Ticks,
        item.Summary,
        item.Confidence,
        item.EvidenceFactIds,
        evidenceClass = "semantic_hypothesis_not_measured_fact"
    };

    private static object SegmentRoleContext(SegmentRoleHypothesis item) => new
    {
        item.Id,
        item.SourceId,
        startTicks = item.SourceRange.Start.Ticks,
        durationTicks = item.SourceRange.Duration.Ticks,
        role = item.Role.ToString(),
        item.Confidence,
        item.EvidenceFactIds,
        evidenceChannels = item.EvidenceChannels.Select(channel => channel.ToString()),
        startBoundaryTicks = item.StartBoundary.Time.Ticks,
        endBoundaryTicks = item.EndBoundary.Time.Ticks,
        evidenceClass = "segment_role_hypothesis_not_measured_fact"
    };

    private static object DecisionContext(EditDecision item) => new
    {
        item.Id,
        kind = item.Kind.ToString(),
        item.SourceId,
        startTicks = item.SourceRange.Start.Ticks,
        durationTicks = item.SourceRange.Duration.Ticks,
        item.Order,
        item.Rationale,
        item.Confidence,
        segmentRole = item.SegmentRole.ToString(),
        item.EvidenceFactIds,
        item.Parameters
    };

    private static ImmutableArray<EditDecision> ParseDecisions(JsonElement root)
        => root.GetProperty("decisions").EnumerateArray()
            .Select(item => new EditDecision(
                Guid.Parse(RequiredString(item, "id")),
                Enum.Parse<EditDecisionKind>(RequiredString(item, "kind"), true),
                Guid.Parse(RequiredString(item, "source_id")),
                new TimeRange(
                    new TimelineTime(item.GetProperty("start_ticks").GetInt64()),
                    new TimelineTime(item.GetProperty("duration_ticks").GetInt64())),
                item.GetProperty("order").GetInt32(),
                RequiredString(item, "rationale"),
                item.GetProperty("confidence").GetDouble(),
                item.GetProperty("evidence_fact_ids").EnumerateArray()
                    .Select(value => Guid.Parse(value.GetString()!)).ToImmutableArray(),
                item.GetProperty("parameters").EnumerateObject()
                    .ToImmutableDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty),
                SegmentRole: item.TryGetProperty("segment_role", out var roleElement) &&
                             Enum.TryParse<SegmentRole>(roleElement.GetString(), true, out var segmentRole)
                    ? segmentRole
                    : SegmentRole.Unknown))
            .ToImmutableArray();

    private static string RequiredString(JsonElement root, string name)
        => root.GetProperty(name).GetString()
           ?? throw new InvalidDataException($"Structured response field '{name}' is empty.");

    private static ImmutableArray<string> Strings(JsonElement root, string name)
        => root.GetProperty(name).EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToImmutableArray();

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<string> ReasonWithCompactionAsync(
        string role,
        JsonElement schema,
        JsonElement context,
        string instruction,
        CancellationToken cancellationToken)
    {
        var current = context;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.ReasonAsync(
                    role, schema, current, instruction, cancellationToken).ConfigureAwait(false);
            }
            catch (AiServerV2Exception exception) when (
                exception.ErrorCode == "invalid_context_budget" && attempt < 8)
            {
                current = CompactContext(current);
            }
        }
    }

    private static JsonElement CompactContext(JsonElement context)
    {
        var node = JsonNode.Parse(context.GetRawText())
                   ?? throw new InvalidDataException("Editorial context is empty.");
        CompactNode(node);
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void CompactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var child in obj.ToArray())
            {
                if (child.Value is JsonArray array && child.Key is
                    "chapters" or "scenes" or "shots" or "facts" or
                    "hypotheses" or "segmentRoleHypotheses" or "counterEvidence" or "speakerTurns")
                    TrimArray(array);
                if (child.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 2_048)
                    obj[child.Key] = text[..1_792] + "…[compacted]";
                if (child.Value is not null) CompactNode(child.Value);
            }
            return;
        }

        if (node is JsonArray values)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 2_048)
                    values[index] = text[..1_792] + "…[compacted]";
                if (values[index] is not null) CompactNode(values[index]!);
            }
        }
    }

    private static void TrimArray(JsonArray array)
    {
        if (array.Count <= 8) return;
        var keep = Math.Max(8, array.Count / 2);
        var head = (keep + 1) / 2;
        while (array.Count > keep) array.RemoveAt(head);
    }
}
