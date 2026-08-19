using System.Text.Json;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Diagnostics;
using KadrStudio.Application.Automation.Agent.Runtime;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Core.Tests;

public sealed class AgentDebugLoggingTests
{
    [Fact]
    public async Task Planning_loop_logs_full_exception_when_context_builder_fails()
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Проверь материал и подготовь план.");

        var registry = new AgentToolRegistry();
        var logger = new RecordingAgentDebugLog();

        var loop = new AgentPlanningLoop(
            orchestrator,
            registry,
            new AgentToolExecutor(
                registry,
                debugLog: logger),
            new NeverCalledModel(),
            conversationProvider: () =>
                throw new InvalidOperationException(
                    "Conversation context exploded."),
            debugLog: logger);

        var result = await loop.RunUntilPauseAsync();

        Assert.Equal(AgentTaskPhase.Failed, result.Phase);

        var failure = Assert.Single(
            logger.Entries.Where(entry =>
                entry.Area == "planning_loop" &&
                entry.EventName == "model_turn_failed"));

        Assert.NotNull(failure.Exception);
        Assert.Contains(
            "Conversation context exploded.",
            failure.Exception!,
            StringComparison.Ordinal);
        Assert.Contains(
            "InvalidOperationException",
            failure.Exception!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_executor_logs_call_exception_and_result()
    {
        var orchestrator = new AiAgentOrchestrator();
        var task = orchestrator.StartTask(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Проверь инструмент.");
        task = orchestrator.BeginInvestigation();

        var registry = new AgentToolRegistry();
        registry.Register(new ThrowingReadTool());

        var logger = new RecordingAgentDebugLog();
        var executor = new AgentToolExecutor(
            registry,
            debugLog: logger);

        var result = await executor.ExecuteAsync(
            task,
            AgentToolCall.Create(
                task.Id,
                "throwing_read",
                AgentToolJson.ToElement(new { marker = "debug" })));

        Assert.Equal(AgentToolResultStatus.Failed, result.Status);
        Assert.Contains(
            logger.Entries,
            entry => entry.EventName == "tool_call_requested");
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.EventName == "tool_exception" &&
                entry.Exception?.Contains(
                    "tool exploded",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.EventName == "tool_result" &&
                entry.Message?.Contains(
                    "Failed",
                    StringComparison.Ordinal) == true);
    }

    private sealed class RecordingAgentDebugLog : IAgentDebugLog
    {
        public List<AgentDebugLogEntry> Entries { get; } = [];

        public string? CurrentLogPath => "memory://agent-debug";

        public void Write(AgentDebugLogEntry entry)
        {
            Entries.Add(entry);
        }
    }

    private sealed class NeverCalledModel : IAgentModel
    {
        public ValueTask<AgentModelDecision> DecideAsync(
            AgentModelTurnRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Model must not be called when context construction fails.");
    }

    private sealed class ThrowingReadTool : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            "throwing_read",
            "Throws an exception so diagnostic logging can be verified.",
            AgentToolAccess.ReadOnly,
            AgentToolJson.ParseObject(
                """
                {
                  "type": "object",
                  "properties": {
                    "marker": { "type": "string" }
                  },
                  "additionalProperties": false
                }
                """));

        public ValueTask<AgentToolExecutionOutput> ExecuteAsync(
            AgentToolContext context,
            JsonElement arguments,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("tool exploded");
    }
}
