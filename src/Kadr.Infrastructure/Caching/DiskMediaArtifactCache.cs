using System.Buffers.Binary;
using System.Security.Cryptography;
using KadrStudio.Application.Caching;

namespace KadrStudio.Infrastructure.Caching;

public sealed class DiskMediaArtifactCache : IMediaArtifactCache
{
    private static ReadOnlySpan<byte> Magic => "KADRCACH"u8;
    private const int HeaderSize = 8 + sizeof(int) + sizeof(int) + 32;
    private readonly string _root;
    private readonly long _memoryLimitBytes;
    private readonly object _memoryGate = new();
    private readonly Dictionary<MediaCacheKey, MemoryEntry> _memory = [];
    private readonly LinkedList<MediaCacheKey> _lru = [];
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private long _memoryBytes;
    private bool _disposed;

    public DiskMediaArtifactCache(string root, long memoryLimitBytes = 128L * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A cache root is required.", nameof(root));
        if (memoryLimitBytes < 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes));
        _root = Path.GetFullPath(root);
        _memoryLimitBytes = memoryLimitBytes;
        Directory.CreateDirectory(_root);
    }

    public async ValueTask<ReadOnlyMemory<byte>?> TryGetAsync(
        MediaCacheKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        lock (_memoryGate)
        {
            if (_memory.TryGetValue(key, out var cached))
            {
                TouchUnsafe(cached);
                return cached.Payload;
            }
        }

        var path = GetArtifactPath(key);
        if (!File.Exists(path)) return null;
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            byte[] encoded;
            try
            {
                encoded = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            var payload = Decode(encoded, key.FormatVersion);
            if (payload is null)
            {
                TryDelete(path);
                return null;
            }
            TryTouch(path);
            AddMemory(key, payload);
            return payload;
        }
        finally
        {
            _diskGate.Release();
        }
    }

    public async ValueTask PutAsync(
        MediaCacheKey key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        if (payload.Length == 0) throw new ArgumentException("A cache artifact cannot be empty.", nameof(payload));
        var bytes = payload.ToArray();
        var encoded = Encode(bytes, key.FormatVersion);
        var path = GetArtifactPath(key);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";

        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            AddMemory(key, bytes);
        }
        finally
        {
            TryDelete(temporary);
            _diskGate.Release();
        }
    }

    public async Task InvalidateSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (sourceId == Guid.Empty) throw new ArgumentException("A source ID is required.", nameof(sourceId));
        lock (_memoryGate)
        {
            foreach (var key in _memory.Keys.Where(item => item.SourceId == sourceId).ToArray())
                RemoveMemoryUnsafe(key);
        }

        var sourceDirectory = ResolveInsideRoot(Path.Combine(_root, sourceId.ToString("N")));
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, recursive: true);
        }
        finally
        {
            _diskGate.Release();
        }
    }

    public async Task TrimAsync(long targetDiskBytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (targetDiskBytes < 0) throw new ArgumentOutOfRangeException(nameof(targetDiskBytes));
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = EnumerateArtifacts()
                .OrderBy(item => item.LastAccessTimeUtc)
                .ThenBy(item => item.FullName, StringComparer.Ordinal)
                .ToArray();
            var total = files.Sum(item => item.Length);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= targetDiskBytes) break;
                var length = file.Length;
                TryDelete(file.FullName);
                total -= length;
            }
        }
        finally
        {
            _diskGate.Release();
        }
    }

    public async Task<MediaCacheSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = EnumerateArtifacts().ToArray();
            lock (_memoryGate)
                return new MediaCacheSnapshot(_memoryBytes, files.Sum(item => item.Length), _memory.Count, files.Length);
        }
        finally
        {
            _diskGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        lock (_memoryGate)
        {
            _memory.Clear();
            _lru.Clear();
            _memoryBytes = 0;
        }
        _diskGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private string GetArtifactPath(MediaCacheKey key)
        => ResolveInsideRoot(Path.Combine(
            _root,
            key.SourceId.ToString("N"),
            key.Kind.ToString(),
            $"v{key.FormatVersion}",
            $"l{key.Level}",
            $"{key.Segment:D12}-{key.StableHash}.cache"));

    private string ResolveInsideRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cache path escaped the configured root.");
        return fullPath;
    }

    private IEnumerable<FileInfo> EnumerateArtifacts()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateFiles(_root, "*.cache", SearchOption.AllDirectories).Select(path => new FileInfo(path));
    }

    private void AddMemory(MediaCacheKey key, byte[] payload)
    {
        if (payload.LongLength > _memoryLimitBytes) return;
        lock (_memoryGate)
        {
            RemoveMemoryUnsafe(key);
            var node = _lru.AddFirst(key);
            _memory.Add(key, new MemoryEntry(payload, node));
            _memoryBytes += payload.LongLength;
            while (_memoryBytes > _memoryLimitBytes && _lru.Last is { } last)
                RemoveMemoryUnsafe(last.Value);
        }
    }

    private void TouchUnsafe(MemoryEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
    }

    private void RemoveMemoryUnsafe(MediaCacheKey key)
    {
        if (!_memory.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node);
        _memoryBytes -= entry.Payload.LongLength;
    }

    private static byte[] Encode(byte[] payload, int version)
    {
        var result = GC.AllocateUninitializedArray<byte>(checked(HeaderSize + payload.Length));
        Magic.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), version);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12, 4), payload.Length);
        SHA256.HashData(payload, result.AsSpan(16, 32));
        payload.CopyTo(result, HeaderSize);
        return result;
    }

    private static byte[]? Decode(byte[] encoded, int expectedVersion)
    {
        if (encoded.Length < HeaderSize || !encoded.AsSpan(0, 8).SequenceEqual(Magic)) return null;
        if (BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(8, 4)) != expectedVersion) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(12, 4));
        if (length <= 0 || encoded.Length != HeaderSize + length) return null;
        var payload = encoded.AsSpan(HeaderSize, length);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(payload, hash);
        return hash.SequenceEqual(encoded.AsSpan(16, 32)) ? payload.ToArray() : null;
    }

    private static void ValidateKey(MediaCacheKey key)
    {
        if (key.SourceId == Guid.Empty || string.IsNullOrWhiteSpace(key.SourceFingerprint) ||
            key.Level < 0 || key.Segment < 0 || key.FormatVersion < 1)
            throw new ArgumentException("The media cache key is invalid.", nameof(key));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void TryTouch(string path) { try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { } }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed record MemoryEntry(byte[] Payload, LinkedListNode<MediaCacheKey> Node);
}
