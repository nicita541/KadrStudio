using System.Windows;
using KadrStudio.Controls;
using KadrStudio.Models;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TimelineGeometryTests
{
    [Fact]
    public void Viewport_is_single_inverse_mapping_for_time_and_content_coordinates()
    {
        var viewport = new TimelineViewport(80, 720, 1280, 96);
        var x = viewport.TimeToContentX(25.25);

        Assert.Equal(25.25, viewport.ContentXToTime(x), 6);
        Assert.Equal(viewport.ContentXToTime(viewport.VisibleContentLeft), viewport.VisibleTimelineStart, 6);
        Assert.Equal(viewport.ContentXToTime(viewport.VisibleContentRight), viewport.VisibleTimelineEnd, 6);
        Assert.True(viewport.ClipToVisible(new Rect(x - 500, 10, 1000, 50)).Width <= 1000);
    }

    [Theory]
    [InlineData(1600, 1, 800)]
    [InlineData(1600, 1.5, 1200)]
    [InlineData(200, 2, 200)]
    public void Waveform_column_density_tracks_physical_pixels(double width, double dpi, int expected)
    {
        var viewport = new TimelineViewport(1, 0, width, 0);
        Assert.Equal(expected, viewport.ColumnCount(width, dpi));
    }

    [Fact]
    public void Track_layout_orders_video_above_audio_and_expands_away_from_center()
    {
        var layout = new TrackLayout(3, 3, false);

        Assert.True(layout.GetTrackTop(TrackKind.Visual, 2) < layout.GetTrackTop(TrackKind.Visual, 1));
        Assert.True(layout.GetTrackTop(TrackKind.Visual, 1) < layout.GetTrackTop(TrackKind.Visual, 0));
        Assert.True(layout.GetTrackTop(TrackKind.Visual, 0) < layout.GetTrackTop(TrackKind.Audio, 0));
        Assert.True(layout.GetTrackTop(TrackKind.Audio, 0) < layout.GetTrackTop(TrackKind.Audio, 1));
        Assert.Equal(new TrackAddress(TrackKind.Visual, 0),
            layout.GetTrackAt(layout.GetTrackTop(TrackKind.Visual, 0) + 10));
        Assert.Equal(new TrackAddress(TrackKind.Audio, 0),
            layout.GetTrackAt(layout.GetTrackTop(TrackKind.Audio, 0) + 10));
    }
}
