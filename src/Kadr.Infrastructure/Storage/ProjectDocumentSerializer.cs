using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using KadrStudio.Core.Domain;

namespace KadrStudio.Infrastructure.Storage;

internal static class ProjectDocumentSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ProjectState project)
        => JsonSerializer.Serialize(ProjectDocument.FromState(project), Options);

    public static ProjectState Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<ProjectDocument>(json, Options)
            ?? throw new InvalidDataException("Снимок проекта пуст или повреждён.");
        return document.ToState();
    }

    public static string SerializeSequence(SequenceState sequence)
        => JsonSerializer.Serialize(SequenceDocument.FromState(sequence), Options);

    public static SequenceState DeserializeSequence(string json)
        => (JsonSerializer.Deserialize<SequenceDocument>(json, Options)
            ?? throw new InvalidDataException("Снимок последовательности пуст или повреждён.")).ToState();

    private sealed record ProjectDocument
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required int CanvasWidth { get; init; }
        public required int CanvasHeight { get; init; }
        public required int FrameRateNumerator { get; init; }
        public required int FrameRateDenominator { get; init; }
        public int AudioSampleRate { get; init; } = 48_000;
        public required long Revision { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
        public required TimelineTrack[] Tracks { get; init; }
        public required MediaSource[] Sources { get; init; }
        public required MediaClip[] MediaClips { get; init; }
        public required TextClip[] TextClips { get; init; }
        public TimelineTransition[] Transitions { get; init; } = [];
        public required TimelineMarker[] Markers { get; init; }
        public SequenceDocument[] Sequences { get; init; } = [];
        public Guid? ActiveSequenceId { get; init; }
        public SourceAnnotation[] SourceAnnotations { get; init; } = [];
        public MediaAnalysisReference[] AnalysisReferences { get; init; } = [];
        public MontagePlan[] MontagePlans { get; init; } = [];
        public AiConversation? AiConversation { get; init; }
        public long? InPointTicks { get; init; }
        public long? OutPointTicks { get; init; }

        public static ProjectDocument FromState(ProjectState project) => new()
        {
            Id = project.Id,
            Name = project.Name,
            CanvasWidth = project.CanvasWidth,
            CanvasHeight = project.CanvasHeight,
            FrameRateNumerator = project.FrameRate.Numerator,
            FrameRateDenominator = project.FrameRate.Denominator,
            AudioSampleRate = project.Sequence.AudioSampleRate,
            Revision = project.Revision,
            CreatedAt = project.CreatedAt,
            UpdatedAt = project.UpdatedAt,
            Tracks = project.Tracks.ToArray(),
            Sources = project.Sources.Values
                .Select(source => source.Streams.IsDefault ? source with { Streams = [] } : source)
                .ToArray(),
            MediaClips = project.MediaClips.ToArray(),
            TextClips = project.TextClips.ToArray(),
            Transitions = project.Transitions.ToArray(),
            Markers = project.Markers.ToArray(),
            Sequences = project.Sequences.Select(SequenceDocument.FromState).ToArray(),
            ActiveSequenceId = project.ActiveSequenceId,
            SourceAnnotations = project.SourceAnnotations.ToArray(),
            AnalysisReferences = project.AnalysisReferences.ToArray(),
            MontagePlans = project.MontagePlans.ToArray(),
            AiConversation = project.AiConversation,
            InPointTicks = project.InPoint?.Ticks,
            OutPointTicks = project.OutPoint?.Ticks
        };

        public ProjectState ToState() => new()
        {
            Id = Id,
            Name = Name,
            Sequence = new SequenceSettings(
                CanvasWidth, CanvasHeight, new FrameRate(FrameRateNumerator, FrameRateDenominator), AudioSampleRate),
            Revision = Revision,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Tracks = Tracks.ToImmutableArray(),
            Sources = Sources.ToImmutableDictionary(item => item.Id),
            MediaClips = MediaClips.ToImmutableArray(),
            TextClips = TextClips.ToImmutableArray(),
            Transitions = Transitions.ToImmutableArray(),
            Markers = Markers.ToImmutableArray(),
            Sequences = Sequences.Select(item => item.ToState()).ToImmutableArray(),
            ActiveSequenceId = ActiveSequenceId,
            SourceAnnotations = SourceAnnotations.ToImmutableArray(),
            AnalysisReferences = AnalysisReferences.ToImmutableArray(),
            MontagePlans = MontagePlans.ToImmutableArray(),
            AiConversation = (AiConversation ?? KadrStudio.Core.Domain.AiConversation.Create())
                .RecoverInterruptedOperations(),
            InPoint = InPointTicks is { } inTicks ? new TimelineTime(inTicks) : null,
            OutPoint = OutPointTicks is { } outTicks ? new TimelineTime(outTicks) : null
        };
    }

    private sealed record SequenceDocument
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public required long Revision { get; init; }
        public required SequenceStatus Status { get; init; }
        public required MontageTargetFormat TargetFormat { get; init; }
        public required int CanvasWidth { get; init; }
        public required int CanvasHeight { get; init; }
        public required int FrameRateNumerator { get; init; }
        public required int FrameRateDenominator { get; init; }
        public required int AudioSampleRate { get; init; }
        public required TimelineTrack[] Tracks { get; init; }
        public required MediaClip[] MediaClips { get; init; }
        public required TextClip[] TextClips { get; init; }
        public required TimelineTransition[] Transitions { get; init; }
        public required TimelineMarker[] Markers { get; init; }
        public long? InPointTicks { get; init; }
        public long? OutPointTicks { get; init; }
        public Guid? ParentSequenceId { get; init; }
        public Guid? MontagePlanId { get; init; }

        public static SequenceDocument FromState(SequenceState sequence) => new()
        {
            Id = sequence.Id,
            Name = sequence.Name,
            Revision = sequence.Revision,
            Status = sequence.Status,
            TargetFormat = sequence.TargetFormat,
            CanvasWidth = sequence.Settings.CanvasWidth,
            CanvasHeight = sequence.Settings.CanvasHeight,
            FrameRateNumerator = sequence.Settings.FrameRate.Numerator,
            FrameRateDenominator = sequence.Settings.FrameRate.Denominator,
            AudioSampleRate = sequence.Settings.AudioSampleRate,
            Tracks = sequence.Tracks.ToArray(),
            MediaClips = sequence.MediaClips.ToArray(),
            TextClips = sequence.TextClips.ToArray(),
            Transitions = sequence.Transitions.ToArray(),
            Markers = sequence.Markers.ToArray(),
            InPointTicks = sequence.InPoint?.Ticks,
            OutPointTicks = sequence.OutPoint?.Ticks,
            ParentSequenceId = sequence.ParentSequenceId,
            MontagePlanId = sequence.MontagePlanId
        };

        public SequenceState ToState() => new(
            Id,
            Name,
            Revision,
            Status,
            TargetFormat,
            new SequenceSettings(
                CanvasWidth, CanvasHeight, new FrameRate(FrameRateNumerator, FrameRateDenominator), AudioSampleRate),
            Tracks.ToImmutableArray(),
            MediaClips.ToImmutableArray(),
            TextClips.ToImmutableArray(),
            Transitions.ToImmutableArray(),
            Markers.ToImmutableArray(),
            InPointTicks is { } inTicks ? new TimelineTime(inTicks) : null,
            OutPointTicks is { } outTicks ? new TimelineTime(outTicks) : null,
            ParentSequenceId,
            MontagePlanId);
    }
}
