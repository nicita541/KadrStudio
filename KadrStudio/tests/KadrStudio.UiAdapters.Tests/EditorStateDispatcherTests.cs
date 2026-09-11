using System.Windows.Threading;
using KadrStudio.Services;

namespace KadrStudio.UiAdapters.Tests;

public sealed class EditorStateDispatcherTests
{
    [Fact]
    public async Task Background_callbacks_run_on_owner_and_recheck_lifetime_before_mutation()
    {
        var ready = new TaskCompletionSource<(EditorStateDispatcher Owner, Dispatcher Dispatcher, int ThreadId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult((new EditorStateDispatcher(dispatcher), dispatcher, Environment.CurrentManagedThreadId));
                Dispatcher.Run();
                stopped.SetResult();
            }
            catch (Exception exception) { ready.TrySetException(exception); stopped.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var owner = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var appliedThread = 0;
            Assert.True(await Task.Run(() => owner.Owner.TryApply(() => true,
                () => appliedThread = Environment.CurrentManagedThreadId)));
            Assert.Equal(owner.ThreadId, appliedThread);
            Assert.False(await Task.Run(() => owner.Owner.TryApply(() => false,
                () => throw new InvalidOperationException("Stale callback applied"))));
            owner.Owner.Dispose();
            Assert.False(await Task.Run(() => owner.Owner.TryApply(() => true,
                () => throw new InvalidOperationException("Disposed callback applied"))));
        }
        finally
        {
            owner.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
