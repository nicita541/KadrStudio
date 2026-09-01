using System.Buffers.Binary;
using System.Security.Cryptography;
using KadrStudio.Application.Caching;

namespace KadrStudio.Infrastructure.Caching;

public sealed class DiskMediaArtifactCache : IArtifactStore
{
    private static ReadOnlySpan<byte> Magic => "KADRCACH"u8;
    private const int HeaderSize = 8 + sizeof(int) + sizeof(int) + 32;
    private string _root;
    private ArtifactStoreOptions _options;
    private readonly long _memoryLimitBytes;
    private readonly object _memoryGate = new();
    private readonly Dictionary<MediaCacheKey, MemoryEntry> _memory = [];
    private readonly LinkedList<MediaCacheKey> _lru = [];
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private long _memoryBytes;
    private bool _disposed;

    public DiskMediaArtifactCache(string root, long memoryLimitBytes = 128L * 1024 * 1024)
        : this(new ArtifactStoreOptions(root, MemoryBudgetBytes: memoryLimitBytes))
    {
    }

    public DiskMediaArtifactCache(ArtifactStoreOptions options)
    {
        _options = options.Normalize();
        _root = _options.Root;
        _memoryLimitBytes = _options.MemoryBudgetBytes;
        Directory.CreateDirectory(_root);
    }

    public ArtifactStoreOptions Options => _options;

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
        await TrimAsync(_options.DiskBudgetBytes, cancellationToken).ConfigureAwait(false);
    }

    public string GetPayloadPath(MediaCacheKey key, string extension)
    {
        ThrowIfDisposed();
        ValidateKey(key);
        var normalizedExtension = NormalizeExtension(extension);
        return ResolveInsideRoot(Path.Combine(
            _root, key.SourceId.ToString("N"), key.Kind.ToString(), $"v{key.FormatVersion}",
            $"l{key.Level}", $"{key.Segment:D12}-{key.StableHash}{normalizedExtension}"));
    }

    public async Task<string?> TryGetPayloadPathAsync(
        MediaCacheKey key,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var path = GetPayloadPath(key, extension);
        var checksumPath = path + ".sha256";
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path) || !File.Exists(checksumPath)) return null;
            var expected = (await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false)).Trim();
            string actual;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                actual = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                TryDelete(checksumPath);
                return null;
            }
            TryTouch(path);
            TryTouch(checksumPath);
            return path;
        }
        finally { _diskGate.Release(); }
    }

    public async Task<string> PutFileAsync(
        MediaCacheKey key,
        string sourcePath,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var destination = GetPayloadPath(key, extension);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            string checksum;
            await using (var verify = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                checksum = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false));
            File.Move(temporary, destination, overwrite: true);
            await File.WriteAllTextAsync(destination + ".sha256", checksum, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
            _diskGate.Release();
        }
        await TrimAsync(_options.DiskBudgetBytes, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    public async Task MoveAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(newRoot);
        if (destination.Equals(_root, StringComparison.OrdinalIgnoreCase)) return;
        if (IsInside(destination, _root) || IsInside(_root, destination))
            throw new IOException("Artifact cache cannot be moved into itself or one of its parent directories.");
        Directory.CreateDirectory(destination);
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = _root;
            foreach (var source in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(original, source);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                if (new FileInfo(source).Length != new FileInfo(target).Length)
                    throw new IOException($"Artifact cache copy verification failed for {relative}.");
            }
            Directory.Delete(original, recursive: true);
            _root = destination;
            _options = _options with { Root = destination };
        }
        finally { _diskGate.Release(); }
    }

    public async Task SetDiskBudgetAsync(long diskBudgetBytes, CancellationToken cancellationToken = default)
    {
        if (diskBudgetBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(diskBudgetBytes));
        _options = _options with { DiskBudgetBytes = diskBudgetBytes };
        await TrimAsync(diskBudgetBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            Directory.CreateDirectory(_root);
            lock (_memoryGate)
            {
                _memory.Clear();
                _lru.Clear();
                _memoryBytes = 0;
            }
        }
        finally { _diskGate.Release(); }
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
                TryDelete(file.FullName + ".sha256");
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

    private static bool IsInside(string candidate, string parent)
    {
        var fullCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullCandidate.StartsWith(
            fullParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<FileInfo> EnumerateArtifacts()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path));
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

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) throw new ArgumentException("A payload extension is required.", nameof(extension));
        var value = extension.StartsWith('.') ? extension : "." + extension;
        if (value.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
            throw new ArgumentException("The payload extension is invalid.", nameof(extension));
        return value.ToLowerInvariant();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static void TryTouch(string path) { try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { } }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed record MemoryEntry(byte[] Payload, LinkedListNode<MediaCacheKey> Node);
}
