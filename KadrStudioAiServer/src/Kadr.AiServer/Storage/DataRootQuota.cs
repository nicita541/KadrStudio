namespace KadrStudio.AiServer.Storage;

public sealed class DataRootQuota
{
    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DataRootQuota(string dataRoot, long maximumBytes)
    {
        _root = Path.GetFullPath(dataRoot);
        _maximumBytes = maximumBytes > 0 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        Directory.CreateDirectory(_root);
    }

    public long GetStorageBytes()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch (IOException) { return 0L; }
                    catch (UnauthorizedAccessException) { return 0L; }
                })
            : 0;

    public long GetPartialBytes()
        => Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*.upload", SearchOption.AllDirectories)
                .Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch (IOException) { return 0L; }
                })
            : 0;

    public async Task<T> ExecuteWriteAsync<T>(
        long maximumGrowthBytes,
        Func<Task<T>> write,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = GetStorageBytes();
            if (maximumGrowthBytes > _maximumBytes - current)
                throw new StorageQuotaException(
                    "storage_quota_exceeded",
                    $"AI Server data root quota would be exceeded ({current} + {maximumGrowthBytes} > {_maximumBytes} bytes).");
            return await write().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

public sealed class StorageQuotaException(string errorCode, string message) : IOException(message)
{
    public string ErrorCode { get; } = errorCode;
}
