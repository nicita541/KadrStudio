using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

namespace KadrStudio.Core.Tests;

public sealed class AgentRangeQueryToolTests
{
    [Fact]
    public async Task Inspect_range_forwards_optional_semantic_query()
    {
        var backend = new CapturingBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "test",
            AgentTaskPhase.Investigating, null, null, [], [], null, null, null, now, now);

        var arguments = AgentToolJson.ToElement(new
        {
            target_kind = "media",
            target_id = Guid.NewGuid(),
            start_seconds = 10,
            end_seconds = 20,
            detail = "frames",
            query = "Проверь, является ли этот блок заставкой."
        });

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", arguments));

        Assert.True(result.IsSuccess);
        Assert.NotNull(backend.LastRequest);
        Assert.Equal(
            "Проверь, является ли этот блок заставкой.",
            backend.LastRequest!.Query);
    }

    private sealed class CapturingBackend : IAgentReadOnlyToolBackend
    {
        public AgentRangeInspectionRequest? LastRequest { get; private set; }

        public ValueTask<JsonElement> InspectProjectAsync(
            AgentToolContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectTimelineAsync(
            AgentToolContext context, Guid sequenceId, CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectMediaAsync(
            AgentToolContext context, Guid mediaId, CancellationToken cancellationToken)
            => ValueTask.FromResult(AgentToolJson.EmptyObject());

        public ValueTask<JsonElement> InspectRangeAsync(
            AgentToolContext context,
            AgentRangeInspectionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(AgentToolJson.ToElement(new { ok = true }));
        }
    }
}
