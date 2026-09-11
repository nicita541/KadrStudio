using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KadrStudio.Core.Domain;
using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TextOverlayLayoutTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1080, 1920)]
    public void Layout_uses_project_pixels_and_keeps_wrapping_and_rotation(int width, int height)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var style = new TextStyle(FontSize: 48, X: .95, Y: .05, Rotation: 25, BoxWidth: .3, BoxHeight: .2);
                var border = TextOverlayVisualFactory.Create("Несколько строк текста для проверки переноса", style, width, height);
                var text = Assert.IsType<TextBlock>(border.Child);
                Assert.Equal(48, text.FontSize);
                Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                Assert.Equal(25, Assert.IsType<RotateTransform>(border.RenderTransform).Angle);
                Assert.Equal(width * .3, border.Width);
                Assert.Equal(height * .2, border.Height);
                Assert.InRange(Canvas.GetLeft(border), 0, width - border.Width);
                Assert.InRange(Canvas.GetTop(border), 0, height - border.Height);
                border.Measure(new Size(border.Width, border.Height));
                border.Arrange(new Rect(0, 0, border.Width, border.Height));
                Assert.True(text.ActualHeight > 0);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
