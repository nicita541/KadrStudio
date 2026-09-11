using System.Net;
using System.Net.Http;
using System.Text.Json;
using KadrStudio.Application.Automation.Editorial;
using KadrStudio.Core.Domain;
using KadrStudio.Services;
using KadrStudio.Services.Editorial;

namespace KadrStudio.UiAdapters.Tests;

public sealed class IndexerCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poll_failure_or_cancel_cancels_all_known_owned_jobs(bool cancel)
    {
        var root = Path.Combine(KadrLocalDataPaths.TempRoot, "indexer-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? bundlePath = null;
        try
        {
            var path = Path.Combine(root, "source.wav");
            var locator = new FfmpegLocator();
            var runner = new ProcessRunner();
            Assert.Equal(0, (await runner.RunAsync(locator.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "0.2", path])).ExitCode);
            var source = new MediaSource(Guid.NewGuid(), path, "source", MediaKind.Audio,
                TimelineTime.FromSeconds(.2), true, 0, 0, new FrameRate(30), "", "pcm_s16le", Streams: []);
            var project = ProjectState.CreateNew();
            project = (project with
            {
                Sources = project.Sources.Add(source.Id, source),
                MediaClips = [new MediaClip(Guid.NewGuid(), source.Id, project.Tracks.First(track => track.Kind == TrackKind.Audio).Id,
                    TimelineTime.Zero, TimelineTime.Zero, source.Duration)]
            }).EnsureSequenceContainer();
            var proxies = new AnalysisProxyBuilder(locator, runner);
            await using var bundle = await proxies.BuildAsync(source, default);
            bundlePath = bundle.Directory;
            using var cancellation = new CancellationTokenSource();
            var handler = new Handler(cancel ? cancellation : null);
            using var connection = new AiServerConnection(new AiServerClientOptions(new Uri("http://localhost/")), handler);
            var indexer = new AiServerMediaUnderstandingIndexer(new AiServerV2Client(connection), proxies);
            var operation = indexer.EnsureIndexesAsync(project, project.ActiveSequenceId!.Value,
                MontageProfileCatalog.Get(MontageProfileKind.Generic), null, cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            else await Assert.ThrowsAsync<AiServerV2Exception>(() => operation);
            Assert.NotEmpty(handler.Started);
            Assert.Equal(handler.Started.Order(), handler.Cancelled.Order());
            Assert.True(handler.CleanupHadLiveToken);
        }
        finally
        {
            if (bundlePath is not null) Directory.Delete(bundlePath, true);
            Directory.Delete(root, true);
        }
    }

    private sealed class Handler(CancellationTokenSource? cancellation) : HttpMessageHandler
    {
        public List<Guid> Started { get; } = [];
        public List<Guid> Cancelled { get; } = [];
        public bool CleanupHadLiveToken { get; private set; }
        private string? _owner;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.StartsWith("/v2/assets/", StringComparison.Ordinal))
            {
                var size = long.Parse(uri.Query.Split('=')[1]);
                return Json(new { receivedBytes = size, totalBytes = size, isComplete = true });
            }
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var owner = body.RootElement.GetProperty("ownerId").GetString();
                _owner ??= owner;
                Assert.Equal(_owner, owner);
                var id = Guid.NewGuid();
                Started.Add(id);
                return Json(new { id, state = "Queued", ownerId = owner, ownershipProtocolVersion = 1 });
            }
            if (request.Method == HttpMethod.Delete)
            {
                Assert.Equal("?ownerId=" + _owner, uri.Query);
                CleanupHadLiveToken = !token.IsCancellationRequested;
                Cancelled.Add(Guid.Parse(uri.Segments.Last()));
                return Json(new { });
            }
            cancellation?.Cancel();
            token.ThrowIfCancellationRequested();
            return new(HttpStatusCode.InternalServerError) { Content = new StringContent("poll failure") };
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
