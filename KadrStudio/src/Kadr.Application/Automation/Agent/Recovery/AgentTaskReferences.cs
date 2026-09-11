using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Persistence;
using KadrStudio.Core.Domain;

namespace KadrStudio.Application.Automation.Agent.Recovery;

public static class AgentTaskReferences
{
    public static AgentTaskState Reconcile(AgentTaskState task, ProjectState project)
    {
        if (task.ProjectId != project.Id || task.DraftSequenceId is not { } draft || project.FindSequence(draft) is not null)
            return task;
        var now = DateTimeOffset.UtcNow;
        const string reason = "Связанный Agent Draft отсутствует в текущем состоянии проекта.";
        return task with
        {
            DraftSequenceId = null,
            Phase = task.IsTerminal ? task.Phase : AgentTaskPhase.Cancelled,
            FailureMessage = task.IsTerminal ? task.FailureMessage : reason,
            Checkpoint = null,
            UpdatedAt = now,
            Journal = task.SafeJournal.Add(new AgentJournalEntry(Guid.NewGuid(), now, AgentJournalKind.PhaseChanged, reason))
        };
    }

    public static AiConversation Reconcile(AiConversation conversation, ProjectState project)
        => conversation with { Messages = conversation.Messages.Select(message =>
        {
            if (message.Kind != AiChatMessageKind.AgentMemory || message.AgentTaskId is null) return message;
            try
            {
                using var document = JsonDocument.Parse(message.Text);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return message;
                // Envelope-shaped data must never fall through to legacy task decoding.
                var isEnvelope = root.TryGetProperty(nameof(AgentTaskPersistenceEnvelope.FormatVersion), out _) ||
                    root.TryGetProperty(nameof(AgentTaskPersistenceEnvelope.Task), out _);
                var envelope = isEnvelope ? root.Deserialize<AgentTaskPersistenceEnvelope>() : null;
                var task = isEnvelope ? envelope?.Task : root.Deserialize<AgentTaskState>();
                if (task is null || task.Id != message.AgentTaskId ||
                    envelope?.FormatVersion > AgentTaskPersistenceEnvelope.CurrentFormatVersion) return message;
                var reconciled = Reconcile(task, project);
                return ReferenceEquals(task, reconciled) ? message : message with
                {
                    Text = envelope is null
                        ? JsonSerializer.Serialize(reconciled with { TargetSourceIds = reconciled.SafeTargetSourceIds })
                        : JsonSerializer.Serialize(envelope with { Task = reconciled with { TargetSourceIds = reconciled.SafeTargetSourceIds } })
                };
            }
            catch (JsonException) { return message; }
        }).ToImmutableArray() };
}
