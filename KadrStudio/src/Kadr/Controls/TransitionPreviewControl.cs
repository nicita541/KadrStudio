using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using KadrStudio.Core.Domain;

namespace KadrStudio.Controls;

public sealed class TransitionPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(TransitionKind), typeof(TransitionPreviewControl),
        new FrameworkPropertyMetadata(TransitionKind.CrossDissolve, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Stopwatch _clock = new();

    public TransitionKind Kind
    {
        get => (TransitionKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public TransitionPreviewControl()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        IsMouseDirectlyOverChanged += (_, _) => UpdateAnimationState();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        if (bounds.Width < 2 || bounds.Height < 2) return;

        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(17, 18, 23)), null, bounds, 6, 6);
        var frame = new Rect(4, 4, Math.Max(0, bounds.Width - 8), Math.Max(0, bounds.Height - 8));
        var progress = _clock.IsRunning ? (_clock.Elapsed.TotalSeconds % 1.4) / 1.4 : 0;
        DrawTransition(drawingContext, frame, progress);
        drawingContext.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(71, 73, 84)), 1), frame, 4, 4);
    }

    private void DrawTransition(DrawingContext context, Rect frame, double progress)
    {
        var first = new SolidColorBrush(Color.FromRgb(40, 112, 176));
        var second = new SolidColorBrush(Color.FromRgb(139, 80, 223));
        switch (Kind)
        {
            case TransitionKind.CrossDissolve:
                context.DrawRectangle(first, null, frame);
                context.PushOpacity(progress);
                context.DrawRectangle(second, null, frame);
                context.Pop();
                break;
            case TransitionKind.DipToBlack:
            case TransitionKind.DipToWhite:
                context.DrawRectangle(progress < 0.5 ? first : second, null, frame);
                context.PushOpacity(1 - Math.Abs(progress * 2 - 1));
                context.DrawRectangle(Kind == TransitionKind.DipToWhite ? Brushes.White : Brushes.Black, null, frame);
                context.Pop();
                break;
            case TransitionKind.Wipe:
                context.DrawRectangle(first, null, frame);
                context.PushClip(new RectangleGeometry(new Rect(frame.X, frame.Y, frame.Width * progress, frame.Height)));
                context.DrawRectangle(second, null, frame);
                context.Pop();
                break;
            case TransitionKind.Slide:
                context.DrawRectangle(first, null, new Rect(frame.X - frame.Width * progress, frame.Y, frame.Width, frame.Height));
                context.DrawRectangle(second, null, new Rect(frame.X + frame.Width * (1 - progress), frame.Y, frame.Width, frame.Height));
                break;
            case TransitionKind.ConstantPowerAudio:
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 46, 40)), null, frame);
                DrawWave(context, frame, first, 1 - progress, 0);
                DrawWave(context, frame, second, progress, Math.PI / 2);
                break;
        }
    }

    private static void DrawWave(DrawingContext context, Rect frame, Brush brush, double opacity, double phase)
    {
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            for (var index = 0; index <= 32; index++)
            {
                var ratio = index / 32d;
                var point = new Point(
                    frame.X + ratio * frame.Width,
                    frame.Y + frame.Height / 2 + Math.Sin(ratio * Math.PI * 8 + phase) * frame.Height * 0.22);
                if (index == 0) sink.BeginFigure(point, false, false);
                else sink.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        context.PushOpacity(Math.Clamp(opacity, 0.18, 1));
        context.DrawGeometry(null, new Pen(brush, 2), geometry);
        context.Pop();
    }

    private void UpdateAnimationState()
    {
        var hovered = IsMouseOver || FindHoveredAncestor();
        if (hovered && !_clock.IsRunning) _clock.Start();
        else if (!hovered && _clock.IsRunning)
        {
            _clock.Reset();
            InvalidateVisual();
        }
    }

    private bool FindHoveredAncestor()
    {
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current is FrameworkElement element)
        {
            if (element.IsMouseOver) return true;
            current = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        UpdateAnimationState();
        if (_clock.IsRunning) InvalidateVisual();
    }
}
