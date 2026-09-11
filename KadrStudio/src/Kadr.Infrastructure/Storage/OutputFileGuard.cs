using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KadrStudio.Infrastructure.Storage;

/// <summary>Validates publication targets before work and again immediately before rename.</summary>
public static class OutputFileGuard
{
    public static void Validate(string outputPath, IEnumerable<string> protectedPaths, bool allowOverwrite)
    {
        var output = Path.GetFullPath(outputPath);
        for (FileSystemInfo? entry = new FileInfo(output); entry is not null;
             entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Путь результата не должен проходить через символическую ссылку или junction.");
        foreach (var source in protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullSource = Path.GetFullPath(source);
            if (output.Equals(fullSource, StringComparison.OrdinalIgnoreCase) || SameFile(output, fullSource))
                throw new IOException("Результат нельзя записать поверх исходного файла или проекта. Выберите другой путь.");
        }
        if (File.Exists(output) && !allowOverwrite)
            throw new IOException("Файл результата уже существует. Подтвердите замену или выберите другой путь.");
    }

    private static bool SameFile(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        if (!OperatingSystem.IsWindows())
            return new FileInfo(first).ResolveLinkTarget(true)?.FullName == second ||
                   new FileInfo(second).ResolveLinkTarget(true)?.FullName == first;
        var left = Identity(first);
        var right = Identity(second);
        return left.VolumeSerialNumber == right.VolumeSerialNumber &&
               left.FileIndexHigh == right.FileIndexHigh && left.FileIndexLow == right.FileIndexLow;
    }

    private static ByHandleFileInformation Identity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Не удалось проверить принадлежность файла.", new Win32Exception(Marshal.GetLastWin32Error()));
        return info;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
