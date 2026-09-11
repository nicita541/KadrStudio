using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Storage;

namespace KadrStudio.Core.Tests;

public sealed class HistorySafetyTests
{
    [Fact]
    public void Undo_draft_creation_cancels_persisted_review_and_redo_does_not_revive_it()
    {
        var project = ProjectState.CreateNew().EnsureSequenceContainer();
        var session = new EditorSession(project);
        var draft = SequenceState.Capture(project, Guid.NewGuid(), "Draft", SequenceStatus.Draft);
        session.Execute(new EditTransaction("draft", new CreateSequenceCommand(draft)));
        var now = DateTimeOffset.UtcNow;
        var task = new KadrStudio.Application.Automation.Agent.AgentTaskState(Guid.NewGuid(), project.Id,
            project.ActiveSequenceId!.Value, project.AiConversation.Id, "edit",
            KadrStudio.Application.Automation.Agent.AgentTaskPhase.ReviewingDraft, draft.Id, null, null, now, now);
        var envelope = KadrStudio.Application.Automation.Agent.Persistence.AgentTaskPersistenceEnvelope.Create(task);
        var message = new AiChatMessage(Guid.NewGuid(), AiChatRole.Assistant, AiChatMessageKind.AgentMemory,
            System.Text.Json.JsonSerializer.Serialize(envelope), now, AgentTaskId: task.Id);
        session.Execute(new EditTransaction("task", [new ReplaceAiConversationCommand(
            session.State.AiConversation with { Messages = [message] })], RecordInHistory: false));
        Assert.True(session.Undo());
        var restored = System.Text.Json.JsonSerializer.Deserialize<KadrStudio.Application.Automation.Agent.Persistence.AgentTaskPersistenceEnvelope>(
            Assert.Single(session.State.AiConversation.Messages).Text)!.Task;
        Assert.Null(restored.DraftSequenceId);
        Assert.Equal(KadrStudio.Application.Automation.Agent.AgentTaskPhase.Cancelled, restored.Phase);
        var orchestrator = new KadrStudio.Application.Automation.Agent.AiAgentOrchestrator();
        orchestrator.RestoreTask(task);
        orchestrator.ReconcileProject(session.State);
        Assert.Equal(restored.Phase, orchestrator.CurrentTask!.Phase);
        Assert.Null(orchestrator.CurrentTask.DraftSequenceId);
        Assert.True(session.Redo());
        Assert.Contains(session.State.Sequences, item => item.Id == draft.Id);
        Assert.Equal(restored, System.Text.Json.JsonSerializer.Deserialize<KadrStudio.Application.Automation.Agent.Persistence.AgentTaskPersistenceEnvelope>(
            Assert.Single(session.State.AiConversation.Messages).Text)!.Task with { Journal = restored.Journal, TargetSourceIds = restored.TargetSourceIds });
    }

    [Fact]
    public async Task Legacy_recovery_remains_readable_and_next_save_wins_over_future_document_timestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "legacy-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRecoveryStore(root);
            var project = ProjectState.CreateNew("legacy") with { UpdatedAt = DateTimeOffset.Parse("2035-01-01T00:00:00Z") };
            await store.SaveAsync(project, "legacy");
            var path = Path.Combine(root, $"{project.Id:N}.recovery.kadr");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE legacy_entries AS SELECT id, project_id, project_name, revision,
                        updated_at, reason, snapshot_json, snapshot_checksum FROM recovery_entries;
                    DROP TABLE recovery_entries;
                    ALTER TABLE legacy_entries RENAME TO recovery_entries;
                    PRAGMA user_version=1;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            Assert.Equal("legacy", (await store.LoadAsync(project.Id))!.Name);
            await store.SaveAsync(project with { Name = "latest", UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z") }, "new save");
            Assert.Equal("latest", (await store.LoadAsync(project.Id))!.Name);
            Assert.Equal("latest", (await store.ListAsync())[0].Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Conversation_saved_between_edits_survives_undo_and_redo()
    {
        var session = new EditorSession(ProjectState.CreateNew());
        session.Execute(new EditTransaction("rename", new RenameProjectCommand("Edited")));
        var conversation = session.State.AiConversation with
        {
            Messages = [new AiChatMessage(Guid.NewGuid(), AiChatRole.User, AiChatMessageKind.Text,
                "Keep this message", DateTimeOffset.UtcNow)]
        };
        session.Execute(new EditTransaction("chat", [new ReplaceAiConversationCommand(conversation)],
            RecordInHistory: false, SynchronizeActiveSequence: false));

        Assert.True(session.Undo());
        Assert.Equal("Keep this message", Assert.Single(session.State.AiConversation.Messages).Text);
        Assert.True(session.Redo());
        Assert.Equal("Edited", session.State.Name);
        Assert.Equal("Keep this message", Assert.Single(session.State.AiConversation.Messages).Text);
    }

    [Fact]
    public async Task Recovery_keeps_latest_save_even_when_undo_moves_document_time_backwards()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "recovery-safety", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SqliteRecoveryStore(root);
            var project = ProjectState.CreateNew() with { UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z") };
            for (var i = 1; i <= 22; i++)
                await store.SaveAsync(project with { Name = "Edit " + i, Revision = i, UpdatedAt = project.UpdatedAt.AddMinutes(i) }, "edit");
            await store.SaveAsync(project with { Name = "Undo state" }, "undo");

            var reopened = new SqliteRecoveryStore(root);
            Assert.Equal("Undo state", (await reopened.LoadAsync(project.Id))!.Name);
            var versions = await reopened.ListAsync();
            Assert.Equal(20, versions.Count);
            Assert.Equal("Undo state", versions[0].Name);
        }
        finally { Directory.Delete(root, true); }
    }
}
