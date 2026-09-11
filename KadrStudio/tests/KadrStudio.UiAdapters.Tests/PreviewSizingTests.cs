using KadrStudio.Core.Domain;
using KadrStudio.Playback;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class PreviewSizingTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(1080, 1920, 608, 1080)]
    public void Automatic_quality_preserves_native_1080_and_bounds_large_canvas(int width, int height, int expectedWidth, int expectedHeight)
    {
        var project = ProjectState.CreateNew() with { CanvasWidth = width, CanvasHeight = height };
        Assert.Equal((expectedWidth, expectedHeight), PreviewSizing.Resolve(project, true));
        Assert.Equal((width, height), PreviewSizing.Resolve(project, false));
    }
}

