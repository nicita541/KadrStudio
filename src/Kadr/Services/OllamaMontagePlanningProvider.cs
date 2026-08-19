using KadrStudio.Application.Automation;
using KadrStudio.Core.Domain;

namespace KadrStudio.Services;

public sealed class OllamaMontagePlanningProvider(
    OllamaVideoAnalysisService ollama,
    IMontagePlanningProvider? fallback = null) : IMontagePlanningProvider
{
    private readonly IMontagePlanningProvider _fallback = fallback ?? new EvidenceMontagePlanningProvider();

    public Task<MontagePlan> CreatePlanAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default)
        => RunAsync(context, revise: false, cancellationToken);

    public Task<MontagePlan> RevisePlanAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default)
        => RunAsync(context, revise: true, cancellationToken);

    private async Task<MontagePlan> RunAsync(
        MontagePlanningContext context,
        bool revise,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ollama.PlanMontageAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var plan = revise
                ? await _fallback.RevisePlanAsync(context, cancellationToken).ConfigureAwait(false)
                : await _fallback.CreatePlanAsync(context, cancellationToken).ConfigureAwait(false);
            return plan with
            {
                Summary = plan.Summary + " ИИ не вернул безопасный структурированный ответ; использован проверенный базовый отбор.",
                Warnings = plan.Warnings.Add($"ИИ-сервер: {exception.Message}")
            };
        }
    }
}
