using KadrStudio.Application.Media;
using KadrStudio.Models;
using KadrStudio.ViewModels;
using Domain = KadrStudio.Core.Domain;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class AudioStreamSelectionTests
{
    [Theory]
    [InlineData(3, 1)]
    [InlineData(null, 2)]
    [InlineData(99, 0)]
    public async Task Timeline_import_uses_explicit_stream_and_undo_redo_preserves_choice(int? selected, int audioCount)
    {
        var path = Path.Combine(Path.GetTempPath(), "kadr-audio-choice-" + Guid.NewGuid().ToString("N") + ".mp4");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            await using var model = new MainViewModel();
            var asset = new MediaAsset
            {
                Path = path, Name = "multi-audio", Kind = MediaKind.Video, Duration = 2,
                Width = 320, Height = 180, FrameRate = 30, HasAudio = true,
                ProbeResult = new MediaProbeResult(path, Domain.MediaKind.Video, Domain.TimelineTime.FromSeconds(2),
                    [new(0, Domain.MediaStreamKind.Video, "h264"),
                     new(1, Domain.MediaStreamKind.Audio, "aac", SampleRate: 48000, Channels: 2, Language: "rus", IsDefault: true),
                     new(3, Domain.MediaStreamKind.Audio, "aac", SampleRate: 48000, Channels: 2, Language: "eng")],
                    new MediaFingerprint(1, File.GetLastWriteTimeUtc(path).Ticks, "fixture"), 320, 180, new Domain.FrameRate(30))
            };
            Assert.True(model.RegisterImportedMedia(asset));
            model.AddAssetToTimeline(asset.Id, selectedAudioStreamIndex: selected);
            var audio = model.CoreState.MediaClips.Where(clip => model.CoreState.FindTrack(clip.TrackId)?.Kind == Domain.TrackKind.Audio).ToArray();
            Assert.Equal(audioCount, audio.Length);
            if (audioCount == 0) { Assert.Empty(model.CoreState.MediaClips); return; }
            if (selected.HasValue) Assert.Equal(selected, Assert.Single(audio).StreamIndex);
            Assert.Single(model.CoreState.MediaClips.Select(clip => clip.LinkGroupId).Distinct());
            var expected = model.CoreState.MediaClips;
            model.Undo();
            Assert.Empty(model.CoreState.MediaClips);
            model.Redo();
            Assert.Equal(expected, model.CoreState.MediaClips);
            var projectPath = path + ".kadr";
            try
            {
                var store = new KadrStudio.Infrastructure.Storage.SqliteProjectStore();
                await store.SaveAsync(projectPath, model.CoreState);
                var reopened = await store.LoadAsync(projectPath);
                Assert.Equal(expected.ToArray(), reopened.MediaClips.ToArray());
                Assert.Equal(audio.Select(clip => clip.StreamIndex), reopened.MediaClips
                    .Where(clip => reopened.FindTrack(clip.TrackId)?.Kind == Domain.TrackKind.Audio)
                    .Select(clip => clip.StreamIndex));
            }
            finally { File.Delete(projectPath); }
        }
        finally { File.Delete(path); }
    }
}
