namespace KadrStudio.Models;

public sealed class ExportSettings
{
    public ExportResolution Resolution { get; set; } = ExportResolution.P1080;
    public bool UseHardwareEncoding { get; set; }
    public int Quality { get; set; } = 20;

    public (int Width, int Height) GetSize() => Resolution switch
    {
        ExportResolution.P480 => (854, 480),
        ExportResolution.P720 => (1280, 720),
        _ => (1920, 1080)
    };
}

public sealed record ExportProgress(double Percent, string Stage, string Detail);

