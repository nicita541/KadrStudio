using KadrStudio.AiServer.Storage;

namespace KadrStudio.AiServer.Tests;

public sealed class ContentAddressedArtifactStoreTests
{
    [Fact]
    public async Task Binary_video_artifact_preserves_media_type_length_and_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "kadr-artifact-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.mp4");
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        try
        {
            var store = new ContentAddressedArtifactStore(root);
            var stored = await store.PutFileAsync(source, "video/mp4", ".mp4", CancellationToken.None);
            var found = store.Find(stored.Id);

            Assert.NotNull(found);
            Assert.Equal("video/mp4", found!.MediaType);
            Assert.Equal(bytes.LongLength, found.Length);
            Assert.Equal(".mp4", found.Extension);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(found.Path));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
