using System.Windows;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using KadrStudio.Application.Caching;

namespace KadrStudio.Controls;

public sealed class WaveformRenderer
{
    private static readonly Pen PeakPen = FrozenPen(Color.FromRgb(183, 255, 220), 1.15);
    private static readonly Pen RmsPen = FrozenPen(Color.FromArgb(150, 52, 211, 153), 2.2);
    private static readonly Pen CenterPen = FrozenPen(Color.FromArgb(95, 209, 250, 229));
    private readonly ConditionalWeakTable<WaveformPyramid, DisplayScale> _displayScales = new();

    public void Draw(DrawingContext context, WaveformPyramid pyramid, Rect area,
        double sourceStartRatio, double sourceEndRatio, double dpiScale)
    {
        if (pyramid.IsEmpty || area.IsEmpty) return;
        var columnCount = new TimelineViewport(1, 0, area.Width, 0).ColumnCount(area.Width, dpiScale);
        var peaks = pyramid.ReadColumns(sourceStartRatio, sourceEndRatio, columnCount);
        var step = area.Width / peaks.Length;
        var center = area.Top + area.Height / 2;
        var amplitudeHeight = Math.Max(1, area.Height / 2 - 1);
        var gain = _displayScales.GetValue(pyramid, CalculateDisplayScale).Gain;
        context.DrawLine(CenterPen, new Point(area.Left, center), new Point(area.Right, center));
        for (var index = 0; index < peaks.Length; index++)
        {
            var peak = peaks[index];
            var x = area.Left + (index + 0.5) * step;
            var maximum = Math.Max(
                Math.Max(Math.Abs(peak.MinimumLeft), Math.Abs(peak.MaximumLeft)),
                Math.Max(Math.Abs(peak.MinimumRight), Math.Abs(peak.MaximumRight)));
            var rms = Math.Max(peak.RmsLeft, peak.RmsRight);
            DrawEnvelope(context, x, center, amplitudeHeight, rms * gain, maximum * gain);
        }
    }

    private static void DrawEnvelope(
        DrawingContext context,
        double x,
        double center,
        double amplitudeHeight,
        double rms,
        double peak)
    {
        peak = Math.Clamp(peak, 0, 1);
        if (peak < 0.002) return;
        rms = Math.Clamp(rms, 0, peak);
        var peakPixels = Math.Max(0.55, peak * amplitudeHeight);
        var rmsPixels = Math.Max(0.45, rms * amplitudeHeight);
        context.DrawLine(PeakPen,
            new Point(x, center - peakPixels),
            new Point(x, center + peakPixels));
        if (rms > 0.002)
        {
            context.DrawLine(RmsPen,
                new Point(x, center - rmsPixels),
                new Point(x, center + rmsPixels));
        }
    }

    private static DisplayScale CalculateDisplayScale(WaveformPyramid pyramid)
    {
        var source = pyramid.Levels[0].Peaks;
        if (source.IsDefaultOrEmpty) return new DisplayScale(1);
        var stride = Math.Max(1, source.Length / 4_096);
        var magnitudes = new List<float>(Math.Min(source.Length, 4_096));
        for (var index = 0; index < source.Length; index += stride)
        {
            var peak = source[index];
            var magnitude = Math.Max(
                Math.Max(Math.Abs(peak.MinimumLeft), Math.Abs(peak.MaximumLeft)),
                Math.Max(Math.Abs(peak.MinimumRight), Math.Abs(peak.MaximumRight)));
            if (magnitude >= 0.002f) magnitudes.Add(magnitude);
        }
        if (magnitudes.Count == 0) return new DisplayScale(1);
        magnitudes.Sort();
        var reference = magnitudes[(int)Math.Floor((magnitudes.Count - 1) * 0.92)];
        return new DisplayScale(Math.Clamp(0.9 / Math.Max(0.02, reference), 1, 12));
    }

    private static Pen FrozenPen(Color color, double thickness = 1)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private sealed record DisplayScale(double Gain);
}
