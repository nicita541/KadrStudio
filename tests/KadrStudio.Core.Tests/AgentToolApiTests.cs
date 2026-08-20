using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Tools.ReadOnly;

namespace KadrStudio.Core.Tests;

public sealed class AgentToolApiTests
{
    [Fact]
    public void Default_read_only_tool_set_has_stable_unique_names()
    {
        var registry = AgentReadOnlyToolSet.Create(new FakeBackend());

        Assert.Equal(
            new[]
            {
                "compare_media_ranges",
                "compare_sequences",
                "inspect_boundary",
                "inspect_editor_context",
                "inspect_media",
                "inspect_objects",
                "inspect_project",
                "inspect_range",
                "inspect_sequence_overview",
                "inspect_timeline",
                "inspect_timeline_integrity",
                "search_timeline"
            },
            registry.Descriptors.Select(item => item.Name).ToArray());

        Assert.All(
            registry.Descriptors,
            descriptor => Assert.Equal(
                AgentToolAccess.ReadOnly,
                descriptor.Access));
    }

    [Fact]
    public async Task Inspect_project_executes_against_active_task()
    {
        var backend = new FakeBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_project"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, backend.ProjectCalls);
        Assert.Equal(task.ProjectId, result.Data!.Value.GetProperty("project_id").GetGuid());
    }

    [Fact]
    public async Task Timeline_defaults_to_draft_during_execution()
    {
        var backend = new FakeBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);

        var source = Guid.NewGuid();
        var draft = Guid.NewGuid();
        var task = CreateTask(
            phase: AgentTaskPhase.Executing,
            sourceSequenceId: source,
            draftSequenceId: draft);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_timeline"));

