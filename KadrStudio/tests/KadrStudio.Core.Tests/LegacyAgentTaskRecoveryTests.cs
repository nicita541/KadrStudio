using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Persistence;
using KadrStudio.Application.Automation.Agent.Recovery;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Storage;

namespace KadrStudio.Core.Tests;

public sealed class LegacyAgentTaskRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_cancels_legacy_draft_task_and_redo_preserves_cancellation(bool envelopeFormat)
    {
        var (session, task) = CreateDraftSession(envelopeFormat);

        Assert.True(session.Undo());

        var message = Assert.Single(session.State.AiConversation.Messages);
        var envelope = envelopeFormat ? JsonSerializer.Deserialize<AgentTaskPersistenceEnvelope>(message.Text) : null;
        if (envelopeFormat) Assert.Equal(2, envelope!.FormatVersion);
        var cancelled = envelope?.Task ?? JsonSerializer.Deserialize<AgentTaskState>(message.Text)!;
        Assert.Equal(task.Id, cancelled.Id);
        Assert.Equal(AgentTaskPhase.Cancelled, cancelled.Phase);
        Assert.Null(cancelled.DraftSequenceId);
        Assert.Null(cancelled.Checkpoint);
        Assert.False(string.IsNullOrWhiteSpace(cancelled.FailureMessage));
        Assert.Single(cancelled.SafeJournal);
        Assert.True(session.Redo());
        Assert.NotNull(session.State.FindSequence(task.DraftSequenceId!.Value));
        Assert.Equal(message, Assert.Single(session.State.AiConversation.Messages));
    }

    [Fact]
    public void Reconciliation_preserves_unreadable_future_and_unrelated_memories_exactly()
    {
        var (session, task) = CreateDraftSession();
        var project = ProjectState.CreateNew().EnsureSequenceContainer() with { Id = task.ProjectId };
        var raw = JsonSerializer.Serialize(task);
        string[] payloads =
        [
            "{broken", "null", "[]", "{}",
            JsonSerializer.Serialize(new AgentTaskPersistenceEnvelope(999, task)),
            "{\"FormatVersion\":999," + raw[1..],
            "{\"Task\":null," + raw[1..],
            JsonSerializer.Serialize(task with { Id = Guid.NewGuid() }),
            JsonSerializer.Serialize(task with { ProjectId = Guid.NewGuid() })
        ];
        foreach (var payload in payloads)
        {
            var original = Assert.Single(session.State.AiConversation.Messages) with { Text = payload };
            var conversation = project.AiConversation with { Messages = [original] };
            Assert.Equal(original, Assert.Single(AgentTaskReferences.Reconcile(conversation, project).Messages));
        }
    }

    [Fact]
    public async Task Reconciled_legacy_task_survives_repeated_sqlite_save_and_reopen()
    {
        var (session, task) = CreateDraftSession();
        Assert.True(session.Undo());
        var directory = Path.Combine(Path.GetTempPath(), "KadrStudio", "legacy-task", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project.kadr");
            var state = session.State;
            var savedMessage = Assert.Single(state.AiConversation.Messages);
            for (var i = 0; i < 2; i++)
            {
                await new SqliteProjectStore().SaveAsync(path, state);
                state = await new SqliteProjectStore().LoadAsync(path);
                Assert.Null(state.FindSequence(task.DraftSequenceId!.Value));
                Assert.Equal(savedMessage, Assert.Single(state.AiConversation.Messages));
                var reopened = JsonSerializer.Deserialize<AgentTaskState>(savedMessage.Text)!;
                Assert.Equal(AgentTaskPhase.Cancelled, reopened.Phase);
                Assert.Null(reopened.Checkpoint);
                Assert.Null(reopened.DraftSequenceId);
                Assert.Equal(savedMessage, Assert.Single(AgentTaskReferences.Reconcile(state.AiConversation, state).Messages));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (EditorSession Session, AgentTaskState Task) CreateDraftSession(bool envelopeFormat = false)
    {
        var project = ProjectState.CreateNew().EnsureSequenceContainer();
        var session = new EditorSession(project);
        var draft = SequenceState.Capture(project, Guid.NewGuid(), "Draft", SequenceStatus.Draft);
        session.Execute(new EditTransaction("draft", new CreateSequenceCommand(draft)));
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTaskState(Guid.NewGuid(), project.Id, project.ActiveSequenceId!.Value,
            project.AiConversation.Id, "edit", AgentTaskPhase.ReviewingDraft, draft.Id, null, null, now, now,
            Journal: [], TargetSourceIds: [], Checkpoint: new EditorialTaskCheckpoint(
                AgentTaskPhase.ReviewingDraft, [], [], [], null, null, null, null, now));
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(task))!.AsObject();
        legacy.Remove(nameof(AgentTaskState.Journal));
        legacy.Remove(nameof(AgentTaskState.TargetSourceIds));
        JsonNode payload = envelopeFormat ? new JsonObject { ["FormatVersion"] = 2, ["Task"] = legacy } : legacy;
        var memory = new AiChatMessage(Guid.NewGuid(), AiChatRole.Assistant, AiChatMessageKind.AgentMemory,
            payload.ToJsonString(), now, AgentTaskId: task.Id);
        session.Execute(new EditTransaction("task", [new ReplaceAiConversationCommand(
            session.State.AiConversation with { Messages = [memory] })], RecordInHistory: false));
        return (session, task);
    }
}
