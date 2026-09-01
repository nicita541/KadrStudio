using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KadrStudio.Core.Domain;
using KadrStudio.Models;

namespace KadrStudio.Controls;

/// <summary>
/// Renders and requests only thumbnail tiles intersecting the current viewport.
/// Extraction is delegated to the workspace boundary and never blocks WPF.
/// </summary>
public sealed class ThumbnailRenderer(Dispatcher dispatcher, Action invalidate, int cacheLimit = 256) : IDisposable
{
    private readonly ConcurrentDictionary<string, ImageSource> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _imageLru = new();
    private readonly ConcurrentDictionary<ThumbnailRequestKey, string> _resolved = new();
    private readonly ConcurrentDictionary<ThumbnailRequestKey, byte> _pendingRequests = new();
    private readonly ConcurrentDictionary<ThumbnailRequestKey, DateTimeOffset> _failedUntil = new();
    private readonly ConcurrentQueue<ThumbnailRequestKey> _requestLru = new();
    private CancellationTokenSource _viewportCancellation = new();

    public Func<Guid, TimelineTime, CancellationToken, Task<string?>>? Request { get; set; }

    public void BeginViewportGeneration()
    {
        var previous = Interlocked.Exchange(ref _viewportCancellation, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
    }

    public void Draw(DrawingContext context, TimelineClip clip, MediaAsset asset, Rect clipRectangle, Rect visibleRectangle)
    {
        if (visibleRectangle.IsEmpty || Request is null) return;
        context.PushClip(new RectangleGeometry(visibleRectangle));
        context.PushOpacity(0.88);
        foreach (var tile in BuildTiles(clip, asset.Duration, clipRectangle, visibleRectangle))
        {
            var time = TimelineTime.FromSeconds(tile.SourceTimeSeconds);
            var key = new ThumbnailRequestKey(asset.Id, time.Ticks);
            if (_resolved.TryGetValue(key, out var path))
            {
                _requestLru.Enqueue(key);
                if (TryGetImage(path) is { } image) context.DrawImage(image, tile.Bounds);
            }
            else
            {
                QueueRequest(key, time);
            }
        }
        context.Pop();
        context.Pop();
    }

    public static IReadOnlyList<ThumbnailTile> BuildTiles(
        TimelineClip clip,
        double sourceDuration,
        Rect clipRectangle,
        Rect visibleRectangle,
        double tileWidth = 82)
    {
        if (clipRectangle.IsEmpty || visibleRectangle.IsEmpty || tileWidth <= 0) return [];
        var result = new List<ThumbnailTile>();
        var first = clipRectangle.Left + Math.Floor((visibleRectangle.Left - clipRectangle.Left) / tileWidth) * tileWidth;
        for (var left = first; left < visibleRectangle.Right; left += tileWidth)
        {
            var boundedLeft = Math.Max(left, clipRectangle.Left);
            var right = Math.Min(left + tileWidth, clipRectangle.Right);
            if (right <= boundedLeft) continue;
            var center = Math.Clamp(left + tileWidth / 2, clipRectangle.Left, clipRectangle.Right);
            var localRatio = Math.Clamp((center - clipRectangle.Left) / clipRectangle.Width, 0, 1);
            var sourceTime = Math.Clamp(clip.SourceStart + localRatio * clip.Duration, 0, Math.Max(0, sourceDuration));
            result.Add(new ThumbnailTile(
                new Rect(boundedLeft, clipRectangle.Top, right - boundedLeft, clipRectangle.Height),
                sourceTime));
        }
        return result;
    }

    private void QueueRequest(ThumbnailRequestKey key, TimelineTime time)
    {
        if (Request is null || _pendingRequests.ContainsKey(key)) return;
        if (_failedUntil.TryGetValue(key, out var retryAt) && retryAt > DateTimeOffset.UtcNow) return;
        if (!_pendingRequests.TryAdd(key, 0)) return;
        var token = _viewportCancellation.Token;
        _ = ResolveAsync(key, time, token);
    }

    private async Task ResolveAsync(ThumbnailRequestKey key, TimelineTime time, CancellationToken cancellationToken)
    {
        try
        {
            var request = Request;
            if (request is null) return;
            var path = await request(key.SourceId, time, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(path))
            {
                _failedUntil[key] = DateTimeOffset.UtcNow.AddSeconds(5);
                return;
            }
            _resolved[key] = path;
            _requestLru.Enqueue(key);
            TrimRequests();
            _ = dispatcher.BeginInvoke(invalidate, DispatcherPriority.Render);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch
        {
            _failedUntil[key] = DateTimeOffset.UtcNow.AddSeconds(5);
        }
        finally
        {
            _pendingRequests.TryRemove(key, out _);
        }
    }

    private ImageSource? TryGetImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_imageCache.TryGetValue(path, out var cached))
        {
            _imageLru.Enqueue(path);
            return cached;
        }
        if (_pendingImages.TryAdd(path, 0)) _ = LoadImageAsync(path);
        return null;
    }

    private async Task LoadImageAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            await using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _imageCache[path] = image;
            _imageLru.Enqueue(path);
            TrimImages();
            _ = dispatcher.BeginInvoke(invalidate, DispatcherPriority.Render);
        }
        catch { }
        finally { _pendingImages.TryRemove(path, out _); }
    }

    private void TrimImages()
    {
        while (_imageCache.Count > cacheLimit && _imageLru.TryDequeue(out var oldest))
            _imageCache.TryRemove(oldest, out _);
    }

    private void TrimRequests()
    {
        var limit = Math.Max(64, cacheLimit * 2);
        while (_resolved.Count > limit && _requestLru.TryDequeue(out var oldest))
        {
            if (_resolved.TryRemove(oldest, out var path)) _imageCache.TryRemove(path, out _);
            _failedUntil.TryRemove(oldest, out _);
        }
    }

    public void Dispose()
    {
        var cancellation = Interlocked.Exchange(ref _viewportCancellation, new CancellationTokenSource());
        cancellation.Cancel();
        cancellation.Dispose();
        _viewportCancellation.Dispose();
    }
}

public readonly record struct ThumbnailTile(Rect Bounds, double SourceTimeSeconds);
internal readonly record struct ThumbnailRequestKey(Guid SourceId, long SourceTicks);