        Assert.True(result.IsSuccess);
        Assert.Equal(draft, backend.LastTimelineSequenceId);
    }

    [Fact]
    public async Task Inspect_range_validates_time_order_before_backend_call()
    {
        var backend = new FakeBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();

        var arguments = AgentToolJson.ToElement(new
        {
            target_kind = "media",
            target_id = Guid.NewGuid(),
            start_seconds = 20,
            end_seconds = 10,
            detail = "frames"
        });

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(
                task.Id,
                "inspect_range",
                arguments));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(0, backend.RangeCalls);
    }

    [Fact]
    public async Task Inspect_range_rejects_semantic_query_with_structure_only_summary()
    {
        var backend = new FakeBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();
        var arguments = AgentToolJson.ToElement(new
        {
            target_kind = "sequence",
            target_id = task.SourceSequenceId,
            start_seconds = 0,
            end_seconds = 120,
            detail = "summary",
            query = "Где заканчивается опенинг?"
        });

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "inspect_range", arguments));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Contains("frames, audio, transcript or all", result.Summary, StringComparison.Ordinal);
        Assert.Equal(0, backend.RangeCalls);
    }

    [Fact]
    public async Task Unknown_tool_is_rejected_without_throwing()
    {
        var registry = AgentReadOnlyToolSet.Create(new FakeBackend());
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "does_not_exist"));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("tool_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task Tool_call_from_another_task_is_rejected()
    {
        var registry = AgentReadOnlyToolSet.Create(new FakeBackend());
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(
                Guid.NewGuid(),
                "inspect_project"));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("task_mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task Tools_do_not_run_while_waiting_for_user_or_approval()
    {
        var backend = new FakeBackend();
        var registry = AgentReadOnlyToolSet.Create(backend);
        var executor = new AgentToolExecutor(registry);

        foreach (var phase in new[]
                 {
                     AgentTaskPhase.WaitingForUserInput,
                     AgentTaskPhase.WaitingForApproval,
                     AgentTaskPhase.Approved
                 })
        {
            var task = CreateTask(phase: phase);
            var result = await executor.ExecuteAsync(
                task,
                AgentToolCall.Create(task.Id, "inspect_project"));

            Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
            Assert.Equal("phase_not_executable", result.ErrorCode);
        }

        Assert.Equal(0, backend.ProjectCalls);
    }

    [Fact]
    public async Task Editing_tool_requires_separate_draft()
    {
        var registry = new AgentToolRegistry();
        registry.Register(new FakeEditingTool());

        var executor = new AgentToolExecutor(registry);
        var task = CreateTask(phase: AgentTaskPhase.Executing);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "fake_edit"));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("draft_required", result.ErrorCode);
    }

    [Fact]
    public async Task Editing_tool_is_rejected_during_planning()
    {
        var registry = new AgentToolRegistry();
        registry.Register(new FakeEditingTool());

        var executor = new AgentToolExecutor(registry);
        var task = CreateTask(phase: AgentTaskPhase.Investigating);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "fake_edit"));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("editing_phase_required", result.ErrorCode);
    }

    [Fact]
    public async Task Editing_tool_can_execute_only_on_separate_draft()
    {
        var registry = new AgentToolRegistry();
        registry.Register(new FakeEditingTool());

        var executor = new AgentToolExecutor(registry);
        var task = CreateTask(
            phase: AgentTaskPhase.Executing,
            sourceSequenceId: Guid.NewGuid(),
            draftSequenceId: Guid.NewGuid());

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "fake_edit"));

        Assert.True(result.IsSuccess);
        Assert.Equal("fake_edit", result.ToolName);
    }

    [Fact]
    public async Task Oversized_observation_is_rejected()
    {
        var registry = new AgentToolRegistry();
        registry.Register(new LargeReadOnlyTool());

        var executor = new AgentToolExecutor(
            registry,
            new AgentToolExecutorOptions(
                MaxObservationCharacters: 100));

        var task = CreateTask();

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "large_read"));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("observation_too_large", result.ErrorCode);
    }

    [Fact]
    public void Duplicate_tool_names_are_rejected()
    {
        var registry = new AgentToolRegistry();
        registry.Register(new NamedReadOnlyTool("inspect_test"));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new NamedReadOnlyTool("inspect_test")));
    }

    [Fact]
    public async Task Tool_lookup_is_case_insensitive()
    {
        var registry = AgentReadOnlyToolSet.Create(new FakeBackend());
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(task.Id, "INSPECT_PROJECT"));

        Assert.True(result.IsSuccess);
        Assert.Equal("inspect_project", result.ToolName);
    }

    [Fact]
    public async Task Unknown_arguments_are_rejected()
    {
        var registry = AgentReadOnlyToolSet.Create(new FakeBackend());
        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(
                task.Id,
                "inspect_project",
                AgentToolJson.ToElement(new { unexpected = true })));

        Assert.Equal(AgentToolResultStatus.Rejected, result.Status);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_tool_failure()
    {
        var registry = new AgentToolRegistry();
        registry.Register(new CancelAwareTool());

        var executor = new AgentToolExecutor(registry);
        var task = CreateTask();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () =>
            {
                await executor.ExecuteAsync(
                    task,
                    AgentToolCall.Create(task.Id, "cancel_read"),
                    source.Token);
            });
    }

    private static AgentTaskState CreateTask(
        AgentTaskPhase phase = AgentTaskPhase.Investigating,
        Guid? sourceSequenceId = null,
        Guid? draftSequenceId = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new AgentTaskState(
            Guid.NewGuid(),
            Guid.NewGuid(),
            sourceSequenceId ?? Guid.NewGuid(),
            null,
            "Test request",
            phase,
            null,
            null,
            [],
            [],
            draftSequenceId,
            null,
            null,
            now,
            now);
    }

    private sealed class FakeBackend : IAgentReadOnlyToolBackend
    {
        public int ProjectCalls { get; private set; }

        public int RangeCalls { get; private set; }

        public Guid? LastTimelineSequenceId { get; private set; }

        public ValueTask<JsonElement> InspectProjectAsync(
            AgentToolContext context,
            CancellationToken cancellationToken)
        {
            ProjectCalls++;
            return ValueTask.FromResult(
                AgentToolJson.ToElement(new
                {
                    project_id = context.ProjectId,
                    source_sequence_id = context.SourceSequenceId
                }));
        }

        public ValueTask<JsonElement> InspectTimelineAsync(
            AgentToolContext context,
            Guid sequenceId,
            CancellationToken cancellationToken)
        {
            LastTimelineSequenceId = sequenceId;
            return ValueTask.FromResult(
                AgentToolJson.ToElement(new
                {
                    sequence_id = sequenceId
                }));
        }

        public ValueTask<JsonElement> InspectMediaAsync(
            AgentToolContext context,
            Guid mediaId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(
                AgentToolJson.ToElement(new
                {
                    media_id = mediaId
                }));

        public ValueTask<JsonElement> InspectRangeAsync(
            AgentToolContext context,
            AgentRangeInspectionRequest request,
            CancellationToken cancellationToken)
        {
            RangeCalls++;
            return ValueTask.FromResult(
                AgentToolJson.ToElement(new
                {
                    request.TargetId,
                    request.StartSeconds,
                    request.EndSeconds,
                    detail = request.Detail.ToString()
                }));
        }
    }

    private sealed class FakeEditingTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "fake_edit",
            "Editing tool used only to verify the Stage 3 safety boundary.",
            AgentToolAccess.Editing,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    "Fake draft edit completed.",
                    new
                    {
                        draft_sequence_id = context.DraftSequenceId
                    }));
    }

    private sealed class LargeReadOnlyTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "large_read",
            "Returns intentionally oversized output for the guard test.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    "Large observation.",
                    new { text = new string('x', 1000) }));
    }

    private sealed class NamedReadOnlyTool : IAgentTool
    {
        public NamedReadOnlyTool(string name)
        {
            Descriptor = new AgentToolDescriptor(
                name,
                "Named test tool.",
                AgentToolAccess.ReadOnly,
                AgentToolJson.EmptyObject());
        }

        public AgentToolDescriptor Descriptor { get; }

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    "OK",
                    new { ok = true }));
    }

    private sealed class CancelAwareTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "cancel_read",
            "Cancellation propagation test tool.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.EmptyObject());

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                AgentToolExecutionOutput.From(
                    "OK",
                    new { ok = true }));
        }
    }
}
