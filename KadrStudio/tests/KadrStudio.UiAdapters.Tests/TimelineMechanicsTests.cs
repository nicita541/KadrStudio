using KadrStudio.Controls;
using KadrStudio.Core.Domain;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TimelineMechanicsTests
{
    [Fact]
    public void Snap_threshold_is_pixel_based_and_exact_clip_edge_wins_a_tie()
    {
        var clipEdge = new TimelineSnapTarget(10, TimelineSnapTargetKind.ClipEdge);
        var playhead = new TimelineSnapTarget(10, TimelineSnapTargetKind.Playhead);

        var near = TimelineSnapEngine.SnapTime(
            9.93,
            24,
            100,
            snappingEnabled: true,
            [playhead, clipEdge]);
        var far = TimelineSnapEngine.SnapTime(
            9.90,
            24,
            100,
            snappingEnabled: true,
            [clipEdge]);

        Assert.True(near.IsSnapped);
        Assert.Equal(10, near.Value);
        Assert.Equal(TimelineSnapTargetKind.ClipEdge, near.Target?.Kind);
        Assert.False(far.IsSnapped);
    }

    [Fact]
    public void Linked_move_snaps_all_anchors_with_one_delta()
    {
        var result = TimelineSnapEngine.SnapDelta(
            proposedDelta: 2.94,
            frameAlignedDelta: 2.958333333,
            movingAnchors: [2, 12],
            targets: [new TimelineSnapTarget(15, TimelineSnapTargetKind.ClipEdge)],
            pixelsPerSecond: 100,
            snappingEnabled: true);

        Assert.True(result.IsSnapped);
        Assert.Equal(3, result.Value, precision: 8);
    }

    [Theory]
    [InlineData(24_000, 1_001)]
    [InlineData(30_000, 1_001)]
    public void Frame_navigation_has_no_accumulated_fractional_fps_error(
        int numerator,
        int denominator)
    {
        var rate = new FrameRate(numerator, denominator);
        var maximum = TimelineTime.FromSeconds(10_000);
        var current = TimelineTime.Zero;
        for (var index = 0; index < 10_000; index++)
        {
            current = TimelineFrameNavigator.Step(current, 1, rate, maximum);
        }

        Assert.Equal(TimelineTime.FromFrames(10_000, rate), current);
        for (var index = 0; index < 10_000; index++)
        {
            current = TimelineFrameNavigator.Step(current, -1, rate, maximum);
        }

        Assert.Equal(TimelineTime.Zero, current);
    }
}
