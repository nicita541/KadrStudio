using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Application.Rendering;

namespace KadrStudio.Services;

public static class TextOverlayRasterizer
{
    public static Task<OverlayRasterLease> PrepareAsync(RenderPlan plan, int width, int height, CancellationToken token)
    {
        var completion = new TaskCompletionSource<OverlayRasterLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            OverlayRasterLease? lease = null;
            try
            {
                token.ThrowIfCancellationRequested();
                var root = Path.Combine(KadrLocalDataPaths.TempRoot, "text-layout-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                lease = new OverlayRasterLease(root, plan);
                var layers = ImmutableArray.CreateBuilder<RenderTextLayer>();
                foreach (var layer in plan.TextLayers)
                {
                    token.ThrowIfCancellationRequested();
                    var canvas = new Canvas { Width = plan.CanvasWidth, Height = plan.CanvasHeight, ClipToBounds = true };
                    canvas.Children.Add(TextOverlayVisualFactory.Create(layer.Text, layer.Style, plan.CanvasWidth, plan.CanvasHeight));
                    canvas.Measure(new Size(plan.CanvasWidth, plan.CanvasHeight));
                    canvas.Arrange(new Rect(0, 0, plan.CanvasWidth, plan.CanvasHeight));
                    canvas.UpdateLayout();
                    var fontIdentities = new SortedSet<string>(StringComparer.Ordinal);
                    CollectFonts(canvas, fontIdentities);
                    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        version = 1, layer.Text, layer.Style, plan.CanvasWidth, plan.CanvasHeight, width, height,
                        fonts = fontIdentities.ToArray()
                    }))));
                    var path = Path.Combine(root, key + ".png");
                    if (!File.Exists(path))
                    {
                        var bitmap = new RenderTargetBitmap(width, height, 96.0 * width / plan.CanvasWidth,
                            96.0 * height / plan.CanvasHeight, PixelFormats.Pbgra32);
                        bitmap.Render(canvas);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        lease.Own(path);
                        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        encoder.Save(file);
                    }
                    layers.Add(layer with { RasterPath = path, RasterIdentity = key, RasterFontIdentities = fontIdentities.ToImmutableArray() });
                }
                token.ThrowIfCancellationRequested();
                lease.Plan = plan with
                {
                    TextLayers = layers.ToImmutable(),
                    OverlaySignature = plan.OverlaySignature + ":raster:" + string.Join(':', layers.Select(layer => layer.RasterIdentity))
                };
                completion.TrySetResult(lease);
            }
            catch (OperationCanceledException) { lease?.Dispose(); completion.TrySetCanceled(token); }
            catch (Exception error) { lease?.Dispose(); completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Kadr text layout" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static void CollectFonts(Visual visual, ISet<string> identities)
    {
        CollectDrawing(VisualTreeHelper.GetDrawing(visual), identities);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            if (VisualTreeHelper.GetChild(visual, index) is Visual child) CollectFonts(child, identities);
    }

    private static void CollectDrawing(Drawing? drawing, ISet<string> identities)
    {
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children) CollectDrawing(child, identities);
        else if (drawing is GlyphRunDrawing glyph)
        {
            var font = glyph.GlyphRun.GlyphTypeface;
            var uri = font.FontUri;
            var identity = uri.ToString() + ":" + font.StyleSimulations;
            if (uri.IsFile)
            {
                using var stream = File.OpenRead(uri.LocalPath);
                identity += ":" + Convert.ToHexString(SHA256.HashData(stream));
            }
            identities.Add(identity);
        }
    }
}

public sealed class OverlayRasterLease(string root, RenderPlan plan) : IDisposable
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    public RenderPlan Plan { get; internal set; } = plan;
    internal void Own(string path) => _paths.Add(path);
    public void Dispose()
    {
        foreach (var path in _paths)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _paths.Clear();
        try { Directory.Delete(root, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
