using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;
using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TextOverlayExportTests
{
    [Theory]
    [InlineData(640, 360, 0, 1)]
    [InlineData(360, 640, 30, 1)]
    [InlineData(1280, 720, -25, 1)]
    [InlineData(1920, 1080, 15, 2)]
    public async Task Export_uses_shared_raster_geometry_and_releases_artifacts(int width, int height, double rotation, int outputScale)
    {
        var directory = Path.Combine(KadrLocalDataPaths.TempRoot, "text-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var project = ProjectState.CreateNew() with { CanvasWidth = width, CanvasHeight = height };
            var style = new TextStyle(FontSize: 38, X: .57, Y: .46, Rotation: rotation, BoxWidth: .45, BoxHeight: .45);
            project = project with
            {
                TextClips = [new TextClip(Guid.NewGuid(), project.Tracks.First(track => track.Kind == TrackKind.Text).Id,
                    TimelineTime.Zero, TimelineTime.FromSeconds(1), "Перенос строк\nShared text 123", style)]
            };
            await using var coordinator = new TimelineRenderCoordinator(new FfmpegLocator());
            var plan = coordinator.CreatePlan(project);
            width *= outputScale;
            height *= outputScale;
            string rasterPath;
            using (var raster = await TextOverlayRasterizer.PrepareAsync(plan, width, height, default))
            {
                var layer = Assert.Single(raster.Plan.TextLayers);
                rasterPath = layer.RasterPath!;
                Assert.True(File.Exists(rasterPath));
                Assert.False(string.IsNullOrWhiteSpace(layer.RasterIdentity));
                Assert.NotEmpty(layer.RasterFontIdentities);
                var output = Path.Combine(directory, "frame.png");
                var options = new RenderOutputOptions(RenderPurpose.StillFrame, output, width, height, IncludeAudio: false);
                var command = coordinator.CreateCommand(raster.Plan, options);
                Assert.DoesNotContain("drawtext", string.Join(' ', command.Arguments));
                Assert.Contains(rasterPath, command.Arguments);
                await coordinator.RenderAsync(plan, options);
                var expected = Pixels(rasterPath, width, height);
                var actual = Pixels(output, width, height);
                var expectedInk = 0;
                var sharedInk = 0;
                var unexpectedInk = 0;
                for (var pixel = 0; pixel < width * height; pixel++)
                {
                    var offset = pixel * 4;
                    // Straight-alpha PNG over black matches the FFmpeg background.
                    var reference = expected[offset + 2] * expected[offset + 3] / 255;
                    if (reference > 100)
                    {
                        expectedInk++;
                        if (actual[offset + 2] > 70) sharedInk++;
                    }
                    else if (reference < 20 && actual[offset + 2] > 100) unexpectedInk++;
                }
                Assert.True(expectedInk > 100);
                Assert.True(sharedInk >= expectedInk * .97, $"Aligned text pixels: {sharedInk}/{expectedInk}");
                Assert.True(unexpectedInk < expectedInk * .03, $"Unexpected text pixels: {unexpectedInk}/{expectedInk}");
            }
            Assert.False(File.Exists(rasterPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static byte[] Pixels(string path, int width, int height)
    {
        using var stream = File.OpenRead(path);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(width, image.PixelWidth);
        Assert.Equal(height, image.PixelHeight);
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[width * height * 4];
        converted.CopyPixels(bytes, width * 4, 0);
        return bytes;
    }
}
