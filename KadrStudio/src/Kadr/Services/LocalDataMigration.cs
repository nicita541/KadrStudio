using System.Security.Cryptography;

namespace KadrStudio.Services;

/// <summary>Explicit offline migration; never deletes or merges existing user data.</summary>
public static class LocalDataMigration
{
    public static async Task CopyAsync(string sourceRoot, string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (source.Equals(Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Migration requires data directories, not volume roots.");
        if (IsWithin(source, destination) || IsWithin(destination, source))
            throw new IOException("Migration roots must be separate directories.");
        RejectLinks(source);
        RejectLinks(destination);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Migration destination already exists; existing data will not be merged or overwritten.");

        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("Migration destination needs a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".kadr-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        // Failed staging is deliberately preserved for diagnosis; only a verified tree is published.
        var entries = Snapshot(source);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, isDirectory) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = Path.Combine(source, relative);
            var target = Path.Combine(staging, relative);
            RejectLinks(original);
            if (isDirectory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            var sourceHash = await HashAsync(original, cancellationToken).ConfigureAwait(false);
            if (sourceHash != await HashAsync(target, cancellationToken).ConfigureAwait(false))
                throw new IOException("Migration verification failed: " + relative);
            hashes.Add(relative, sourceHash);
        }
        if (!entries.SequenceEqual(Snapshot(source)))
            throw new IOException("Source tree changed during migration. Close all KadrStudio processes before retrying.");
        foreach (var (relative, hash) in hashes)
        {
            RejectLinks(Path.Combine(source, relative));
            if (hash != await HashAsync(Path.Combine(source, relative), cancellationToken).ConfigureAwait(false))
                throw new IOException("Source file changed during migration: " + relative);
        }
        cancellationToken.ThrowIfCancellationRequested();
        RejectLinks(destination);
        RejectLinks(staging);
        Directory.Move(staging, destination);
    }

    private static (string Relative, bool IsDirectory)[] Snapshot(string root)
    {
        var entries = new List<(string, bool)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Migration does not follow links: " + path);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add((Path.GetRelativePath(root, path), isDirectory));
                if (isDirectory) pending.Push(path);
            }
        }
        return entries.OrderBy(entry => entry.Item1, StringComparer.Ordinal).ToArray();
    }

    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Migration does not follow links: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }
}
