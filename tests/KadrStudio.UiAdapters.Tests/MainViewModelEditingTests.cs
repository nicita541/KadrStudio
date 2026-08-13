using KadrStudio.Models;
using KadrStudio.ViewModels;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class MainViewModelEditingTests
{
    [Fact]
    public async Task Core_commands_keep_linked_video_audio_and_undo_redo_consistent()
    {
        await using var viewModel = new MainViewModel();
        var asset = new MediaAsset
        {
            Path = Path.Combine(Path.GetTempPath(), "not-probed.mp4"),
            Name = "linked.mp4", Kind = MediaKind.Video, Duration = 20, HasAudio = true,
            Width = 1920, Height = 1080, FrameRate = 30
        };
        viewModel.Project.Media.Add(asset);
        viewModel.BeginEdit();
        viewModel.CommitEdit("Add test source");

        viewModel.AddAssetToTimeline(asset.Id);
        Assert.Equal(2, viewModel.Project.Clips.Count);
        Assert.Single(viewModel.Project.Clips.Select(clip => clip.LinkGroupId).Distinct());
        Assert.Contains(viewModel.Project.Clips, clip => clip.Track == TrackKind.Visual);
        Assert.Contains(viewModel.Project.Clips, clip => clip.Track == TrackKind.Audio);

        viewModel.Playhead = 8;
        Assert.True(viewModel.SplitSelectedAtPlayhead());
        Assert.Equal(4, viewModel.Project.Clips.Count);
        Assert.Equal(2, viewModel.Project.Clips.Count(clip => Math.Abs(clip.Start - 8) < 0.001));

        Assert.True(viewModel.UnlinkSelectedClip());
        Assert.Null(viewModel.SelectedClip?.LinkGroupId);
        viewModel.DeleteSelectedClip();
        Assert.Equal(3, viewModel.Project.Clips.Count);

        viewModel.Undo();
        Assert.Equal(4, viewModel.Project.Clips.Count);
        viewModel.Redo();
        Assert.Equal(3, viewModel.Project.Clips.Count);
    }
}
