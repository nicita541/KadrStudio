using System.Diagnostics;
using System.Text;

namespace KadrStudio.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    public const int MaximumOutputCharacters = 16 * 1024 * 1024;
    public const int MaximumErrorCharacters = 64 * 1024;
    public const int MaximumCallbackLineCharacters = 4096;

    public async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(executable)}.");
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var standardOutputTask = ReadOutputAsync(process.StandardOutput, lifetime.Token);
        var errorBuilder = new StringBuilder();
        var errorTask = ReadErrorAsync(process, errorBuilder, onErrorLine, lifetime.Token);
        var exitTask = process.WaitForExitAsync(lifetime.Token);

        try
        {
            var pending = new List<Task> { standardOutputTask, errorTask, exitTask };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
        }
        catch
        {
            lifetime.Cancel();
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (Exception exception) { Trace.TraceWarning("Process cleanup failed: {0}", exception); }
            try { await Task.WhenAll(standardOutputTask, errorTask, exitTask).ConfigureAwait(false); }
            catch { /* Observe owned tasks while preserving the original failure. */ }
            throw;
        }

        return new ProcessResult(process.ExitCode, await standardOutputTask.ConfigureAwait(false), errorBuilder.ToString());
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (output.Length > MaximumOutputCharacters - count)
                throw new InvalidDataException($"Process stdout exceeded {MaximumOutputCharacters} characters.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static async Task ReadErrorAsync(
        Process process,
        StringBuilder errorBuilder,
        Action<string>? onErrorLine,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var previousWasCarriageReturn = false;
        int count;
        while ((count = await process.StandardError.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            errorBuilder.Append(buffer, 0, count);
            if (errorBuilder.Length > MaximumErrorCharacters)
                errorBuilder.Remove(0, errorBuilder.Length - MaximumErrorCharacters);
            if (onErrorLine is null) continue;
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    if (character != '\n' || !previousWasCarriageReturn)
                    {
                        onErrorLine(line.ToString());
                        line.Clear();
                    }
                    previousWasCarriageReturn = character == '\r';
                }
                else
                {
                    previousWasCarriageReturn = false;
                    if (line.Length < MaximumCallbackLineCharacters) line.Append(character);
                }
            }
        }
        if (line.Length > 0) onErrorLine?.Invoke(line.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Процесс уже мог завершиться между проверкой и вызовом Kill.
        }
    }
}
