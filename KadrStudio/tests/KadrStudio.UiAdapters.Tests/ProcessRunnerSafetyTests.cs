using System.Diagnostics;
using System.Text;
using KadrStudio.Services;
using Xunit;

namespace KadrStudio.UiAdapters.Tests;

public sealed class ProcessRunnerSafetyTests
{
    [Fact]
    public async Task Cancelled_request_does_not_attempt_process_start()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().RunAsync(
            "missing-process-must-not-be-started.exe", [], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task HugeStandardOutputFailsExplicitly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(
            "[Console]::Out.Write(('x' * (20 * 1024 * 1024)))", cancellationToken: timeout.Token));
    }

    [Fact]
    public async Task HugeStandardErrorRetainsOnlyTail()
    {
        var result = await RunAsync("1..10000 | ForEach-Object { [Console]::Error.WriteLine(('x' * 100)) }; [Console]::Error.WriteLine('THE-END')");
        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.StandardError.Length, 1, 64 * 1024);
        Assert.EndsWith("THE-END" + Environment.NewLine, result.StandardError);
    }

    [Fact]
    public async Task UnbrokenStandardErrorBoundsCallbackAndKeepsNextLine()
    {
        var lines = new List<string>();
        var result = await RunAsync("[Console]::Error.Write(('x' * (2 * 1024 * 1024))); [Console]::Error.WriteLine(); [Console]::Error.WriteLine('NEXT')", lines.Add);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, lines.Count);
        Assert.InRange(lines[0].Length, 1, 4096);
        Assert.Equal("NEXT", lines[1]);
    }

    [Fact]
    public async Task ThrowingCallbackStopsContinuousWriterBeforeCancellationAndLeavesNoChild()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Process? child = null;
        try
        {
            await Assert.ThrowsAsync<CallbackException>(() => RunAsync(
                "[Console]::Error.WriteLine($PID); while ($true) { [Console]::Error.WriteLine(('x' * 4096)) }",
                line => { child = Process.GetProcessById(int.Parse(line)); throw new CallbackException(); }, timeout.Token));
            Assert.False(timeout.IsCancellationRequested);
            Assert.NotNull(child);
            Assert.True(child.HasExited);
        }
        finally { StopFixture(child); }
    }

    [Fact]
    public async Task CancellationWaitsForOwnedChildToExit()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Process? child = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(
                "[Console]::Error.WriteLine($PID); while ($true) { [Console]::Out.Write(('x' * 1024)); Start-Sleep -Milliseconds 10 }",
                line => { child = Process.GetProcessById(int.Parse(line)); cancellation.Cancel(); }, cancellation.Token));
            Assert.NotNull(child);
            Assert.True(child.HasExited);
        }
        finally { StopFixture(child); }
    }

    [Fact]
    public async Task NormalOutputAndErrorLinesArePreserved()
    {
        var lines = new List<string>();
        var result = await RunAsync("[Console]::Out.Write('{\"ok\":true}'); [Console]::Error.Write(\"first`r`n`nlast\")", lines.Add);
        Assert.Equal("{\"ok\":true}", result.StandardOutput);
        Assert.Equal(new[] { "first", "", "last" }, lines);
    }

    private static Task<ProcessResult> RunAsync(string script, Action<string>? callback = null, CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return new ProcessRunner().RunAsync(executable,
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }, callback, cancellationToken);
    }

    private static void StopFixture(Process? child)
    {
        if (child is null) return;
        try { if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(5000); } }
        finally { child.Dispose(); }
    }

    private sealed class CallbackException : Exception;
}
