using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Views;

namespace KadrStudio.UiAdapters.Tests;

public sealed class AiChatRenderTests
{
    [Fact]
    public void Empty_chat_and_composer_render_at_supported_dpi_scales()
        => RunSta(() =>
        {
            var app = System.Windows.Application.Current ?? new KadrStudio.App();
            if (app.Resources.Count == 0 && app is KadrStudio.App kadrApp) kadrApp.InitializeComponent();
            var window = new MainWindow();
            var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("AiChatPanel"));
            Assert.NotNull(window.FindName("AiChatMessagesListBox"));
            Assert.NotNull(window.FindName("AiChatPromptTextBox"));
            Assert.NotNull(window.FindName("AiChatScenarioComboBox"));

            foreach (var dpi in new[] { 96d, 120d, 144d })
            {
                panel.Width = 360;
                panel.Height = 700;
                panel.Measure(new Size(360, 700));
                panel.Arrange(new Rect(0, 0, 360, 700));
                panel.UpdateLayout();
                var width = (int)Math.Ceiling(360 * dpi / 96d);
                var height = (int)Math.Ceiling(700 * dpi / 96d);
                var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                Assert.True(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                    $"AI chat rendered transparent output at {dpi / 96d:P0} scale.");
            }
        });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
