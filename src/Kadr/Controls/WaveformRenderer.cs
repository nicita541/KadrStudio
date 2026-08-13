using System.Windows;
using System.Windows.Media;
using KadrStudio.Application.Caching;

namespace KadrStudio.Controls;

public sealed class WaveformRenderer
{
    private static readonly Pen LeftPen = FrozenPen(Color.FromRgb(167, 243, 208));
    private static readonly Pen RightPen = FrozenPen(Color.FromRgb(110, 231, 183));
    private static readonly Pen CenterPen = FrozenPen(Color.FromArgb(95, 209, 250, 229));

    public void Draw(DrawingContext context, WaveformPyramid pyramid, Rect area,
        double sourceStartRatio, double sourceEndRatio, double dpiScale)
    {
        if (pyramid.IsEmpty || area.IsEmpty) return;
        var columnCount = new TimelineViewport(1, 0, area.Width, 0).ColumnCount(area.Width, dpiScale);
        var peaks = pyramid.ReadColumns(sourceStartRatio, sourceEndRatio, columnCount);
        var step = area.Width / peaks.Length;
        var channelHeight = area.Height / 2;
        var leftCenter = area.Top + channelHeight / 2;
        var rightCenter = area.Top + channelHeight + channelHeight / 2;
        context.DrawLine(CenterPen, new Point(area.Left, area.Top + channelHeight), new Point(area.Right, area.Top + channelHeight));
        for (var index = 0; index < peaks.Length; index++)
        {
            var peak = peaks[index];
            var x = area.Left + (index + 0.5) * step;
            DrawChannel(context, LeftPen, x, leftCenter, channelHeight, peak.MinimumLeft, peak.MaximumLeft);
            DrawChannel(context, RightPen, x, rightCenter, channelHeight, peak.MinimumRight, peak.MaximumRight);
        }
    }

    private static void DrawChannel(DrawingContext context, Pen pen, double x, double center,
        double height, float minimum, float maximum)
    {
        var amplitude = Math.Max(1, height / 2 - 1);
        var top = center - maximum * amplitude;
        var bottom = center - minimum * amplitude;
        if (Math.Abs(bottom - top) < 0.5) return;
        context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
    }

    private static Pen FrozenPen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
