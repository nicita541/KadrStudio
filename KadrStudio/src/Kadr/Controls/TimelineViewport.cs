using System.Windows;

namespace KadrStudio.Controls;

public readonly record struct TimelineViewport(
    double PixelsPerSecond,
    double HorizontalOffset,
    double ViewportWidth,
    double LeftGutterWidth)
{
    private double SafeScale => Math.Max(0.0001, PixelsPerSecond);
    public double VisibleContentLeft => HorizontalOffset + LeftGutterWidth;
    public double VisibleContentRight => ViewportWidth > 0
        ? HorizontalOffset + ViewportWidth
        : double.PositiveInfinity;
    public double VisibleTimelineStart => ContentXToTime(VisibleContentLeft);
    public double VisibleTimelineEnd => double.IsPositiveInfinity(VisibleContentRight)
        ? double.PositiveInfinity
        : Math.Max(VisibleTimelineStart, ContentXToTime(VisibleContentRight));
    public double TimeToContentX(double seconds) => LeftGutterWidth + seconds * PixelsPerSecond;
    public double ContentXToTime(double x) => Math.Max(0, (x - LeftGutterWidth) / SafeScale);
    public double DurationToPixels(double seconds) => Math.Max(0, seconds) * SafeScale;
    public double PixelsToDuration(double pixels) => pixels / SafeScale;
    public Rect ClipToVisible(Rect source, double inset = 0)
    {
        var left = Math.Max(source.Left, VisibleContentLeft + inset);
        var right = Math.Min(source.Right, VisibleContentRight - inset);
        return right > left ? new Rect(left, source.Top, right - left, source.Height) : Rect.Empty;
    }
    public int ColumnCount(double visiblePixelWidth, double dpiScale, double physicalPixelsPerColumn = 2)
        => Math.Max(1, (int)Math.Ceiling(visiblePixelWidth * Math.Max(1, dpiScale) / physicalPixelsPerColumn));
}
