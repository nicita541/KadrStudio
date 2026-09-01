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
    public async Task Schema_v7_roundtrips_editorial_index_graph_patches_and_quality_report()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "editorial-v7.kadr");
        var store = new SqliteProjectStore();
        var project = CreateProject();
        var source = Assert.Single(project.Sources.Values);
        var now = DateTimeOffset.UtcNow;
        var range = new TimeRange(TimelineTime.Zero, source.Duration);
        var shot = new ShotNode(Guid.NewGuid(), source.Id, range, [], [], "single shot");
        var coverage = new CoverageMap(
            source.Id,
            source.Fingerprint,
            source.Duration,
            ImmutableDictionary<CoverageChannel, ImmutableArray<CoverageInterval>>.Empty)
            .Add(
                CoverageChannel.Frames,
                CoverageInterval.Create(range, 120, false, "video-understanding", "2"));
        var index = new MediaUnderstandingIndex(
            Guid.NewGuid(), source.Id, source.Fingerprint, "2", source.Duration, coverage,
            [new AnalyzerManifest("video-understanding", "2", "test-vlm", "windows-grpc", [CoverageChannel.Frames], now)],
            [], [], [shot], [], [], [], [], now, now);
        var profile = new MontageProfile(
            "generic", 1, MontageProfileKind.Generic, "Generic",
            [CoverageChannel.Frames], [EditorialPassKind.StoryContinuity], 4, []);
        var brief = new EditorialBrief(
            Guid.NewGuid(), "shorten", "viewer", profile, null, "clean", [], ["source preserved"], now);
        var decision = new EditDecision(
            Guid.NewGuid(), EditDecisionKind.Keep, source.Id, range, 0,
            "measured material", 0.9, [], ImmutableDictionary<string, string>.Empty);
        var graph = new MontageGraph(
            Guid.NewGuid(), Guid.NewGuid(), project.Id,
            project.Revision, brief, [decision], 1, now, now);
        var patch = new DraftPatch(
            Guid.NewGuid(), graph.Id, EditorialPassKind.StoryContinuity, 0, [decision], "rough cut", now);
        var report = new DraftQualityReport(
            Guid.NewGuid(), graph.TaskId, Guid.NewGuid(), DraftQualityStatus.Passed,
            [], ImmutableDictionary<string, double>.Empty.Add("cut_accuracy_frames", 1), now);
        var external = new ExternalReference(
            Guid.NewGuid(), "https://example.invalid/reference", "Reference", "citation", now, true, "style");
        project = project with
        {
            UnderstandingIndexes = [index],
            MontageGraphs = [graph],
            DraftPatches = [patch],
            DraftQualityReports = [report],
            ExternalReferences = [external]
        };

        await store.SaveAsync(path, project);
        var loaded = await store.LoadAsync(path);

        Assert.Equivalent(index, Assert.Single(loaded.UnderstandingIndexes), strict: true);
        Assert.Equal(graph.Fingerprint(), Assert.Single(loaded.MontageGraphs).Fingerprint());
        Assert.Equivalent(patch, Assert.Single(loaded.DraftPatches), strict: true);
        Assert.Equivalent(report, Assert.Single(loaded.DraftQualityReports), strict: true);
        Assert.Equal(external, Assert.Single(loaded.ExternalReferences));
    }

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
    public async Task Schema_v7_legacy_sequence_metadata_is_ignored_but_draft_survives()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "legacy-draft.kadr");
        var store = new SqliteProjectStore();
        var project = CreateProject().EnsureSequenceContainer().SynchronizeActiveSequence();
        var source = project.ActiveSequence!;
        var draft = source with
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Agent Draft",
            Revision = 1,
            Status = SequenceStatus.Draft,
            ParentSequenceId = source.Id
        };
        project = project with { Sequences = project.Sequences.Add(draft) };

        await store.SaveAsync(path, project);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT snapshot_json FROM sequences WHERE id = $id;";
            read.Parameters.AddWithValue("$id", draft.Id.ToString("N"));
            var snapshot = (string)(await read.ExecuteScalarAsync())!;
            var legacyMetadata =
                $",\"montagePlanId\":\"{Guid.NewGuid()}\",\"agentCheckpoint\":{{\"taskId\":\"{Guid.NewGuid()}\",\"planId\":\"{Guid.NewGuid()}\"}}";
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sequences SET snapshot_json = $snapshot WHERE id = $id; UPDATE metadata SET value = '7' WHERE key = 'schema_version';";
            update.Parameters.AddWithValue("$id", draft.Id.ToString("N"));
            update.Parameters.AddWithValue("$snapshot", snapshot.Insert(snapshot.LastIndexOf('}'), legacyMetadata));
            await update.ExecuteNonQueryAsync();
        }

        var loaded = await store.LoadAsync(path);

        var loadedDraft = loaded.FindSequence(draft.Id);
        Assert.NotNull(loadedDraft);
        Assert.Equal(SequenceStatus.Draft, loadedDraft!.Status);
        Assert.Equal(source.Id, loadedDraft.ParentSequenceId);
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

        Assert.Equal(7L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM tracks;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM media_sources;"));
        Assert.Equal(4L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM media_clips;"));
        Assert.Equal(2L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM subtitle_clips;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM text_clips;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM markers;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM transitions;"));
        Assert.Equal(9L, await ScalarInt64Async(connection,
            "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM ai_conversation;"));
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
    public async Task Recovery_keeps_twenty_versions_and_restores_the_selected_revision()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new SqliteRecoveryStore(directory.Path);
        var original = CreateProject();
        for (var revision = 1; revision <= 25; revision++)
        {
            await recovery.SaveAsync(original with
            {
                Revision = revision,
                Name = $"Revision {revision}",
                UpdatedAt = original.UpdatedAt.AddSeconds(revision)
            }, $"edit {revision}");
        }

        var versions = await recovery.ListAsync();

        Assert.Equal(SqliteRecoveryStore.MaximumVersionsPerProject, versions.Count);
        Assert.Equal(Enumerable.Range(6, 20).Select(value => (long)value),
            versions.OrderBy(item => item.Revision).Select(item => item.Revision));
        var selected = versions.Single(item => item.Revision == 12);
        var restored = await recovery.LoadAsync(selected.ProjectId, selected.RecoveryId);
        Assert.NotNull(restored);
        Assert.Equal("Revision 12", restored.Name);
        await recovery.DeleteAsync(selected.ProjectId, selected.RecoveryId);
        Assert.DoesNotContain(await recovery.ListAsync(), item => item.RecoveryId == selected.RecoveryId);
    }

    [Fact]
    public async Task Recovery_with_modified_snapshot_is_rejected_by_checksum()
    {
        using var directory = new TemporaryDirectory();
        var recovery = new SqliteRecoveryStore(directory.Path);
        var project = CreateProject();
        await recovery.SaveAsync(project, "before corruption");
        var path = Path.Combine(directory.Path, $"{project.Id:N}.recovery.kadr");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE recovery_entries SET snapshot_json=snapshot_json || ' ';";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => recovery.LoadAsync(project.Id));
    }

    [Fact]
    public void Project_write_lease_prevents_parallel_writer_and_is_released_cleanly()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "locked.kadr");
        using (ProjectFileLease.Acquire(path))
            Assert.Throws<ProjectFileLockedException>(() => ProjectFileLease.Acquire(path));

        using var reopened = ProjectFileLease.Acquire(path);
        Assert.Equal(Path.GetFullPath(path), reopened.ProjectPath);
    }

    [Fact]
    public async Task Schema_v2_is_read_without_mutation_and_next_save_migrates_to_v6()
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
                DROP TABLE ai_chat_messages;
                DROP TABLE ai_conversation;
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
        Assert.Equal(9L, await ScalarInt64Async(migrated,
            "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';"));
        Assert.Equal(1L, await ScalarInt64Async(migrated,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='transitions';"));
    }

    [Fact]
    public async Task Schema_v5_without_agent_checkpoints_loads_and_next_save_migrates_to_v9()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "v5.kadr");
        var store = new SqliteProjectStore();
        await store.SaveAsync(path, CreateProject());
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE metadata SET value='5' WHERE key='schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await store.LoadAsync(path);

        Assert.NotEmpty(loaded.Sequences);
        await store.SaveAsync(path, loaded);
        await using var migrated = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await migrated.OpenAsync();
        Assert.Equal(9L, await ScalarInt64Async(migrated,
            "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';"));
    }

    [Fact]
    public async Task Schema_v9_roundtrips_track_renditions_and_upscale_jobs()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "upscale-v9.kadr");
        var store = new SqliteProjectStore();
        var project = CreateProject().EnsureSequenceContainer();
        var sequence = project.ActiveSequence!;
        var original = sequence.Tracks.Single(item => item.Kind == TrackKind.Visual && item.Index == 0);
        var enhancedIndex = sequence.Tracks.Where(item => item.Kind == TrackKind.Visual).Max(item => item.Index) + 1;
        var enhanced = new TimelineTrack(Guid.NewGuid(), TrackKind.Visual, enhancedIndex, "V1 · AnimeSR-X", IsVisible: false);
        var now = DateTimeOffset.UtcNow;
        var group = new TrackRenditionGroup(
            Guid.NewGuid(), sequence.Id, original.Id, enhanced.Id, TrackRenditionKind.Original,
            sequence.Revision, new string('a', 64), "AnimeSR-X-experimental", new string('b', 64), UpscaleScaleMode.Auto2160p,
            false, now, now);
        var job = new UpscaleJob(
            Guid.NewGuid(), sequence.Id, [original.Id], UpscaleScaleMode.Auto2160p,
            UpscaleJobState.Succeeded, 1, "done", new string('b', 64), now, now);
        project = (project with
        {
            Tracks = project.Tracks.Add(enhanced),
            RenditionGroups = [group],
            UpscaleJobs = [job]
        }).SynchronizeActiveSequence();

        await store.SaveAsync(path, project);
        var loaded = await store.LoadAsync(path);

        Assert.Equal(group, Assert.Single(loaded.RenditionGroups));
        Assert.Equivalent(job, Assert.Single(loaded.UpscaleJobs), strict: true);
        Assert.Equal(group, Assert.Single(loaded.ActiveSequence!.RenditionGroups));
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        Assert.Equal(9L, await ScalarInt64Async(connection,
            "SELECT CAST(value AS INTEGER) FROM metadata WHERE key='schema_version';"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM track_renditions;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM upscale_jobs;"));
    }

    [Fact]
    public async Task Ai_conversation_roundtrips_and_running_operation_becomes_interrupted()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "chat.kadr");
        var store = new SqliteProjectStore();
        var project = CreateProject();
        var now = DateTimeOffset.UtcNow;
        var conversation = new AiConversation(Guid.NewGuid(), now, now,
        [
            new AiChatMessage(Guid.NewGuid(), AiChatRole.User, AiChatMessageKind.Text, "Объедини серии", now),
            new AiChatMessage(Guid.NewGuid(), AiChatRole.Assistant, AiChatMessageKind.Progress,
                "Анализ…", now, AiChatOperationState.Running, 42)
        ]);

        await store.SaveAsync(path, project with { AiConversation = conversation });
        var loaded = await store.LoadAsync(path);

        Assert.Equal(conversation.Id, loaded.AiConversation.Id);
        Assert.Equal(2, loaded.AiConversation.Messages.Length);
        Assert.Equal("Объедини серии", loaded.AiConversation.Messages[0].Text);
        Assert.Equal(AiChatMessageKind.Error, loaded.AiConversation.Messages[1].Kind);
        Assert.Equal(AiChatOperationState.Interrupted, loaded.AiConversation.Messages[1].OperationState);
        Assert.Contains("прервана", loaded.AiConversation.Messages[1].Text, StringComparison.OrdinalIgnoreCase);
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
        var audio2 = project.Tracks.Single(item => item.Kind == TrackKind.Audio && item.Index == 1);
        var subtitle = project.Tracks.Single(item => item.Kind == TrackKind.Subtitle && item.Index == 0);
        var subtitle2 = new TimelineTrack(Guid.NewGuid(), TrackKind.Subtitle, 1, "S2");
        var text = project.Tracks.Single(item => item.Kind == TrackKind.Text);
        var firstVideo = new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.Zero, TimelineTime.Zero,
            TimelineTime.FromSeconds(5), null,
            new VideoParameters(0.1, 1.1, 0.9, 0, 0.4, 0.6, 1.2, 0.8, 12, 0.1, 0, 0.05, 0, 0.75), null, 0);
        var secondVideo = new MediaClip(Guid.NewGuid(), source.Id, visual.Id, TimelineTime.FromSeconds(5), TimelineTime.FromSeconds(5),
            TimelineTime.FromSeconds(5), null, new VideoParameters(), null, 0);
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
                    ,new MediaStreamDescriptor(2, MediaStreamKind.Audio, "aac", "fltp", SampleRate: 48_000, Channels: 2, Language: "jpn")
                    ,new MediaStreamDescriptor(3, MediaStreamKind.Subtitle, "ass", Language: "rus", Title: "Надписи", IsForced: true)
                    ,new MediaStreamDescriptor(4, MediaStreamKind.Subtitle, "ass", Language: "rus", Title: "Субтитры")
                    ,new MediaStreamDescriptor(5, MediaStreamKind.Attachment, "ttf", Title: "Embedded font")
                ]
            }),
            Tracks = project.Tracks.Add(subtitle2),
            MediaClips =
            [
                firstVideo,
                secondVideo,
                new MediaClip(Guid.NewGuid(), source.Id, audio.Id, TimelineTime.Zero, TimelineTime.FromSeconds(1),
                    TimelineTime.FromSeconds(10), null, null, new AudioParameters(0.8, false, -0.2), 1),
                new MediaClip(Guid.NewGuid(), source.Id, audio2.Id, TimelineTime.Zero, TimelineTime.FromSeconds(1),
                    TimelineTime.FromSeconds(10), null, null, new AudioParameters(0.7, false, 0.2), 2)
            ],
            SubtitleClips =
            [
                new SubtitleClip(Guid.NewGuid(), source.Id, 3, subtitle.Id, TimelineTime.Zero,
                    TimelineTime.Zero, TimelineTime.FromSeconds(10), PreserveAssStyling: true),
                new SubtitleClip(Guid.NewGuid(), source.Id, 4, subtitle2.Id, TimelineTime.Zero,
                    TimelineTime.Zero, TimelineTime.FromSeconds(10), PreserveAssStyling: true)
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
        Assert.True(expected.SubtitleClips.SequenceEqual(actual.SubtitleClips));
        Assert.True(expected.TextClips.SequenceEqual(actual.TextClips));
        Assert.True(expected.Transitions.SequenceEqual(actual.Transitions));
        Assert.True(expected.Markers.SequenceEqual(actual.Markers));
        Assert.Equal(expected.AiConversation.Id, actual.AiConversation.Id);
        Assert.True(expected.AiConversation.Messages.SequenceEqual(actual.AiConversation.Messages));
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
