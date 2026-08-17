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

        var tiles = ThumbnailRenderer.BuildTiles(clip, 120, clipBounds, visible);

        Assert.InRange(tiles.Count, 14, 16);
        Assert.All(tiles, tile => Assert.True(tile.Bounds.Right >= visible.Left && tile.Bounds.Left < visible.Right));
        Assert.All(tiles, tile => Assert.InRange(tile.SourceTimeSeconds, 29, 50));
    }

    [Fact]
    public void Tile_times_get_more_precise_when_timeline_is_zoomed()
    {
        var clip = new TimelineClip { Start = 0, SourceStart = 0, Duration = 60 };

        var minute = ThumbnailRenderer.BuildTiles(clip, 60, new Rect(0, 0, 820, 48), new Rect(0, 0, 820, 48));
        var tenSeconds = ThumbnailRenderer.BuildTiles(clip, 60, new Rect(0, 0, 4920, 48), new Rect(0, 0, 820, 48));

        Assert.InRange(minute.Count, 9, 11);
        Assert.InRange(tenSeconds.Count, 9, 11);
        Assert.True(tenSeconds[^1].SourceTimeSeconds - tenSeconds[0].SourceTimeSeconds
            < minute[^1].SourceTimeSeconds - minute[0].SourceTimeSeconds);
    }
}
