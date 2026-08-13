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
            Sources = project.Sources.Values.ToArray(),
            MediaClips = project.MediaClips.ToArray(),
            TextClips = project.TextClips.ToArray(),
            Transitions = project.Transitions.ToArray(),
            Markers = project.Markers.ToArray(),
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
            InPoint = InPointTicks is { } inTicks ? new TimelineTime(inTicks) : null,
            OutPoint = OutPointTicks is { } outTicks ? new TimelineTime(outTicks) : null
        };
    }
}
