using System.Collections.Immutable;

namespace KadrStudio.Core.Domain;

public enum TrackKind
{
    Visual,
    Audio,
    Text
}

public enum MediaKind
{
    Video,
    Audio,
    Image
}

public enum MarkerKind
{
    Scene,
    Opening,
    Ending,
    PostCredits,
    Preview,
    Recap,
    BlackFrame,
    Silence,
    Freeze,
    Note
}

public sealed record TimelineTrack(
    Guid Id,
    TrackKind Kind,
    int Index,
    string Name,
    bool IsMuted = false,
    bool IsLocked = false,
    bool IsVisible = true);

public sealed record MediaSource(
    Guid Id,
    string Path,
    string Name,
    MediaKind Kind,
    TimelineTime Duration,
    bool HasAudio,
    int Width = 0,
    int Height = 0,
    FrameRate? FrameRate = null,
    string VideoCodec = "",
    string AudioCodec = "",
    long FileSize = 0,
    long LastWriteUtcTicks = 0,
    string Fingerprint = "");

public sealed record VideoParameters(
    double Brightness = 0,
    double Contrast = 1,
    double Saturation = 1,
    double Temperature = 0);

public sealed record AudioParameters(
    double Volume = 1,
    bool IsMuted = false,
    double Pan = 0,
    TimelineTime FadeIn = default,
    TimelineTime FadeOut = default,
    double Bass = 0,
    double Mid = 0,
    double Treble = 0);

public sealed record MediaClip(
    Guid Id,
    Guid SourceId,
    Guid TrackId,
    TimelineTime Start,
    TimelineTime SourceIn,
    TimelineTime Duration,
    Guid? LinkGroupId = null,
    VideoParameters? Video = null,
    AudioParameters? Audio = null)
{
    public TimelineTime End => Start + Duration;
    public TimeRange Range => new(Start, Duration);
}

public sealed record TextStyle(
    string FontFamily = "Segoe UI",
    double FontSize = 48,
    string Color = "#FFFFFF",
    double X = 0.5,
    double Y = 0.82,
    double Rotation = 0,
    double BoxWidth = 0.6,
    double BoxHeight = 0.18,
    bool IsSubtitle = false);

public sealed record TextClip(
    Guid Id,
    Guid TrackId,
    TimelineTime Start,
    TimelineTime Duration,
    string Text,
    TextStyle Style)
{
    public TimelineTime End => Start + Duration;
    public TimeRange Range => new(Start, Duration);
}

public sealed record TimelineMarker(
    Guid Id,
    MarkerKind Kind,
    TimelineTime Start,
    TimelineTime Duration,
    string Title,
    string Description = "",
    Guid? SourceId = null,
    TimelineTime SourceStart = default,
    double Confidence = 0,
    string Query = "")
{
    public TimelineTime End => Start + Duration;
    public TimeRange Range => new(Start, Duration);
}

public sealed record ProjectState
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Новый проект";
    public int CanvasWidth { get; init; } = 1920;
    public int CanvasHeight { get; init; } = 1080;
    public FrameRate FrameRate { get; init; } = FrameRate.Fps30;
    public long Revision { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ImmutableArray<TimelineTrack> Tracks { get; init; } = [];
    public ImmutableDictionary<Guid, MediaSource> Sources { get; init; } = ImmutableDictionary<Guid, MediaSource>.Empty;
    public ImmutableArray<MediaClip> MediaClips { get; init; } = [];
    public ImmutableArray<TextClip> TextClips { get; init; } = [];
    public ImmutableArray<TimelineMarker> Markers { get; init; } = [];
    public TimelineTime? InPoint { get; init; }
    public TimelineTime? OutPoint { get; init; }

    public TimelineTime Duration
    {
        get
        {
            var mediaEnd = MediaClips.IsDefaultOrEmpty ? TimelineTime.Zero : MediaClips.Max(item => item.End);
            var textEnd = TextClips.IsDefaultOrEmpty ? TimelineTime.Zero : TextClips.Max(item => item.End);
            return mediaEnd >= textEnd ? mediaEnd : textEnd;
        }
    }

    public TimelineTrack? FindTrack(Guid id) => Tracks.FirstOrDefault(item => item.Id == id);
    public MediaClip? FindMediaClip(Guid id) => MediaClips.FirstOrDefault(item => item.Id == id);
    public TextClip? FindTextClip(Guid id) => TextClips.FirstOrDefault(item => item.Id == id);

    public static ProjectState CreateNew(string name = "Новый проект", FrameRate? frameRate = null)
    {
        var tracks = ImmutableArray.Create(
            new TimelineTrack(Guid.NewGuid(), TrackKind.Visual, 0, "V1"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Visual, 1, "V2"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, 0, "A1"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Audio, 1, "A2"),
            new TimelineTrack(Guid.NewGuid(), TrackKind.Text, 0, "T1"));
        return new ProjectState
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Новый проект" : name.Trim(),
            FrameRate = frameRate ?? FrameRate.Fps30,
            Tracks = tracks
        };
    }
}
