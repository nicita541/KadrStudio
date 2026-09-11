using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using KadrStudio.Services;
using KadrStudio.Services.Editorial;

namespace KadrStudio.UiAdapters.Tests;

public sealed class ArtifactDownloadTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("truncated")]
    [InlineData("checksum")]
    [InlineData("cancel")]
    public async Task Download_publishes_only_closed_complete_verified_payload(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "artifact-download", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "result.mp4");
        await File.WriteAllTextAsync(path, "old destination");
        try
        {
            byte[] payload = [1, 2, 3, 4, 5];
            var id = Convert.ToHexStringLower(SHA256.HashData(scenario == "checksum" ? new byte[] { 7 } : payload));
            using var connection = new AiServerConnection(new AiServerClientOptions(new Uri("http://localhost/")),
                new Handler(payload, scenario == "truncated" ? 10 : payload.Length));
            var client = new AiServerV2Client(connection);
            using var cancellation = new CancellationTokenSource();
            var download = client.DownloadArtifactAsync(id, path,
                scenario == "cancel" ? new CancelProgress(cancellation) : null, cancellation.Token);
            if (scenario == "valid")
            {
                await download;
                Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            }
            else
            {
                if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
                else await Assert.ThrowsAsync<InvalidDataException>(() => download);
                Assert.Equal("old destination", await File.ReadAllTextAsync(path));
            }
            Assert.Equal([path], Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<double>
    {
        public void Report(double value) => source.Cancel();
    }

    private sealed class Handler(byte[] payload, long length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
