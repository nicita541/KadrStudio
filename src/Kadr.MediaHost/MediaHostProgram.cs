using System.Globalization;

namespace KadrStudio.MediaHost;

public static class MediaHostProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            using var lifetime = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                lifetime.Cancel();
            };
            await using var server = new MediaHostServer(options.PipeName, options.FfmpegPath);
            await server.RunAsync(lifetime.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
    }

    private static HostOptions Parse(IReadOnlyList<string> args)
    {
        string? pipe = null;
        string? ffmpeg = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Count:
                    pipe = args[++index];
                    break;
                case "--ffmpeg" when index + 1 < args.Count:
                    ffmpeg = args[++index];
                    break;
                case "--protocol" when index + 1 < args.Count:
                    _ = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(pipe)) throw new ArgumentException("--pipe is required.");
        if (string.IsNullOrWhiteSpace(ffmpeg)) throw new ArgumentException("--ffmpeg is required.");
        var fullFfmpeg = Path.GetFullPath(ffmpeg);
        if (!File.Exists(fullFfmpeg)) throw new FileNotFoundException("FFmpeg was not found.", fullFfmpeg);
        return new HostOptions(pipe, fullFfmpeg);
    }

    private sealed record HostOptions(string PipeName, string FfmpegPath);
}
