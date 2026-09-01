using System.Text;

namespace KadrStudio.Infrastructure.Storage;

/// <summary>
/// Cross-process write lease for a project document. The lease is intentionally
/// stored beside the project so two application instances cannot silently
/// overwrite the same timeline.
/// </summary>
public sealed class ProjectFileLease : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private ProjectFileLease(string projectPath, string lockPath, FileStream stream)
    {
        ProjectPath = projectPath;
        LockPath = lockPath;
        _stream = stream;
    }

    public string ProjectPath { get; }
    public string LockPath { get; }

    public static ProjectFileLease Acquire(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var lockPath = fullPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            var details = Encoding.UTF8.GetBytes($"Kadr Studio project lease{Environment.NewLine}" +
                $"pid={Environment.ProcessId}{Environment.NewLine}" +
                $"opened={DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
                $"project={fullPath}{Environment.NewLine}");
            stream.Write(details);
            stream.Flush(flushToDisk: true);
            return new ProjectFileLease(fullPath, lockPath, stream);
        }
        catch (IOException exception)
        {
            throw new ProjectFileLockedException(fullPath, exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stream.Dispose();
        try { File.Delete(LockPath); } catch (IOException) { }
    }
}

public sealed class ProjectFileLockedException(string projectPath, Exception innerException)
    : IOException(
        $"Проект «{Path.GetFileName(projectPath)}» уже открыт для записи в другом окне Kadr Studio.",
        innerException)
{
    public string ProjectPath { get; } = projectPath;
}
