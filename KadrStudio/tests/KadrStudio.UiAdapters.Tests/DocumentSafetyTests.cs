using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Storage;
using KadrStudio.Services;
using KadrStudio.Application.Storage;

namespace KadrStudio.UiAdapters.Tests;

public sealed class DocumentSafetyTests
{
    [Fact]
    public async Task Checkpoints_follow_first_save_and_save_as_without_changing_ids()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "history-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var coordinator = new ProjectDocumentCoordinator(Path.Combine(root, "history"));
            using var service = new ProjectService(Path.Combine(root, "recovery"), coordinator);
            var history = new ProjectHistoryService(coordinator: coordinator);
            var project = ProjectState.CreateNew("first");
            var checkpoint = await history.CreateCheckpointAsync(project, null, "Before save");
            var original = Path.Combine(root, "original.kadr");
            var copy = Path.Combine(root, "copy.kadr");
            await service.SaveAsync(project, original);
            Assert.Equal(checkpoint.Id, Assert.Single(await history.GetCheckpointsAsync(project, original)).Id);
            await service.SaveAsync(project with { Name = "copy" }, copy);
            Assert.Equal(checkpoint.Id, Assert.Single(await history.GetCheckpointsAsync(project, null)).Id);
            Assert.Equal("first", (await history.RestoreCheckpointAsync(checkpoint)).Name);
            Assert.Equal("first", (await new SqliteProjectStore().LoadAsync(original)).Name);
            Assert.Equal("copy", (await new SqliteProjectStore().LoadAsync(copy)).Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Save_and_checkpoint_share_the_document_queue()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "document-queue", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var coordinator = new ProjectDocumentCoordinator(Path.Combine(root, "history"));
            using var service = new ProjectService(Path.Combine(root, "recovery"), coordinator);
            var history = new ProjectHistoryService(coordinator: coordinator);
            var project = ProjectState.CreateNew();
            var path = Path.Combine(root, "project.kadr");
            var gate = await coordinator.EnterAsync();
            Task save;
            Task<ProjectHistoryEntry> checkpoint;
            try
            {
                save = service.SaveAsync(project, path);
                checkpoint = history.CreateCheckpointAsync(project, null, "Queued before first save completed");
                Assert.False(save.IsCompleted);
                Assert.False(checkpoint.IsCompleted);
            }
            finally { gate.Dispose(); }
            await Task.WhenAll(save, checkpoint);
            Assert.Equal((await checkpoint).Id, Assert.Single(await new SqliteProjectStore().GetCheckpointsAsync(path)).Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Saving_an_older_snapshot_does_not_delete_newer_recovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "recovery-retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recovery = Path.Combine(root, "recovery");
            using var service = new ProjectService(recovery);
            var older = ProjectState.CreateNew("older");
            var newer = older with { Name = "unsaved changes", Revision = 1 };
            await service.SaveAutosaveAsync(newer);
            await service.SaveAsync(older, Path.Combine(root, "project.kadr"));
            Assert.Equal("unsaved changes", (await new SqliteRecoveryStore(recovery).LoadAsync(older.Id))!.Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_save_as_retains_the_original_write_lease()
    {
        var root = Path.Combine(Path.GetTempPath(), "KadrStudio", "document-safety", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new ProjectService(Path.Combine(root, "recovery"));
            var project = ProjectState.CreateNew();
            var original = Path.Combine(root, "original.kadr");
            await service.SaveAsync(project, original);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.SaveAsync(project with { Tracks = [] },
                Path.Combine(root, "copy.kadr")));
            Assert.Throws<ProjectFileLockedException>(() => { using var competing = ProjectFileLease.Acquire(original); });
            Assert.Equal(project.Id, (await new SqliteProjectStore().LoadAsync(original)).Id);
        }
        finally { Directory.Delete(root, true); }
    }
}
