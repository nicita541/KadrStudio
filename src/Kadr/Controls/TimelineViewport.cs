namespace KadrStudio.Controls;

public readonly record struct TimelineViewport(
    double PixelsPerSecond,
    double HorizontalOffset,
    double ViewportWidth,
    double LeftGutterWidth)
{
    public double VisibleTimelineStart => Math.Max(0, HorizontalOffset / Math.Max(0.0001, PixelsPerSecond));
    public double VisibleTimelineEnd => Math.Max(VisibleTimelineStart,
        (HorizontalOffset + Math.Max(0, ViewportWidth - LeftGutterWidth)) / Math.Max(0.0001, PixelsPerSecond));
    public double TimeToContentX(double seconds) => LeftGutterWidth + seconds * PixelsPerSecond;
    public double ContentXToTime(double x) => Math.Max(0, (x - LeftGutterWidth) / PixelsPerSecond);
    public int ColumnCount(double visiblePixelWidth, double dpiScale, double physicalPixelsPerColumn = 2)
        => Math.Max(1, (int)Math.Ceiling(visiblePixelWidth * Math.Max(1, dpiScale) / physicalPixelsPerColumn));
}
