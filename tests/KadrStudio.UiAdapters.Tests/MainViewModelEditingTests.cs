using KadrStudio.Models;
using KadrStudio.ViewModels;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class MainViewModelEditingTests
{
    [Fact]
    public async Task Core_commands_keep_linked_video_audio_and_undo_redo_consistent()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"kadr-linked-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(sourcePath, [0]);
        try
        {
        await using var viewModel = new MainViewModel();
        var asset = new MediaAsset
        {
            Path = sourcePath,
            Name = "linked.mp4", Kind = MediaKind.Video, Duration = 20, HasAudio = true,
            Width = 1920, Height = 1080, FrameRate = 30
        };
        Assert.True(viewModel.RegisterImportedMedia(asset));

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
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
