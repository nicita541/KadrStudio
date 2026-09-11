using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using KadrStudio.Application.Caching;

namespace KadrStudio.Infrastructure.Caching;

public sealed class DiskMediaArtifactCache : IArtifactStore, IDisposable
{
    private static ReadOnlySpan<byte> Magic => "KADRCACH"u8;
    private const string CacheDirectoryName = "KadrStudioCache";
    private const string OwnershipMarkerName = ".kadr-cache-owner.json";
    private const int OwnershipSchemaVersion = 1;
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
    private string[] _protectedPaths = [];
    private static readonly object _pinGate = new();
    private static readonly Dictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MovingRoots = new(StringComparer.OrdinalIgnoreCase);

    public DiskMediaArtifactCache(string root, long memoryLimitBytes = 128L * 1024 * 1024)
        : this(new ArtifactStoreOptions(root, MemoryBudgetBytes: memoryLimitBytes))
    {
    }

    public DiskMediaArtifactCache(ArtifactStoreOptions options)
    {
        _options = options.Normalize();
        _root = Canonicalize(_options.Root);
        EnsureSafeDestructiveRoot(_root, null);
        _memoryLimitBytes = _options.MemoryBudgetBytes;
        var existed = Directory.Exists(_root);
        Directory.CreateDirectory(_root);
        if (!existed || !Directory.EnumerateFileSystemEntries(_root).Any())
            WriteOwnershipMarker(_root);
        else if (options.OwnershipId == Guid.Empty)
        {
            try
            {
                var marker = JsonSerializer.Deserialize<CacheOwnershipMarker>(
                    File.ReadAllText(Path.Combine(_root, OwnershipMarkerName)));
                if (marker is { SchemaVersion: OwnershipSchemaVersion, Product: "KadrStudio" } &&
                    marker.OwnerId != Guid.Empty &&
                    Canonicalize(marker.Root).Equals(_root, StringComparison.OrdinalIgnoreCase))
                    _options = _options with { OwnershipId = marker.OwnerId };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        _options = _options with { Root = _root };
    }

    public ArtifactStoreOptions Options => _options;
    public async Task<IDisposable> PinAsync(MediaCacheKey key, string extension, CancellationToken cancellationToken = default)
    {
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetPayloadPath(key, extension);
            ValidateOwnership(_root, null);
            lock (_pinGate)
            {
                if (MovingRoots.Any(root => IsInside(path, root)))
                    throw new IOException("Artifact cache is moving; retry after relocation completes.");
                _pins[path] = _pins.GetValueOrDefault(path) + 1;
            }
            return new ArtifactPin(() =>
            {
                lock (_pinGate)
                {
                    if (_pins[path] == 1) _pins.Remove(path);
                    else _pins[path]--;
                }
            });
        }
        finally { _diskGate.Release(); }
    }

    private bool IsPinned(string path)
    {
        lock (_pinGate) return _pins.ContainsKey(path);
    }

    private sealed class ArtifactPin(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    public bool IsOwned => TryValidateOwnership(_root, null);

    public void SetProtectedPaths(IReadOnlyCollection<string> paths)
        => Volatile.Write(ref _protectedPaths, paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Canonicalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

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
            if (!TryValidateOwnership(_root, null) || !File.Exists(path)) return null;
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
        var temporary = path + $".{Guid.NewGuid():N}.tmp";

        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwnership(_root, null);
            Directory.CreateDirectory(directory);
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
            if (!TryValidateOwnership(_root, null) || !File.Exists(path) || !File.Exists(checksumPath)) return null;
            var expected = (await File.ReadAllTextAsync(checksumPath, cancellationToken).ConfigureAwait(false)).Trim();
            string actual;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                actual = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsPinned(path))
                {
                    TryDelete(path);
                    TryDelete(checksumPath);
                }
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
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwnership(_root, null);
            Directory.CreateDirectory(directory);
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

    public async Task MoveAsync(
        string selectedParent,
        IReadOnlyCollection<string>? protectedPaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selectedParent))
            throw new ArgumentException("A cache parent folder is required.", nameof(selectedParent));
        var parent = Canonicalize(selectedParent);
        var destination = Canonicalize(Path.Combine(parent, CacheDirectoryName));
        if (destination.Equals(_root, StringComparison.OrdinalIgnoreCase)) return;
        EnsureSafeDestructiveRoot(destination, protectedPaths);
        if (IsInside(destination, _root) || IsInside(_root, destination))
            throw new IOException("Artifact cache cannot be moved into itself or one of its parent directories.");
        string? movingRoot = null;
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = _root;
            lock (_pinGate)
            {
                if (_pins.Keys.Any(path => IsInside(path, _root)))
                    throw new IOException("Artifact cache is in use; release active artifacts before moving it.");
                if (!MovingRoots.Add(original)) throw new IOException("Artifact cache is already moving.");
                movingRoot = original;
            }
            var sourceIsOwned = TryValidateOwnership(original, protectedPaths);
            Directory.CreateDirectory(parent);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("The KadrStudioCache target is not empty; existing files will not be overwritten.");
            var staging = destination + ".moving-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            WriteOwnershipMarker(staging, staging);
            var published = false;
            try
            {
                foreach (var source in sourceIsOwned
                             ? EnumerateArtifacts().SelectMany(file => File.Exists(file.FullName + ".sha256")
                                 ? new[] { file.FullName, file.FullName + ".sha256" } : new[] { file.FullName })
                             : [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(original, source);
                    if (relative.Equals(OwnershipMarkerName, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: false);
                    if (new FileInfo(source).Length != new FileInfo(target).Length)
                        throw new IOException($"Artifact cache copy verification failed for {relative}.");
                }
                WriteOwnershipMarker(staging, destination);
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: false);
                Directory.Move(staging, destination);
                published = true;
            }
            finally
            {
                if (!published && Directory.Exists(staging))
                {
                    WriteOwnershipMarker(staging, staging);
                    DeleteOwnedDirectory(staging, null);
                }
            }
            ValidateOwnership(destination, protectedPaths);
            if (sourceIsOwned)
            {
                ValidateOwnership(original, protectedPaths);
                DeleteArtifacts(EnumerateArtifacts().ToArray(), cancellationToken);
                RemoveEmptyDirectories(original);
                File.Delete(Path.Combine(original, OwnershipMarkerName));
                if (!Directory.EnumerateFileSystemEntries(original).Any()) Directory.Delete(original);
            }
            _root = destination;
            _options = _options with { Root = destination };
        }
        finally
        {
            if (movingRoot is not null)
                lock (_pinGate) MovingRoots.Remove(movingRoot);
            _diskGate.Release();
        }
    }

