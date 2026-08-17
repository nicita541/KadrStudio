using KadrStudio.Controls;
using KadrStudio.Models;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class TimelineReadModelTests
{
    [Fact]
    public void Snapshot_does_not_retain_mutable_project_clip_text_or_media_collections()
    {
        var project = EditorProject.CreateNew();
        var sourceId = Guid.NewGuid();
        var clip = new TimelineClip
        {
            AssetId = sourceId,
            Track = TrackKind.Visual,
            Start = 2,
            Duration = 5
        };
        var text = new TextOverlay { Start = 3, Duration = 2, Text = "before" };
        var asset = new MediaAsset
        {
            Id = sourceId,
            Name = "source.mp4",
            Kind = MediaKind.Video,
            TimelineFramePaths = ["frame-a.jpg"]
        };
        project.Clips.Add(clip);
        project.TextOverlays.Add(text);
        project.Media.Add(asset);

        var snapshot = TimelineReadModel.From(project);
        clip.Start = 20;
        text.Text = "after";
        asset.TimelineFramePaths = ["frame-b.jpg"];
        project.Clips.Clear();

        Assert.Single(snapshot.Clips);
        Assert.Equal(2, snapshot.Clips[0].Start);
        Assert.Equal("before", snapshot.TextOverlays[0].Text);
        Assert.Equal("frame-a.jpg", Assert.Single(snapshot.Media[0].TimelineFramePaths));
    }
}
