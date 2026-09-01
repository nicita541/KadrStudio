namespace KadrStudio.Services;

public sealed class FfmpegLocator
{
    public FfmpegLocator()
    {
        var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools");
        FfmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");
        FfprobePath = Path.Combine(toolsDirectory, "ffprobe.exe");
    }

    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public void EnsureAvailable()
    {
        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath))
        {
            throw new FileNotFoundException(
                "Не найдены ffmpeg.exe и ffprobe.exe. Переустановите Kadr Studio или выполните сборку через build-release.ps1.");
        }
    }
}

