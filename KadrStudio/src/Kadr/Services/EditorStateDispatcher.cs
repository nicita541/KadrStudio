using System.Windows.Threading;

namespace KadrStudio.Services;

/// <summary>Applies background results on the editor's owner after checking their lifetime.</summary>
public sealed class EditorStateDispatcher(Dispatcher dispatcher) : IDisposable
{
    private int _disposed;

    public bool TryApply(Func<bool> isCurrent, Action apply)
    {
        if (Volatile.Read(ref _disposed) != 0 || dispatcher.HasShutdownStarted) return false;
        bool ApplyOnOwner()
        {
            if (Volatile.Read(ref _disposed) != 0 || !isCurrent()) return false;
            apply();
            return true;
        }
        return dispatcher.CheckAccess() ? ApplyOnOwner() : dispatcher.Invoke(ApplyOnOwner);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
