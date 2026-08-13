using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KadrStudio.Models;

namespace KadrStudio.Controls;

public sealed class ThumbnailRenderer(Dispatcher dispatcher, Action invalidate, int cacheLimit = 256)
{
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _lru = new();

    public void Draw(DrawingContext context, TimelineClip clip, MediaAsset asset, Rect clipRectangle, Rect visibleRectangle)
    {
        if (asset.TimelineFramePaths.Count == 0 || visibleRectangle.IsEmpty) return;
        context.PushClip(new RectangleGeometry(visibleRectangle));
        context.PushOpacity(0.88);
        foreach (var tile in BuildTiles(asset.TimelineFramePaths.Count, clip, asset.Duration, clipRectangle, visibleRectangle))
        {
            var path = asset.TimelineFramePaths[tile.FrameIndex];
            if (TryGet(path) is { } image) context.DrawImage(image, tile.Bounds);
        }
        context.Pop();
        context.Pop();
    }

    public static IReadOnlyList<ThumbnailTile> BuildTiles(
        int frameCount,
        TimelineClip clip,
        double sourceDuration,
        Rect clipRectangle,
        Rect visibleRectangle,
        double tileWidth = 82)
    {
        if (frameCount <= 0 || clipRectangle.IsEmpty || visibleRectangle.IsEmpty) return [];
        var result = new List<ThumbnailTile>();
        var first = clipRectangle.Left + Math.Floor((visibleRectangle.Left - clipRectangle.Left) / tileWidth) * tileWidth;
        for (var left = first; left < visibleRectangle.Right; left += tileWidth)
        {
            var width = Math.Min(tileWidth, clipRectangle.Right - left);
            if (width <= 0) continue;
            var center = Math.Min(clipRectangle.Right, left + tileWidth / 2);
            var localRatio = Math.Clamp((center - clipRectangle.Left) / clipRectangle.Width, 0, 1);
            var sourceTime = clip.SourceStart + localRatio * clip.Duration;
            var sourceRatio = sourceDuration <= 0 ? 0 : Math.Clamp(sourceTime / sourceDuration, 0, 1);
            var index = Math.Clamp((int)Math.Round(sourceRatio * (frameCount - 1)), 0, frameCount - 1);
            result.Add(new ThumbnailTile(new Rect(left, clipRectangle.Top, width, clipRectangle.Height), index));
        }
        return result;
    }

    private ImageSource? TryGet(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_cache.TryGetValue(path, out var cached))
        {
            _lru.Enqueue(path);
            return cached;
        }
        if (_pending.TryAdd(path, 0)) _ = LoadAsync(path);
        return null;
    }

    private async Task LoadAsync(string path)
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
            _cache[path] = image;
            _lru.Enqueue(path);
            Trim();
            _ = dispatcher.BeginInvoke(invalidate, DispatcherPriority.Render);
        }
        catch { }
        finally { _pending.TryRemove(path, out _); }
    }

    private void Trim()
    {
        while (_cache.Count > cacheLimit && _lru.TryDequeue(out var oldest)) _cache.TryRemove(oldest, out _);
    }
}

public readonly record struct ThumbnailTile(Rect Bounds, int FrameIndex);
