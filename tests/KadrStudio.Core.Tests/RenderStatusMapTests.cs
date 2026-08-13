using KadrStudio.Application.Editing;
using KadrStudio.Application.Rendering;
using KadrStudio.Core.Domain;

namespace KadrStudio.Core.Tests;

public sealed class RenderStatusMapTests
{
    [Fact]
    public void Audio_edit_invalidates_only_exact_audio_range()
    {
        var projectRange = new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(30));
        var changed = new TimeRange(TimelineTime.FromSeconds(10), TimelineTime.FromSeconds(5));
        var initial = RenderStatusMap.Create(projectRange, RenderRangeState.Ready);
        var changes = new ProjectChangeSet
        {
            Kind = ProjectChangeKind.Audio | ProjectChangeKind.Timeline,
            AudioRanges = [changed]
        };

        var result = initial.Apply(changes);

        Assert.Equal(RenderRangeState.Ready, result.Get(RenderPipeline.Video, TimelineTime.FromSeconds(12)));
        Assert.Equal(RenderRangeState.Ready, result.Get(RenderPipeline.Overlay, TimelineTime.FromSeconds(12)));
        Assert.Equal(RenderRangeState.Invalid, result.Get(RenderPipeline.Audio, TimelineTime.FromSeconds(12)));
        Assert.Equal(RenderRangeState.Ready, result.Get(RenderPipeline.Audio, TimelineTime.FromSeconds(9)));
        Assert.Equal(RenderRangeState.Ready, result.Get(RenderPipeline.Audio, TimelineTime.FromSeconds(16)));
        Assert.Collection(result.Audio,
            item => Assert.Equal(RenderRangeState.Ready, item.State),
            item => Assert.Equal(RenderRangeState.Invalid, item.State),
            item => Assert.Equal(RenderRangeState.Ready, item.State));
    }

    [Fact]
    public void Adjacent_ranges_with_same_state_are_coalesced()
    {
        var projectRange = new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(30));
        var map = RenderStatusMap.Create(projectRange)
            .Set(RenderPipeline.Video,
                new TimeRange(TimelineTime.Zero, TimelineTime.FromSeconds(10)), RenderRangeState.Ready)
            .Set(RenderPipeline.Video,
                new TimeRange(TimelineTime.FromSeconds(10), TimelineTime.FromSeconds(10)), RenderRangeState.Ready);

        Assert.Equal(2, map.Video.Length);
        Assert.Equal(TimelineTime.FromSeconds(20), map.Video[0].Range.Duration);
        Assert.Equal(RenderRangeState.Ready, map.Video[0].State);
    }
}
