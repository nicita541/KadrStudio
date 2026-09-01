namespace KadrStudio.Models;

public enum MediaKind
{
    Video,
    Audio,
    Image
}

public enum TrackKind
{
    Visual,
    Audio
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

public enum ExportResolution
{
    P480,
    P720,
    P1080
}