    public async Task SetDiskBudgetAsync(long diskBudgetBytes, CancellationToken cancellationToken = default)
    {
        if (diskBudgetBytes < 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(diskBudgetBytes));
        _options = _options with { DiskBudgetBytes = diskBudgetBytes };
        await TrimAsync(diskBudgetBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(
        IReadOnlyCollection<string>? protectedPaths = null,
        CancellationToken cancellationToken = default)
    {
        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwnership(_root, protectedPaths);
            DeleteArtifacts(EnumerateArtifacts().ToArray(), cancellationToken);
            RemoveEmptyDirectories(_root);
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

        await _diskGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateOwnership(_root, null);
            DeleteArtifacts(EnumerateArtifacts().Where(file => Path.GetRelativePath(_root, file.FullName)
                .StartsWith(sourceId.ToString("N") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray(), cancellationToken);
            RemoveEmptyDirectories(_root);
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
            ValidateOwnership(_root, null);
            var files = EnumerateArtifacts()
                .OrderBy(item => item.LastAccessTimeUtc)
                .ThenBy(item => item.FullName, StringComparer.Ordinal)
                .ToArray();
            var total = files.Sum(item => item.Length);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total <= targetDiskBytes) break;
                if (IsPinned(file.FullName)) continue;
                var length = file.Length;
                DeleteArtifacts([file], cancellationToken);
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
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_memoryGate)
        {
            _memory.Clear();
            _lru.Clear();
            _memoryBytes = 0;
        }
        _diskGate.Dispose();
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

    private void ValidateOwnership(string root, IReadOnlyCollection<string>? protectedPaths)
    {
        EnsureSafeDestructiveRoot(root, protectedPaths);
        EnsureSafeDestructiveRoot(root, Volatile.Read(ref _protectedPaths));
        EnsureNoReparsePoints(root);
        var markerPath = Path.Combine(root, OwnershipMarkerName);
        CacheOwnershipMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<CacheOwnershipMarker>(File.ReadAllText(markerPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new IOException("Artifact cache deletion was refused because its ownership marker is missing or invalid.", exception);
        }
        if (marker is null || marker.SchemaVersion != OwnershipSchemaVersion ||
            !string.Equals(marker.Product, "KadrStudio", StringComparison.Ordinal) ||
            marker.OwnerId != _options.OwnershipId ||
            !Canonicalize(marker.Root).Equals(Canonicalize(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Artifact cache deletion was refused because its ownership marker does not match this KadrStudio cache.");
    }

    private bool TryValidateOwnership(string root, IReadOnlyCollection<string>? protectedPaths)
    {
        try
        {
            ValidateOwnership(root, protectedPaths);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void WriteOwnershipMarker(string physicalRoot, string? ownedRoot = null)
    {
        var marker = new CacheOwnershipMarker(
            OwnershipSchemaVersion,
            "KadrStudio",
            _options.OwnershipId,
            Canonicalize(ownedRoot ?? physicalRoot));
        var path = Path.Combine(physicalRoot, OwnershipMarkerName);
        File.WriteAllText(path, JsonSerializer.Serialize(marker));
    }

    private void DeleteOwnedDirectory(string root, IReadOnlyCollection<string>? protectedPaths)
    {
        ValidateOwnership(root, protectedPaths);
        Directory.Delete(root, recursive: true);
    }

    private static void EnsureSafeDestructiveRoot(string root, IReadOnlyCollection<string>? protectedPaths)
    {
        var canonical = Canonicalize(root);
        for (var directory = new DirectoryInfo(canonical); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An artifact cache cannot traverse a symbolic link or junction.");
        var driveRoot = Path.GetPathRoot(canonical);
        if (!string.IsNullOrWhiteSpace(driveRoot) &&
            canonical.Equals(Canonicalize(driveRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A drive root cannot be used as the KadrStudio artifact cache.");
        foreach (var protectedRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                 })
        {
            if (!string.IsNullOrWhiteSpace(protectedRoot) &&
                canonical.Equals(Canonicalize(protectedRoot), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Desktop and Documents cannot be used as the KadrStudio artifact cache.");
        }
        foreach (var path in protectedPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var protectedPath = Canonicalize(path);
            if (protectedPath.Equals(canonical, StringComparison.OrdinalIgnoreCase) ||
                IsInside(protectedPath, canonical))
                throw new IOException("Artifact cache deletion was refused because the cache contains a project or source path.");
        }
    }

    private static string Canonicalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        return !string.IsNullOrWhiteSpace(pathRoot) &&
               fullPath.Equals(pathRoot, StringComparison.OrdinalIgnoreCase)
            ? pathRoot
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private IEnumerable<FileInfo> EnumerateArtifacts()
    {
        if (!Directory.Exists(_root)) return [];
        EnsureNoReparsePoints(_root);
        return Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(IsManagedArtifact).Select(path => new FileInfo(path));
    }

    private bool IsManagedArtifact(string path)
    {
        var parts = Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 5 || !Guid.TryParseExact(parts[0], "N", out _) ||
            !Enum.TryParse<MediaArtifactKind>(parts[1], out var kind) || !Enum.IsDefined(kind) ||
            !parts[2].StartsWith('v') || !int.TryParse(parts[2].AsSpan(1), out var version) || version < 1 ||
            !parts[3].StartsWith('l') || !int.TryParse(parts[3].AsSpan(1), out var level) || level < 0)
            return false;
        var name = parts[4];
        var separator = name.IndexOf('-');
        if (separator < 12 || !long.TryParse(name.AsSpan(0, separator), out var segment) || segment < 0 ||
            name.Length < separator + 66 || name[separator + 65] != '.' ||
            name.AsSpan(separator + 1, 64).ContainsAnyExcept("0123456789abcdefABCDEF")) return false;
        if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (File.Exists(path + ".sha256")) return true;
        if (!name.EndsWith(".cache", StringComparison.OrdinalIgnoreCase)) return false;
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        return stream.Read(header) == header.Length && header.SequenceEqual(Magic);
    }

    private void DeleteArtifacts(IEnumerable<FileInfo> files, CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_pinGate)
            {
                if (_pins.ContainsKey(file.FullName)) continue;
                File.Delete(file.FullName);
                File.Delete(file.FullName + ".sha256");
            }
        }
        lock (_memoryGate)
        {
            _memory.Clear();
            _lru.Clear();
            _memoryBytes = 0;
        }
    }

    private static void EnsureNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Artifact operation refused: cache contains a symbolic link or junction.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
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
    private sealed record CacheOwnershipMarker(int SchemaVersion, string Product, Guid OwnerId, string Root);
}
