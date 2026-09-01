using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KadrStudio.Application.Upscaling;
using KadrStudio.Services;

namespace KadrStudio.Views;

public partial class UpscaleComparisonWindow : Window
{
    private readonly string _temporaryDirectory;
    private double _dividerRatio = 0.5;
    private bool _draggingDivider;
    private bool _panning;
    private Point _lastPoint;

    public UpscaleComparisonWindow(UpscaleComparisonFrames frames)
    {
        InitializeComponent();
        _temporaryDirectory = frames.TemporaryDirectory;
        OriginalImage.Source = LoadImage(frames.OriginalFramePath);
        UpscaledImage.Source = LoadImage(frames.UpscaledFramePath);
        Closed += (_, _) => AnimeSrUpscaleService.DeleteComparisonDirectory(_temporaryDirectory);
    }

    private static BitmapImage LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void CompareViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDivider();

    private void CompareViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scale = Math.Clamp(SharedScale.ScaleX * (e.Delta > 0 ? 1.12 : 1 / 1.12), 1, 8);
        SharedScale.ScaleX = SharedScale.ScaleY = scale;
        e.Handled = true;
    }

    private void CompareViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _lastPoint = e.GetPosition(CompareViewport);
        _panning = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _draggingDivider = !_panning;
        CompareViewport.CaptureMouse();
        UpdateFromPoint(_lastPoint);
    }

    private void CompareViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(CompareViewport);
        if (_panning)
        {
            SharedTranslate.X += point.X - _lastPoint.X;
            SharedTranslate.Y += point.Y - _lastPoint.Y;
        }
        else if (_draggingDivider) UpdateFromPoint(point);
        _lastPoint = point;
    }

    private void CompareViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panning = _draggingDivider = false;
        CompareViewport.ReleaseMouseCapture();
    }

    private void UpdateFromPoint(Point point)
    {
        if (CompareViewport.ActualWidth <= 0) return;
        _dividerRatio = Math.Clamp(point.X / CompareViewport.ActualWidth, 0, 1);
        UpdateDivider();
    }

    private void UpdateDivider()
    {
        var width = CompareViewport.ActualWidth;
        var height = CompareViewport.ActualHeight;
        var x = width * _dividerRatio;
        OriginalImage.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, x, height));
        UpscaledImage.Clip = new System.Windows.Media.RectangleGeometry(new Rect(x, 0, Math.Max(0, width - x), height));
        Divider.Margin = new Thickness(Math.Max(0, x - Divider.Width / 2), 0, 0, 0);
    }
}
