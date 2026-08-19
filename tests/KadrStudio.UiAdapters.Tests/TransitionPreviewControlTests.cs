using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Controls;
using KadrStudio.Core.Domain;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TransitionPreviewControlTests
{
    [Fact]
    public void Every_transition_preset_renders_a_nonempty_card()
        => RunSta(() =>
        {
            foreach (var kind in Enum.GetValues<TransitionKind>())
            {
                var control = new TransitionPreviewControl { Kind = kind, Width = 180, Height = 72 };
                control.Measure(new Size(180, 72));
                control.Arrange(new Rect(0, 0, 180, 72));
                control.UpdateLayout();
                var bitmap = new RenderTargetBitmap(180, 72, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(control);
                var pixels = new byte[180 * 72 * 4];
                bitmap.CopyPixels(pixels, 180 * 4, 0);
                Assert.True(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0),
                    $"Preview for {kind} rendered transparent output.");
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
