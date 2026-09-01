using KadrStudio.Models;

namespace KadrStudio.Controls;

public readonly record struct TrackAddress(TrackKind Kind, int Index);

public sealed class TrackLayout(
    int visualCount,
    int audioCount,
    bool hasTextTrack,
    double areaTop = 35,
    double trackHeight = 54,
    double trackGap = 5,
    double bottomPadding = 12)
{
    public int VisualCount { get; } = Math.Max(1, visualCount);
    public int AudioCount { get; } = Math.Max(1, audioCount);
    public bool HasTextTrack { get; } = hasTextTrack;
    public double TrackHeight => trackHeight;

    public double GetTrackTop(TrackKind kind, int index)
    {
        var textCount = HasTextTrack ? 1 : 0;
        var slot = kind == TrackKind.Visual
            ? textCount + VisualCount - 1 - Math.Clamp(index, 0, VisualCount - 1)
            : textCount + VisualCount + Math.Clamp(index, 0, AudioCount - 1);
        return areaTop + slot * (trackHeight + trackGap);
    }

    public double TextTrackTop => areaTop;

    public bool IntersectsViewport(double trackTop, double viewportOffset, double viewportHeight)
        => viewportHeight <= 0 ||
           trackTop + trackHeight >= viewportOffset && trackTop <= viewportOffset + viewportHeight;

    public TrackAddress? GetTrackAt(double y)
    {
        if (y < areaTop) return null;
        var slot = (int)((y - areaTop) / (trackHeight + trackGap));
        var textCount = HasTextTrack ? 1 : 0;
        if (slot < textCount) return new TrackAddress(TrackKind.Visual, 0);
        var mediaSlot = slot - textCount;
        if (mediaSlot < VisualCount) return new TrackAddress(TrackKind.Visual, VisualCount - 1 - mediaSlot);
        var audioIndex = mediaSlot - VisualCount;
        return audioIndex < AudioCount ? new TrackAddress(TrackKind.Audio, audioIndex) : null;
    }

    public double RequiredHeight => areaTop + (VisualCount + AudioCount + (HasTextTrack ? 1 : 0)) *
        (trackHeight + trackGap) - trackGap + bottomPadding;
}
