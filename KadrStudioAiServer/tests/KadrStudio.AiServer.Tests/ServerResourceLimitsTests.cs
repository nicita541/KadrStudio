using System.Security.Cryptography;
using KadrStudio.AiServer.Storage;

namespace KadrStudio.AiServer.Tests;

public sealed class ServerResourceLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kadr-resource-limits", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Abandoned_partial_upload_is_removed_after_ttl()
    {
        var quota = new DataRootQuota(_root, 1024 * 1024);
        var store = new ContentAddressedAssetStore(_root, 1024 * 1024, quota);
        var hash = new string('a', 64);
        await store.AppendAsync(hash, 6, 0, "video/mp4", new MemoryStream([1, 2, 3]), CancellationToken.None);
        var upload = store.FindUpload(hash)!;
        File.SetLastWriteTimeUtc(upload.Path, DateTime.UtcNow.AddHours(-2));

        var removed = store.CleanupAbandonedUploads(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.Null(store.FindUpload(hash));
    }

    [Fact]
    public async Task Data_root_quota_rejects_upload_before_writing_bytes()
    {
        var quota = new DataRootQuota(_root, 8);
        var store = new ContentAddressedAssetStore(_root, 1024, quota);
        var content = new byte[16];
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var error = await Assert.ThrowsAsync<StorageQuotaException>(() => store.AppendAsync(
            hash, content.Length, 0, "application/octet-stream", new MemoryStream(content), CancellationToken.None));

        Assert.Equal("storage_quota_exceeded", error.ErrorCode);
        Assert.Equal(0, quota.GetStorageBytes());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
