using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KadrStudio.Adapters;
using KadrStudio.Controls;
using KadrStudio.Infrastructure.Caching;
using KadrStudio.Models;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TimelineRenderSnapshotTests
{
    [Fact]
    public void Timeline_renders_video_stereo_waveform_and_scrolls_every_layer_together()
        => RunSta(() =>
        {
            var assetId = Guid.NewGuid();
            var waveform = new WaveformPyramidBuilder(48_000, 32);
            waveform.AddInterleavedStereo(Enumerable.Range(0, 32_000)
                .SelectMany(index => new[]
                {
                    MathF.Sin(index / 17f) * (index % 257) / 257f,
                    MathF.Cos(index / 29f) * (index % 113) / 113f
                }).ToArray());
            var project = new ProjectViewMapper().ToUi(KadrStudio.Core.Domain.ProjectState.CreateNew());
            project.Media.Add(new MediaAsset
            {
                Id = assetId, Name = "snapshot.mp4", Path = "snapshot.mp4", Kind = MediaKind.Video,
                Duration = 60, Width = 1920, Height = 1080, HasAudio = true, Waveform = waveform.Build()
            });
            project.Clips.Add(new TimelineClip
            {
                AssetId = assetId, Track = TrackKind.Visual, TrackIndex = 0, Duration = 60
            });
            project.Clips.Add(new TimelineClip
            {
                AssetId = assetId, Track = TrackKind.Audio, TrackIndex = 0, Duration = 60
            });

            var control = new TimelineControl
            {
                Project = project, PixelsPerSecond = 20, HorizontalViewportWidth = 1000,
                HorizontalViewportOffset = 0, PlayheadSeconds = 12
            };
            var first = Render(control, 1300, 320);
            control.HorizontalViewportOffset = 240;
            control.InvalidateVisual();
            var scrolled = Render(control, 1300, 320);

            Assert.NotEqual(Hash(first), Hash(scrolled));
            Assert.True(CountPixels(first, (r, g, b) => b > 120 && b > r * 1.25) > 1_000,
                "Video track did not render its blue body.");
            Assert.True(CountPixels(first, (r, g, b) => g > 120 && g > r * 1.25) > 500,
                "Stereo waveform/audio track did not render visible green peaks.");
        });

    private static byte[] Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = GC.AllocateUninitializedArray<byte>(width * height * 4);
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static string Hash(byte[] pixels) => Convert.ToHexString(SHA256.HashData(pixels));

    private static int CountPixels(byte[] pixels, Func<byte, byte, byte, bool> predicate)
    {
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
            if (predicate(pixels[index + 2], pixels[index + 1], pixels[index])) count++;
        return count;
    }

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
