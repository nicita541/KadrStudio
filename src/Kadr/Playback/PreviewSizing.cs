using KadrStudio.Core.Domain;

namespace KadrStudio.Playback;

public static class PreviewSizing
{
    public static (int Width, int Height) Resolve(ProjectState project, bool useProxy)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!useProxy) return (project.CanvasWidth, project.CanvasHeight);

        var scale = Math.Min(0.5, Math.Min(960d / project.CanvasWidth, 540d / project.CanvasHeight));
        var width = Math.Max(2, (int)Math.Round(project.CanvasWidth * scale / 2) * 2);
        var height = Math.Max(2, (int)Math.Round(project.CanvasHeight * scale / 2) * 2);
        return (width, height);
    }
}
