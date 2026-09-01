using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Rendering;

/// <summary>
/// Compatibility projection for existing preview/export callers. Composition
/// semantics live exclusively in <see cref="RenderGraphCompiler"/>.
/// </summary>
public sealed class RenderPlanBuilder(IProjectValidator? validator = null) : IRenderPlanBuilder
{
    private readonly RenderGraphCompiler _compiler = new(validator);

    public RenderPlan Build(ProjectState project, TimeRange? requestedRange = null)
    {
        var graph = _compiler.Compile(project, requestedRange);
        return new RenderPlan(
            graph.ProjectId, graph.ProjectRevision, graph.CanvasWidth, graph.CanvasHeight,
            graph.FrameRate, graph.Range, graph.VisualLayers, graph.AudioLayers, graph.TextLayers,
            graph.VideoGraphSignature, graph.AudioGraphSignature, graph.OverlaySignature, graph.ContentSignature)
        {
            AudioSampleRate = graph.AudioSampleRate,
            SourceDecodeSignature = graph.SourceDecodeSignature,
            VideoTransitions = graph.VideoTransitions,
            AudioTransitions = graph.AudioTransitions
        };
    }
}
