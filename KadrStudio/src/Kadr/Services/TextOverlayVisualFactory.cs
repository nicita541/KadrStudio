using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

/// <summary>Single project-pixel layout used by editable preview and export rasterization.</summary>
public static class TextOverlayVisualFactory
{
    public static Border Create(string content, TextStyle style, double canvasWidth, double canvasHeight)
    {
        var text = new TextBlock();
        var border = new Border { Child = text };
        Apply(border, text, content, style, canvasWidth, canvasHeight);
        return border;
    }

    public static void Apply(Border border, TextBlock text, string content, TextStyle style,
        double canvasWidth, double canvasHeight)
    {
        text.Text = content;
        text.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily);
        text.FontSize = style.FontSize;
        text.FontWeight = FontWeights.SemiBold;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextAlignment = TextAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Stretch;
        text.Margin = new Thickness(8);
        try { text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Color)); }
        catch (FormatException) { text.Foreground = Brushes.White; }
        catch (NotSupportedException) { text.Foreground = Brushes.White; }
        text.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Color = Colors.Black, Opacity = .9 };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(text, TextRenderingMode.Grayscale);
        border.Width = Math.Clamp(style.BoxWidth * canvasWidth, Math.Min(80, canvasWidth), canvasWidth);
        border.Height = Math.Clamp(style.BoxHeight * canvasHeight, Math.Min(36, canvasHeight), canvasHeight);
        border.Background = new SolidColorBrush(Color.FromArgb(102, 0, 0, 0));
        border.CornerRadius = new CornerRadius(4);
        border.Padding = new Thickness(8, 3, 8, 3);
        border.RenderTransformOrigin = new Point(.5, .5);
        border.RenderTransform = new RotateTransform(style.Rotation);
        Canvas.SetLeft(border, Math.Clamp(style.X * canvasWidth - border.Width / 2, 0, canvasWidth - border.Width));
        Canvas.SetTop(border, Math.Clamp(style.Y * canvasHeight - border.Height / 2, 0, canvasHeight - border.Height));
    }
}
