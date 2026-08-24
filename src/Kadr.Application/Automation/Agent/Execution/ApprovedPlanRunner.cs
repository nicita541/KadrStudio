using System.Collections.Immutable;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent.Execution;

public sealed record ApprovedPlanRunResult(
    string? Error,
    ImmutableArray<AgentToolResult> Results,
    ImmutableArray<Guid> CompletedStepIds)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(Error);
}

/// <summary>
/// Executes only the exact editing calls materialized in an approved plan.
/// It never consults a model and never receives read-only planning decisions.
/// </summary>
public sealed class ApprovedPlanRunner(
    AiAgentOrchestrator orchestrator,
    AgentToolRegistry registry,
    AgentToolExecutor toolExecutor)
{
    public async Task<ApprovedPlanRunResult> RunAsync(
        AgentTaskState task,
        IReadOnlySet<Guid> alreadyCommittedStepIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(alreadyCommittedStepIds);
        var results = ImmutableArray.CreateBuilder<AgentToolResult>();
        var completed = ImmutableArray.CreateBuilder<Guid>();
        foreach (var step in task.Plan!.Steps
                     .Where(step => !string.IsNullOrWhiteSpace(step.ExpectedEditingTool))
                     .OrderBy(step => step.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (alreadyCommittedStepIds.Contains(step.Id))
            {
                continue;
            }

            if (!registry.TryGet(step.ExpectedEditingTool!, out var tool) ||
                tool is null ||
                tool.Descriptor.Access != AgentToolAccess.Editing)
            {
                return new ApprovedPlanRunResult(
                    $"Approved editing tool '{step.ExpectedEditingTool}' is no longer available.",
                    results.ToImmutable(),
                    completed.ToImmutable());
            }

            var current = orchestrator.CurrentTask ?? task;
            var result = await toolExecutor.ExecuteAsync(
                current,
                AgentToolCall.CreateApprovedStep(task, step),
                cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (!result.IsSuccess)
            {
                return new ApprovedPlanRunResult(
                    $"Approved action '{step.Title}' failed: {result.Summary}",
                    results.ToImmutable(),
                    completed.ToImmutable());
            }

            completed.Add(step.Id);
            orchestrator.RecordProgress($"Выполнено: {step.Title}");
        }

        return new ApprovedPlanRunResult(
            null,
            results.ToImmutable(),
            completed.ToImmutable());
    }
}
