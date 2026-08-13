using System.Windows;
using KadrStudio.Controls;
using KadrStudio.Models;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class ThumbnailRendererTests
{
    [Fact]
    public void Tile_plan_contains_only_visible_clip_section()
    {
        var clip = new TimelineClip { Start = 0, SourceStart = 10, Duration = 100 };
        var clipBounds = new Rect(96, 40, 8000, 48);
        var visible = new Rect(1696, 40, 1200, 48);

        var tiles = ThumbnailRenderer.BuildTiles(500, clip, 120, clipBounds, visible);

        Assert.InRange(tiles.Count, 14, 16);
        Assert.All(tiles, tile => Assert.True(tile.Bounds.Right >= visible.Left && tile.Bounds.Left < visible.Right));
        Assert.All(tiles, tile => Assert.InRange(tile.FrameIndex, 0, 499));
    }
}
