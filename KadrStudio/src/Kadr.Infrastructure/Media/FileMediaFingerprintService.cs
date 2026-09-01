using System.Buffers;
using System.Security.Cryptography;
using KadrStudio.Application.Media;

namespace KadrStudio.Infrastructure.Media;

public sealed class FileMediaFingerprintService : IMediaFingerprintService
{
    private const int WindowSize = 64 * 1024;

    public async Task<MediaFingerprint> ComputeFastAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = RequireFile(path);
        await using var stream = new FileStream(
            info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            WindowSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        BitConverter.TryWriteBytes(length, info.Length);
        hash.AppendData(length);
        var offsets = new[]
        {
            0L,
            Math.Max(0, info.Length / 2 - WindowSize / 2),
            Math.Max(0, info.Length - WindowSize)
        }.Distinct().ToArray();
        var buffer = ArrayPool<byte>.Shared.Rent(WindowSize);
        try
        {
            foreach (var offset in offsets)
            {
                stream.Position = offset;
                var remaining = (int)Math.Min(WindowSize, info.Length - offset);
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }
            return new MediaFingerprint(info.Length, info.LastWriteTimeUtc.Ticks,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<MediaFingerprint> ComputeVerifiedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fast = await ComputeFastAsync(path, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(
            Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var verified = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        return fast with { VerifiedHash = verified };
    }

    private static FileInfo RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Media path is required.", nameof(path));
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException("Media file was not found.", info.FullName);
        return info;
    }
}
