using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;

namespace KadrStudio.Core.Tests;

public sealed class SqliteProjectStoreTests
{
    [Fact]
    public async Task Project_roundtrip_is_exact_and_integrity_is_ok()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "project.kadr");
        var store = new SqliteProjectStore();
        var project = CreateProject();

        await store.SaveAsync(path, project);
        var loaded = await store.LoadAsync(path);
        var integrity = await store.CheckIntegrityAsync(path);

        AssertProjectsEqual(project, loaded);
        Assert.True(integrity.IsValid, integrity.Details);
        Assert.True(new FileInfo(path).Length > 1024);
    }

    [Fact]
    public async Task Project_is_stored_in_normalized_tables_with_valid_foreign_keys()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "normalized.kadr");
        var store = new SqliteProjectStore();
        await store.SaveAsync(path, CreateProject());

        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();

        Assert.Equal(5L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM tracks;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM media_sources;"));
        Assert.Equal(3L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM media_clips;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM text_clips;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM markers;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM transitions;"));
        Assert.Equal(3L, await ScalarInt64Async(connection,
            "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';"));
        Assert.Equal(0L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(0L, await ScalarInt64Async(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='project_state';"));
    }

    [Fact]
    public async Task Saving_again_preserves_embedded_checkpoints()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "project.kadr");
        var store = new SqliteProjectStore();
        var project = CreateProject();
        await store.SaveAsync(path, project);
        var checkpoint = await store.CreateCheckpointAsync(path, project, "before rename");
        var renamed = new EditorSession(project);
        renamed.Execute(new EditTransaction("rename", new RenameProjectCommand("Renamed")));

        await store.SaveAsync(path, renamed.State);

        var checkpoints = await store.GetCheckpointsAsync(path);
        var restored = await store.RestoreCheckpointAsync(path, checkpoint.Id);
        Assert.Single(checkpoints);
        AssertProjectsEqual(project, restored);
        Assert.Equal("Renamed", (await store.LoadAsync(path)).Name);
    }

    [Fact]
    public async Task Damaged_file_fails_integrity_and_load()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "damaged.kadr");
        await File.WriteAllTextAsync(path, "not a sqlite database");
        var store = new SqliteProjectStore();

        var integrity = await store.CheckIntegrityAsync(path);

        Assert.False(integrity.IsValid);
        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAsync(path));
    }

    [Fact]
    public async Task Legacy_json_is_rejected_with_clear_message()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "legacy.kadr");
        await File.WriteAllTextAsync(path, "{\"formatVersion\":1}");
        var store = new SqliteProjectStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));

        Assert.Contains("старый JSON-формат", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Recovery_is_isolated_by_project_id()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new SqliteRecoveryStore(directory.Path);
        var first = CreateProject() with { Name = "First" };
        var second = CreateProject() with { Name = "Second" };

        await recovery.SaveAsync(first, "edit first");
        await recovery.SaveAsync(second, "edit second");

        var list = await recovery.ListAsync();
        Assert.Equal(2, list.Count);
        AssertProjectsEqual(first, Assert.IsType<ProjectState>(await recovery.LoadAsync(first.Id)));
        AssertProjectsEqual(second, Assert.IsType<ProjectState>(await recovery.LoadAsync(second.Id)));
        await recovery.DeleteAsync(first.Id);
        Assert.Null(await recovery.LoadAsync(first.Id));
        Assert.NotNull(await recovery.LoadAsync(second.Id));
    }

    [Fact]
    public async Task Schema_v2_is_read_without_mutation_and_next_save_migrates_to_v3()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "v2.kadr");
        var store = new SqliteProjectStore();
        await store.SaveAsync(path, ProjectState.CreateNew("v2", FrameRate.Fps2997));
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                DROP TABLE transitions;
                DROP TABLE video_clip_details;
                DROP TABLE media_source_details;
                DROP TABLE sequence_settings;
                UPDATE metadata SET value='2' WHERE key='schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }
        var hashBefore = SHA256.HashData(await File.ReadAllBytesAsync(path));

        var loaded = await store.LoadAsync(path);

        Assert.Equal(FrameRate.Fps2997, loaded.FrameRate);
        Assert.Equal(48_000, loaded.Sequence.AudioSampleRate);
        Assert.Empty(loaded.Transitions);
        Assert.Equal(hashBefore, SHA256.HashData(await File.ReadAllBytesAsync(path)));

        await store.SaveAsync(path, loaded);
        await using var migrated = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await migrated.OpenAsync();
        Assert.Equal(3L, await ScalarInt64Async(migrated,
            "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';"));
        Assert.Equal(1L, await ScalarInt64Async(migrated,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='transitions';"));
    }

    private static ProjectState CreateProject()
    {
        var project = ProjectState.CreateNew("SQLite project", FrameRate.Fps23976);
        var source = new MediaSource(
            Guid.NewGuid(), "F:\\media\\input.mkv", "input.mkv", MediaKind.Video,
            TimelineTime.FromSeconds(120), true, 1920, 1080, FrameRate.Fps23976,
            "hevc", "aac", 123456, 789, "fingerprint");
        var visual = project.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var audio = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 0);
        var text = project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var firstVideo = new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.Zero, TimelineTime.Zero,
            TimelineTime.FromSeconds(5), null,
            new VideoParameters(0.1, 1.1, 0.9, 0, 0.4, 0.6, 1.2, 0.8, 12, 0.1, 0, 0.05, 0, 0.75), null);
        var secondVideo = new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.FromSeconds(5), TimelineTime.FromSeconds(5),
            TimelineTime.FromSeconds(5), null, new VideoParameters(), null);
        return project with
        {
            Sequence = new SequenceSettings(1920, 1080, FrameRate.Fps23976, 48_000),
            Revision = 42,
            Sources = ImmutableDictionary<Guid, MediaSource>.Empty.Add(source.Id, source with
            {
                PreviousPath = "E:\\old\\input.mkv",
                FastFingerprint = "fast",
                VerifiedFingerprint = "verified",
                IsVariableFrameRate = true,
                ProxyPath = "F:\\cache\\input.proxy.mp4",
                Streams =
                [
                    new MediaStreamDescriptor(0, MediaStreamKind.Video, "hevc", "yuv420p", 1920, 1080,
                        FrameRate: FrameRate.Fps23976, IsVariableFrameRate: true),
                    new MediaStreamDescriptor(1, MediaStreamKind.Audio, "aac", "fltp", SampleRate: 48_000, Channels: 2)
                ]
            }),
            MediaClips =
            [
                firstVideo,
                secondVideo,
                new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.Zero, TimelineTime.FromSeconds(1),
                    TimelineTime.FromSeconds(10), null, null, new AudioParameters(0.8, false, -0.2))
            ],
            Transitions =
            [
                new TimelineTransition(Guid.NewGuid(), TransitionKind.CrossDissolve, visual.Id,
                    firstVideo.Id, secondVideo.Id, TimelineTime.FromSeconds(4.5), TimelineTime.FromSeconds(1))
            ],
            TextClips = [new TextClip(Guid.NewGuid(), text.Id, TimelineTime.FromSeconds(2), TimelineTime.FromSeconds(3), "line 1\nline 2", new TextStyle())],
            Markers = [new TimelineMarker(Guid.NewGuid(), MarkerKind.Opening, TimelineTime.Zero, TimelineTime.FromSeconds(5), "Opening", Confidence: 0.9)],
            InPoint = TimelineTime.FromSeconds(1),
            OutPoint = TimelineTime.FromSeconds(9)
        };
    }

    private static void AssertProjectsEqual(ProjectState expected, ProjectState actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.CanvasWidth, actual.CanvasWidth);
        Assert.Equal(expected.CanvasHeight, actual.CanvasHeight);
        Assert.Equal(expected.FrameRate, actual.FrameRate);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
        Assert.True(expected.Tracks.SequenceEqual(actual.Tracks));
        Assert.Equal(expected.Sources.Keys.Order(), actual.Sources.Keys.Order());
        foreach (var id in expected.Sources.Keys)
        {
            var expectedSource = expected.Sources[id];
            var actualSource = actual.Sources[id];
            Assert.Equal(expectedSource with { Streams = default }, actualSource with { Streams = default });
            Assert.True(
                (expectedSource.Streams.IsDefault ? [] : expectedSource.Streams)
                .SequenceEqual(actualSource.Streams.IsDefault ? [] : actualSource.Streams));
        }
        Assert.True(expected.MediaClips.SequenceEqual(actual.MediaClips));
        Assert.True(expected.TextClips.SequenceEqual(actual.TextClips));
        Assert.True(expected.Transitions.SequenceEqual(actual.Transitions));
        Assert.True(expected.Markers.SequenceEqual(actual.Markers));
        Assert.Equal(expected.InPoint, actual.InPoint);
        Assert.Equal(expected.OutPoint, actual.OutPoint);
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KadrStudio", "core-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
