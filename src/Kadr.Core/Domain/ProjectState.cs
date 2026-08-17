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

public enum MediaOnlineState
{
    Online,
    Offline,
    Relinking,
    Incompatible
}

public enum MediaStreamKind
{
    Video,
    Audio
}

public enum TransitionKind
{
    CrossDissolve,
    DipToBlack,
    DipToWhite,
    Wipe,
    Slide,
    ConstantPowerAudio
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

public sealed record SequenceSettings
{
    public SequenceSettings(
        int canvasWidth = 1920,
        int canvasHeight = 1080,
        FrameRate? frameRate = null,
        int audioSampleRate = 48_000)
    {
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        FrameRate = frameRate ?? FrameRate.Fps30;
        AudioSampleRate = audioSampleRate;
    }

    public int CanvasWidth { get; init; }
    public int CanvasHeight { get; init; }
    public FrameRate FrameRate { get; init; }
    public int AudioSampleRate { get; init; }
    public static SequenceSettings Default { get; } = new();
}

public sealed record MediaStreamDescriptor(
    int StreamIndex,
    MediaStreamKind Kind,
    string Codec,
    string PixelOrSampleFormat = "",
    int Width = 0,
    int Height = 0,
    int SampleRate = 0,
    int Channels = 0,
    FrameRate? FrameRate = null,
    bool IsVariableFrameRate = false);

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
    string Fingerprint = "",
    string PreviousPath = "",
    MediaOnlineState OnlineState = MediaOnlineState.Online,
    string FastFingerprint = "",
    string VerifiedFingerprint = "",
    ImmutableArray<MediaStreamDescriptor> Streams = default,
    bool IsVariableFrameRate = false,
    string ProxyPath = "");

public sealed record VideoParameters(
    double Brightness = 0,
    double Contrast = 1,
    double Saturation = 1,
    double Temperature = 0,
    double PositionX = 0.5,
    double PositionY = 0.5,
    double ScaleX = 1,
    double ScaleY = 1,
    double Rotation = 0,
    double CropLeft = 0,
    double CropTop = 0,
    double CropRight = 0,
    double CropBottom = 0,
    double Opacity = 1);

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

public sealed record TimelineTransition(
    Guid Id,
    TransitionKind Kind,
    Guid TrackId,
    Guid FromClipId,
    Guid ToClipId,
    TimelineTime Start,
    TimelineTime Duration)
{
    public TimelineTime End => Start + Duration;
    public TimeRange Range => new(Start, Duration);
}

public sealed record ProjectState
{
    private SequenceSettings _sequence = SequenceSettings.Default;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Новый проект";
    public SequenceSettings Sequence { get => _sequence; init => _sequence = value ?? SequenceSettings.Default; }
    public int CanvasWidth { get => _sequence.CanvasWidth; init => _sequence = _sequence with { CanvasWidth = value }; }
    public int CanvasHeight { get => _sequence.CanvasHeight; init => _sequence = _sequence with { CanvasHeight = value }; }
    public FrameRate FrameRate { get => _sequence.FrameRate; init => _sequence = _sequence with { FrameRate = value }; }
    public long Revision { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public ImmutableArray<TimelineTrack> Tracks { get; init; } = [];
    public ImmutableDictionary<Guid, MediaSource> Sources { get; init; } = ImmutableDictionary<Guid, MediaSource>.Empty;
    public ImmutableArray<MediaClip> MediaClips { get; init; } = [];
    public ImmutableArray<TextClip> TextClips { get; init; } = [];
    public ImmutableArray<TimelineTransition> Transitions { get; init; } = [];
    public ImmutableArray<TimelineMarker> Markers { get; init; } = [];
    public ImmutableArray<SequenceState> Sequences { get; init; } = [];
    public Guid? ActiveSequenceId { get; init; }
    public ImmutableArray<SourceAnnotation> SourceAnnotations { get; init; } = [];
    public ImmutableArray<MediaAnalysisReference> AnalysisReferences { get; init; } = [];
    public ImmutableArray<MontagePlan> MontagePlans { get; init; } = [];
    public TimelineTime? InPoint { get; init; }
    public TimelineTime? OutPoint { get; init; }

    public SequenceState? ActiveSequence => ActiveSequenceId is { } id
        ? Sequences.FirstOrDefault(item => item.Id == id)
        : null;

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

    public SequenceState? FindSequence(Guid id) => Sequences.FirstOrDefault(item => item.Id == id);
    public MontagePlan? FindMontagePlan(Guid id) => MontagePlans.FirstOrDefault(item => item.Id == id);

    public ProjectState EnsureSequenceContainer(string name = "Исходный монтаж")
    {
        if (!Sequences.IsDefaultOrEmpty && ActiveSequence is not null) return this;
        var id = Guid.NewGuid();
        var sequence = SequenceState.Capture(this, id, name);
        return this with { Sequences = [sequence], ActiveSequenceId = id };
    }

    public ProjectState SynchronizeActiveSequence(bool incrementRevision = true)
    {
        var active = ActiveSequence;
        if (active is null || active.Matches(this)) return this;
        var replacement = active.CaptureTimeline(this, incrementRevision);
        return this with
        {
            Sequences = Sequences.Select(item => item.Id == replacement.Id ? replacement : item).ToImmutableArray()
        };
    }

    public ProjectState ActivateSequence(Guid sequenceId)
    {
        var synchronized = SynchronizeActiveSequence();
        var target = synchronized.FindSequence(sequenceId)
            ?? throw new InvalidOperationException("Последовательность не найдена.");
        return synchronized with
        {
            ActiveSequenceId = target.Id,
            Sequence = target.Settings,
            Tracks = target.Tracks,
            MediaClips = target.MediaClips,
            TextClips = target.TextClips,
            Transitions = target.Transitions,
            Markers = target.Markers,
            InPoint = target.InPoint,
            OutPoint = target.OutPoint
        };
    }

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
