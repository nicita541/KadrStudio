using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using KadrStudio.Application.Storage;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;
using Microsoft.Data.Sqlite;

namespace KadrStudio.Infrastructure.Storage;

public sealed class SqliteProjectStore(IProjectValidator? validator = null) : IProjectStore
{
    private const int CurrentSchemaVersion = 5;
    private const int OldestReadableSchemaVersion = 1;
    private readonly IProjectValidator _validator = validator ?? new ProjectValidator();

    public async Task SaveAsync(string path, ProjectState project, CancellationToken cancellationToken = default)
    {
        EnsureValid(project);
        var fullPath = NormalizeProjectPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var checkpoints = File.Exists(fullPath)
                ? await ReadCheckpointDocumentsAsync(fullPath, cancellationToken).ConfigureAwait(false)
                : Array.Empty<CheckpointDocument>();
            await using (var connection = await OpenAsync(temporaryPath, readOnly: false, cancellationToken).ConfigureAwait(false))
            {
                await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
                await WriteProjectAsync(connection, project, cancellationToken).ConfigureAwait(false);
                foreach (var checkpoint in checkpoints)
                    await WriteCheckpointAsync(connection, checkpoint, cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            }

            var integrity = await CheckIntegrityAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            if (!integrity.IsValid) throw new InvalidDataException($"Новый файл проекта не прошёл проверку: {integrity.Details}");
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryPath + "-wal");
            TryDelete(temporaryPath + "-shm");
        }
    }

    public async Task<ProjectState> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = NormalizeProjectPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Файл проекта не найден.", fullPath);
        await using var connection = await OpenAsync(fullPath, readOnly: true, cancellationToken).ConfigureAwait(false);
        await EnsureSupportedSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var project = await ReadProjectAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureValid(project);
        return project;
    }

    public async Task<ProjectIntegrityResult> CheckIntegrityAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(NormalizeProjectPath(path), readOnly: true, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "unknown";
            return new ProjectIntegrityResult(result.Equals("ok", StringComparison.OrdinalIgnoreCase), result);
        }
        catch (SqliteException exception)
        {
            return new ProjectIntegrityResult(false, exception.Message);
        }
    }

    public async Task<ProjectCheckpointInfo> CreateCheckpointAsync(
        string path,
        ProjectState project,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureValid(project);
        var document = new CheckpointDocument(
            Guid.NewGuid(), project.Id, DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(name) ? "Контрольная точка" : name.Trim(),
            ProjectDocumentSerializer.Serialize(project));
        await using var connection = await OpenAsync(NormalizeProjectPath(path), readOnly: false, cancellationToken).ConfigureAwait(false);
        await EnsureSupportedSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await WriteCheckpointAsync(connection, document, cancellationToken).ConfigureAwait(false);
        return document.ToInfo();
    }

    public async Task<IReadOnlyList<ProjectCheckpointInfo>> GetCheckpointsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var documents = await ReadCheckpointDocumentsAsync(NormalizeProjectPath(path), cancellationToken).ConfigureAwait(false);
        return documents.OrderByDescending(item => item.CreatedAt).Select(item => item.ToInfo()).ToArray();
    }

    public async Task<ProjectState> RestoreCheckpointAsync(
        string path,
        Guid checkpointId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(NormalizeProjectPath(path), readOnly: true, cancellationToken).ConfigureAwait(false);
        await EnsureSupportedSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM checkpoints WHERE id = $id;";
        command.Parameters.AddWithValue("$id", checkpointId.ToString("N"));
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw new KeyNotFoundException("Контрольная точка не найдена.");
        var project = ProjectDocumentSerializer.Deserialize(json);
        EnsureValid(project);
        return project;
    }

    public async Task DeleteCheckpointAsync(string path, Guid checkpointId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(NormalizeProjectPath(path), readOnly: false, cancellationToken).ConfigureAwait(false);
        await EnsureSupportedSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM checkpoints WHERE id = $id;";
        command.Parameters.AddWithValue("$id", checkpointId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteProjectAsync(SqliteConnection connection, ProjectState project, CancellationToken token)
    {
        project = PrepareForStorage(project);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = """
            INSERT INTO metadata(key, value) VALUES
                ('schema_version', $schema),
                ('project_id', $projectId),
                ('project_name', $name),
                ('updated_at', $updated)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        metadata.Parameters.AddWithValue("$schema", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        metadata.Parameters.AddWithValue("$projectId", project.Id.ToString("N"));
        metadata.Parameters.AddWithValue("$name", project.Name);
        metadata.Parameters.AddWithValue("$updated", project.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await metadata.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, """
            INSERT INTO project(
                singleton_id, id, name, canvas_width, canvas_height,
                frame_rate_numerator, frame_rate_denominator, revision,
                created_at, updated_at, in_point_ticks, out_point_ticks)
            VALUES(1, $id, $name, $width, $height, $fpsNum, $fpsDen, $revision,
                   $createdAt, $updatedAt, $inPoint, $outPoint);
            """, token,
            ("$id", project.Id.ToString("N")), ("$name", project.Name),
            ("$width", project.CanvasWidth), ("$height", project.CanvasHeight),
            ("$fpsNum", project.FrameRate.Numerator), ("$fpsDen", project.FrameRate.Denominator),
            ("$revision", project.Revision),
            ("$createdAt", project.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$updatedAt", project.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$inPoint", project.InPoint?.Ticks), ("$outPoint", project.OutPoint?.Ticks)).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, """
            INSERT INTO sequence_settings(singleton_id, audio_sample_rate)
            VALUES(1, $sampleRate);
            """, token, ("$sampleRate", project.Sequence.AudioSampleRate)).ConfigureAwait(false);

        var activeSequenceId = project.ActiveSequenceId!.Value.ToString("N");
        for (var ordinal = 0; ordinal < project.Tracks.Length; ordinal++)
        {
            var track = project.Tracks[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO tracks(id, sequence_id, track_order, kind, track_index, name, is_muted, is_locked, is_visible)
                VALUES($id, $sequenceId, $order, $kind, $index, $name, $muted, $locked, $visible);
                """, token,
                ("$id", track.Id.ToString("N")), ("$sequenceId", activeSequenceId), ("$order", ordinal),
                ("$kind", (int)track.Kind), ("$index", track.Index),
                ("$name", track.Name), ("$muted", track.IsMuted), ("$locked", track.IsLocked),
                ("$visible", track.IsVisible)).ConfigureAwait(false);
        }

        foreach (var source in project.Sources.Values)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO media_sources(
                    id, path, name, kind, duration_ticks, has_audio, width, height,
                    frame_rate_numerator, frame_rate_denominator, video_codec, audio_codec,
                    file_size, last_write_utc_ticks, fingerprint)
                VALUES($id, $path, $name, $kind, $duration, $hasAudio, $width, $height,
                       $fpsNum, $fpsDen, $videoCodec, $audioCodec, $fileSize, $lastWrite, $fingerprint);
                """, token,
                ("$id", source.Id.ToString("N")), ("$path", source.Path), ("$name", source.Name),
                ("$kind", (int)source.Kind), ("$duration", source.Duration.Ticks),
                ("$hasAudio", source.HasAudio), ("$width", source.Width), ("$height", source.Height),
                ("$fpsNum", source.FrameRate?.Numerator), ("$fpsDen", source.FrameRate?.Denominator),
                ("$videoCodec", source.VideoCodec), ("$audioCodec", source.AudioCodec),
                ("$fileSize", source.FileSize), ("$lastWrite", source.LastWriteUtcTicks),
                ("$fingerprint", source.Fingerprint)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO media_source_details(
                    source_id, previous_path, online_state, fast_fingerprint, verified_fingerprint,
                    streams_json, is_variable_frame_rate, proxy_path)
                VALUES($sourceId, $previousPath, $onlineState, $fastFingerprint, $verifiedFingerprint,
                       $streams, $vfr, $proxyPath);
                """, token,
                ("$sourceId", source.Id.ToString("N")), ("$previousPath", source.PreviousPath),
                ("$onlineState", (int)source.OnlineState), ("$fastFingerprint", source.FastFingerprint),
                ("$verifiedFingerprint", source.VerifiedFingerprint),
                ("$streams", JsonSerializer.Serialize(source.Streams.IsDefault ? [] : source.Streams)),
                ("$vfr", source.IsVariableFrameRate), ("$proxyPath", source.ProxyPath)).ConfigureAwait(false);
        }

        foreach (var sequence in project.Sequences)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO sequences(
                    id, name, revision, status, target_format, parent_sequence_id, montage_plan_id,
                    is_active, snapshot_json)
                VALUES($id, $name, $revision, $status, $target, $parent, $plan, $active, $snapshot);
                """, token,
                ("$id", sequence.Id.ToString("N")), ("$name", sequence.Name), ("$revision", sequence.Revision),
                ("$status", (int)sequence.Status), ("$target", (int)sequence.TargetFormat),
                ("$parent", sequence.ParentSequenceId?.ToString("N")),
                ("$plan", sequence.MontagePlanId?.ToString("N")),
                ("$active", sequence.Id == project.ActiveSequenceId),
                ("$snapshot", ProjectDocumentSerializer.SerializeSequence(sequence))).ConfigureAwait(false);
        }

        foreach (var annotation in project.SourceAnnotations)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO source_annotations(
                    id, source_id, kind, source_start_ticks, duration_ticks, note, created_at)
                VALUES($id, $sourceId, $kind, $start, $duration, $note, $createdAt);
                """, token,
                ("$id", annotation.Id.ToString("N")), ("$sourceId", annotation.SourceId.ToString("N")),
                ("$kind", (int)annotation.Kind), ("$start", annotation.SourceRange.Start.Ticks),
                ("$duration", annotation.SourceRange.Duration.Ticks), ("$note", annotation.Note),
                ("$createdAt", annotation.CreatedAt.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }

        foreach (var reference in project.AnalysisReferences)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO analysis_references(
                    source_id, source_fingerprint, pipeline_version, model, profile_id, profile_version, updated_at)
                VALUES($sourceId, $fingerprint, $pipeline, $model, $profile, $profileVersion, $updatedAt);
                """, token,
                ("$sourceId", reference.SourceId.ToString("N")), ("$fingerprint", reference.SourceFingerprint),
                ("$pipeline", reference.PipelineVersion), ("$model", reference.Model),
                ("$profile", reference.ProfileId), ("$profileVersion", reference.ProfileVersion),
                ("$updatedAt", reference.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }

        foreach (var plan in project.MontagePlans)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO montage_plans(id, request_id, status, target_format, created_at, updated_at, plan_json)
                VALUES($id, $requestId, $status, $target, $createdAt, $updatedAt, $json);
                """, token,
                ("$id", plan.Id.ToString("N")), ("$requestId", plan.RequestId.ToString("N")),
                ("$status", (int)plan.Status), ("$target", (int)plan.TargetFormat),
                ("$createdAt", plan.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                ("$updatedAt", plan.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)),
                ("$json", JsonSerializer.Serialize(plan))).ConfigureAwait(false);
            foreach (var item in plan.Items)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO montage_plan_items(plan_id, item_id, item_order, item_json)
                    VALUES($planId, $itemId, $order, $json);
                    """, token,
                    ("$planId", plan.Id.ToString("N")), ("$itemId", item.Id.ToString("N")),
                    ("$order", item.Order), ("$json", JsonSerializer.Serialize(item))).ConfigureAwait(false);
            }
        }

        var conversation = project.AiConversation;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO ai_conversation(singleton_id, id, created_at, updated_at)
            VALUES(1, $id, $createdAt, $updatedAt);
            """, token,
            ("$id", conversation.Id.ToString("N")),
            ("$createdAt", conversation.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$updatedAt", conversation.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        for (var ordinal = 0; ordinal < conversation.Messages.Length; ordinal++)
        {
            var message = conversation.Messages[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO ai_chat_messages(
                    id, conversation_id, message_order, role, kind, operation_state, created_at, message_json)
                VALUES($id, $conversationId, $order, $role, $kind, $state, $createdAt, $json);
                """, token,
                ("$id", message.Id.ToString("N")), ("$conversationId", conversation.Id.ToString("N")),
                ("$order", ordinal), ("$role", (int)message.Role), ("$kind", (int)message.Kind),
                ("$state", (int)message.OperationState),
                ("$createdAt", message.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                ("$json", JsonSerializer.Serialize(message))).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < project.MediaClips.Length; ordinal++)
        {
            var clip = project.MediaClips[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO media_clips(
                    id, sequence_id, clip_order, source_id, track_id, start_ticks, source_in_ticks, duration_ticks, link_group_id,
                    brightness, contrast, saturation, temperature,
                    volume, is_muted, pan, fade_in_ticks, fade_out_ticks, bass, mid, treble)
                VALUES($id, $sequenceId, $order, $sourceId, $trackId, $start, $sourceIn, $duration, $linkGroup,
                       $brightness, $contrast, $saturation, $temperature,
                       $volume, $muted, $pan, $fadeIn, $fadeOut, $bass, $mid, $treble);
                """, token,
                ("$id", clip.Id.ToString("N")), ("$sequenceId", activeSequenceId),
                ("$order", ordinal), ("$sourceId", clip.SourceId.ToString("N")),
                ("$trackId", clip.TrackId.ToString("N")), ("$start", clip.Start.Ticks),
                ("$sourceIn", clip.SourceIn.Ticks), ("$duration", clip.Duration.Ticks),
                ("$linkGroup", clip.LinkGroupId?.ToString("N")),
                ("$brightness", clip.Video?.Brightness), ("$contrast", clip.Video?.Contrast),
                ("$saturation", clip.Video?.Saturation), ("$temperature", clip.Video?.Temperature),
                ("$volume", clip.Audio?.Volume), ("$muted", clip.Audio?.IsMuted),
                ("$pan", clip.Audio?.Pan), ("$fadeIn", clip.Audio?.FadeIn.Ticks),
                ("$fadeOut", clip.Audio?.FadeOut.Ticks), ("$bass", clip.Audio?.Bass),
                ("$mid", clip.Audio?.Mid), ("$treble", clip.Audio?.Treble)).ConfigureAwait(false);
            if (clip.Video is { } video)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO video_clip_details(
                        clip_id, position_x, position_y, scale_x, scale_y, rotation,
                        crop_left, crop_top, crop_right, crop_bottom, opacity)
                    VALUES($clipId, $x, $y, $scaleX, $scaleY, $rotation,
                           $cropLeft, $cropTop, $cropRight, $cropBottom, $opacity);
                    """, token,
                    ("$clipId", clip.Id.ToString("N")), ("$x", video.PositionX), ("$y", video.PositionY),
                    ("$scaleX", video.ScaleX), ("$scaleY", video.ScaleY), ("$rotation", video.Rotation),
                    ("$cropLeft", video.CropLeft), ("$cropTop", video.CropTop),
                    ("$cropRight", video.CropRight), ("$cropBottom", video.CropBottom),
                    ("$opacity", video.Opacity)).ConfigureAwait(false);
            }
        }

        for (var ordinal = 0; ordinal < project.TextClips.Length; ordinal++)
        {
            var clip = project.TextClips[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO text_clips(
                    id, sequence_id, clip_order, track_id, start_ticks, duration_ticks, text, font_family, font_size, color,
                    x, y, rotation, box_width, box_height, is_subtitle)
                VALUES($id, $sequenceId, $order, $trackId, $start, $duration, $text, $font, $fontSize, $color,
                       $x, $y, $rotation, $boxWidth, $boxHeight, $subtitle);
                """, token,
                ("$id", clip.Id.ToString("N")), ("$sequenceId", activeSequenceId),
                ("$order", ordinal), ("$trackId", clip.TrackId.ToString("N")),
                ("$start", clip.Start.Ticks), ("$duration", clip.Duration.Ticks), ("$text", clip.Text),
                ("$font", clip.Style.FontFamily), ("$fontSize", clip.Style.FontSize), ("$color", clip.Style.Color),
                ("$x", clip.Style.X), ("$y", clip.Style.Y), ("$rotation", clip.Style.Rotation),
                ("$boxWidth", clip.Style.BoxWidth), ("$boxHeight", clip.Style.BoxHeight),
                ("$subtitle", clip.Style.IsSubtitle)).ConfigureAwait(false);
        }

        for (var ordinal = 0; ordinal < project.Markers.Length; ordinal++)
        {
            var marker = project.Markers[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO markers(
                    id, sequence_id, marker_order, kind, start_ticks, duration_ticks, title, description, source_id,
                    source_start_ticks, confidence, query)
                VALUES($id, $sequenceId, $order, $kind, $start, $duration, $title, $description, $sourceId,
                       $sourceStart, $confidence, $query);
                """, token,
                ("$id", marker.Id.ToString("N")), ("$sequenceId", activeSequenceId),
                ("$order", ordinal), ("$kind", (int)marker.Kind),
                ("$start", marker.Start.Ticks), ("$duration", marker.Duration.Ticks),
                ("$title", marker.Title), ("$description", marker.Description),
                ("$sourceId", marker.SourceId?.ToString("N")), ("$sourceStart", marker.SourceStart.Ticks),
                ("$confidence", marker.Confidence), ("$query", marker.Query)).ConfigureAwait(false);
        }
        for (var ordinal = 0; ordinal < project.Transitions.Length; ordinal++)
        {
            var transition = project.Transitions[ordinal];
            await ExecuteAsync(connection, transaction, """
                INSERT INTO transitions(
                    id, sequence_id, transition_order, kind, track_id, from_clip_id, to_clip_id, start_ticks, duration_ticks)
                VALUES($id, $sequenceId, $order, $kind, $trackId, $fromClipId, $toClipId, $start, $duration);
                """, token,
                ("$id", transition.Id.ToString("N")), ("$sequenceId", activeSequenceId),
                ("$order", ordinal), ("$kind", (int)transition.Kind),
                ("$trackId", transition.TrackId.ToString("N")),
                ("$fromClipId", transition.FromClipId.ToString("N")),
                ("$toClipId", transition.ToClipId.ToString("N")),
                ("$start", transition.Start.Ticks), ("$duration", transition.Duration.Ticks)).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task<ProjectState> ReadProjectAsync(SqliteConnection connection, CancellationToken token)
    {
        Guid projectId;
        string name;
        int canvasWidth;
        int canvasHeight;
        FrameRate frameRate;
        long revision;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        TimelineTime? inPoint;
        TimelineTime? outPoint;
        var audioSampleRate = 48_000;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name, canvas_width, canvas_height,
                       frame_rate_numerator, frame_rate_denominator, revision,
                       created_at, updated_at, in_point_ticks, out_point_ticks
                FROM project WHERE singleton_id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
                throw new InvalidDataException("The project database does not contain a project record.");
            projectId = ReadGuid(reader, 0);
            name = reader.GetString(1);
            canvasWidth = reader.GetInt32(2);
            canvasHeight = reader.GetInt32(3);
            frameRate = new FrameRate(reader.GetInt32(4), reader.GetInt32(5));
            revision = reader.GetInt64(6);
            createdAt = ReadDateTimeOffset(reader, 7);
            updatedAt = ReadDateTimeOffset(reader, 8);
            inPoint = ReadNullableTime(reader, 9);
            outPoint = ReadNullableTime(reader, 10);
        }

        if (await HasTableAsync(connection, "sequence_settings", token).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT audio_sample_rate FROM sequence_settings WHERE singleton_id = 1;";
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (value is not null) audioSampleRate = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        var tracks = ImmutableArray.CreateBuilder<TimelineTrack>();
        await using (var command = connection.CreateCommand())
        {
            var hasStoredOrder = await HasColumnAsync(connection, "tracks", "track_order", token).ConfigureAwait(false);
            command.CommandText = hasStoredOrder ? """
                SELECT id, kind, track_index, name, is_muted, is_locked, is_visible
                FROM tracks ORDER BY track_order;
                """ : """
                SELECT id, kind, track_index, name, is_muted, is_locked, is_visible
                FROM tracks ORDER BY kind, track_index;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                tracks.Add(new TimelineTrack(
                    ReadGuid(reader, 0),
                    (TrackKind)reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    ReadBoolean(reader, 4),
                    ReadBoolean(reader, 5),
                    ReadBoolean(reader, 6)));
            }
        }

        var sources = ImmutableDictionary.CreateBuilder<Guid, MediaSource>();
        var hasSourceDetails = await HasTableAsync(connection, "media_source_details", token).ConfigureAwait(false);
        var sourceDetails = new Dictionary<Guid, MediaSourceDetails>();
        if (hasSourceDetails)
        {
            await using var detailCommand = connection.CreateCommand();
            detailCommand.CommandText = """
                SELECT source_id, previous_path, online_state, fast_fingerprint, verified_fingerprint,
                       streams_json, is_variable_frame_rate, proxy_path
                FROM media_source_details;
                """;
            await using var detailReader = await detailCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await detailReader.ReadAsync(token).ConfigureAwait(false))
                sourceDetails.Add(ReadGuid(detailReader, 0), new MediaSourceDetails(
                    detailReader.GetString(1), (MediaOnlineState)detailReader.GetInt32(2),
                    detailReader.GetString(3), detailReader.GetString(4), detailReader.GetString(5),
                    ReadBoolean(detailReader, 6), detailReader.GetString(7)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, path, name, kind, duration_ticks, has_audio, width, height,
                       frame_rate_numerator, frame_rate_denominator, video_codec, audio_codec,
                       file_size, last_write_utc_ticks, fingerprint
                FROM media_sources ORDER BY id;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var id = ReadGuid(reader, 0);
                var sourceFrameRate = reader.IsDBNull(8)
                    ? (FrameRate?)null
                    : new FrameRate(reader.GetInt32(8), reader.GetInt32(9));
                var source = new MediaSource(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    (MediaKind)reader.GetInt32(3),
                    new TimelineTime(reader.GetInt64(4)),
                    ReadBoolean(reader, 5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    sourceFrameRate,
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetInt64(12),
                    reader.GetInt64(13),
                    reader.GetString(14));
                if (sourceDetails.TryGetValue(id, out var details))
                {
                    source = source with
                    {
                        PreviousPath = details.PreviousPath,
                        OnlineState = details.OnlineState,
                        FastFingerprint = details.FastFingerprint,
                        VerifiedFingerprint = details.VerifiedFingerprint,
                        Streams = JsonSerializer.Deserialize<ImmutableArray<MediaStreamDescriptor>>(details.StreamsJson),
                        IsVariableFrameRate = details.IsVariableFrameRate,
                        ProxyPath = details.ProxyPath
                    };
                }
                sources.Add(id, source);
            }
        }

        var mediaClips = ImmutableArray.CreateBuilder<MediaClip>();
        var hasVideoDetails = await HasTableAsync(connection, "video_clip_details", token).ConfigureAwait(false);
        var videoDetails = new Dictionary<Guid, VideoClipDetails>();
        if (hasVideoDetails)
        {
            await using var detailCommand = connection.CreateCommand();
            detailCommand.CommandText = """
                SELECT clip_id, position_x, position_y, scale_x, scale_y, rotation,
                       crop_left, crop_top, crop_right, crop_bottom, opacity
                FROM video_clip_details;
                """;
            await using var detailReader = await detailCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await detailReader.ReadAsync(token).ConfigureAwait(false))
                videoDetails.Add(ReadGuid(detailReader, 0), new VideoClipDetails(
                    detailReader.GetDouble(1), detailReader.GetDouble(2), detailReader.GetDouble(3),
                    detailReader.GetDouble(4), detailReader.GetDouble(5), detailReader.GetDouble(6),
                    detailReader.GetDouble(7), detailReader.GetDouble(8), detailReader.GetDouble(9),
                    detailReader.GetDouble(10)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, source_id, track_id, start_ticks, source_in_ticks, duration_ticks, link_group_id,
                       brightness, contrast, saturation, temperature,
                       volume, is_muted, pan, fade_in_ticks, fade_out_ticks, bass, mid, treble
                FROM media_clips ORDER BY clip_order;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var video = reader.IsDBNull(7)
                    ? null
                    : new VideoParameters(reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9), reader.GetDouble(10));
                var clipId = ReadGuid(reader, 0);
                if (video is not null && videoDetails.TryGetValue(clipId, out var details))
                {
                    video = video with
                    {
                        PositionX = details.PositionX, PositionY = details.PositionY,
                        ScaleX = details.ScaleX, ScaleY = details.ScaleY, Rotation = details.Rotation,
                        CropLeft = details.CropLeft, CropTop = details.CropTop,
                        CropRight = details.CropRight, CropBottom = details.CropBottom,
                        Opacity = details.Opacity
                    };
                }
                var audio = reader.IsDBNull(11)
                    ? null
                    : new AudioParameters(
                        reader.GetDouble(11), ReadBoolean(reader, 12), reader.GetDouble(13),
                        new TimelineTime(reader.GetInt64(14)), new TimelineTime(reader.GetInt64(15)),
                        reader.GetDouble(16), reader.GetDouble(17), reader.GetDouble(18));
                mediaClips.Add(new MediaClip(
                    clipId,
                    ReadGuid(reader, 1),
                    ReadGuid(reader, 2),
                    new TimelineTime(reader.GetInt64(3)),
                    new TimelineTime(reader.GetInt64(4)),
                    new TimelineTime(reader.GetInt64(5)),
                    ReadNullableGuid(reader, 6),
                    video,
                    audio));
            }
        }

        var textClips = ImmutableArray.CreateBuilder<TextClip>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, track_id, start_ticks, duration_ticks, text, font_family, font_size, color,
                       x, y, rotation, box_width, box_height, is_subtitle
                FROM text_clips ORDER BY clip_order;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var style = new TextStyle(
                    reader.GetString(5), reader.GetDouble(6), reader.GetString(7),
                    reader.GetDouble(8), reader.GetDouble(9), reader.GetDouble(10),
                    reader.GetDouble(11), reader.GetDouble(12), ReadBoolean(reader, 13));
                textClips.Add(new TextClip(
                    ReadGuid(reader, 0), ReadGuid(reader, 1),
                    new TimelineTime(reader.GetInt64(2)), new TimelineTime(reader.GetInt64(3)),
                    reader.GetString(4), style));
            }
        }

        var markers = ImmutableArray.CreateBuilder<TimelineMarker>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, kind, start_ticks, duration_ticks, title, description, source_id,
                       source_start_ticks, confidence, query
                FROM markers ORDER BY marker_order;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                markers.Add(new TimelineMarker(
                    ReadGuid(reader, 0), (MarkerKind)reader.GetInt32(1),
                    new TimelineTime(reader.GetInt64(2)), new TimelineTime(reader.GetInt64(3)),
                    reader.GetString(4), reader.GetString(5), ReadNullableGuid(reader, 6),
                    new TimelineTime(reader.GetInt64(7)), reader.GetDouble(8), reader.GetString(9)));
            }
        }

        var transitions = ImmutableArray.CreateBuilder<TimelineTransition>();
        if (await HasTableAsync(connection, "transitions", token).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, track_id, from_clip_id, to_clip_id, start_ticks, duration_ticks
                FROM transitions ORDER BY transition_order;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                transitions.Add(new TimelineTransition(
                    ReadGuid(reader, 0), (TransitionKind)reader.GetInt32(1), ReadGuid(reader, 2),
                    ReadGuid(reader, 3), ReadGuid(reader, 4), new TimelineTime(reader.GetInt64(5)),
                    new TimelineTime(reader.GetInt64(6))));
        }

        var project = new ProjectState
        {
            Id = projectId,
            Name = name,
            Sequence = new SequenceSettings(canvasWidth, canvasHeight, frameRate, audioSampleRate),
            Revision = revision,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Tracks = tracks.ToImmutable(),
            Sources = sources.ToImmutable(),
            MediaClips = mediaClips.ToImmutable(),
            TextClips = textClips.ToImmutable(),
            Transitions = transitions.ToImmutable(),
            Markers = markers.ToImmutable(),
            InPoint = inPoint,
            OutPoint = outPoint
        };
        return await ReadAiWorkspaceAsync(connection, project, token).ConfigureAwait(false);
    }

    private static async Task<ProjectState> ReadAiWorkspaceAsync(
        SqliteConnection connection,
        ProjectState project,
        CancellationToken token)
    {
        var plans = ImmutableArray.CreateBuilder<MontagePlan>();
        if (await HasTableAsync(connection, "montage_plans", token).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT plan_json FROM montage_plans ORDER BY created_at;";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var plan = JsonSerializer.Deserialize<MontagePlan>(reader.GetString(0))
                    ?? throw new InvalidDataException("Сохранённый план ИИ-монтажа повреждён.");
                plans.Add(plan);
            }
        }

        var sequences = ImmutableArray.CreateBuilder<SequenceState>();
        Guid? activeSequenceId = null;
        if (await HasTableAsync(connection, "sequences", token).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, is_active, snapshot_json FROM sequences ORDER BY rowid;";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var sequence = ProjectDocumentSerializer.DeserializeSequence(reader.GetString(2));
                if (sequence.Id != ReadGuid(reader, 0))
                    throw new InvalidDataException("ID варианта монтажа не совпадает с его снимком.");
                sequences.Add(sequence);
                if (ReadBoolean(reader, 1)) activeSequenceId = sequence.Id;
            }
        }

        if (sequences.Count == 0)
        {
            activeSequenceId = project.Id;
            sequences.Add(SequenceState.Capture(project, project.Id, "Исходный монтаж"));
        }

        var annotations = ImmutableArray.CreateBuilder<SourceAnnotation>();
        if (await HasTableAsync(connection, "source_annotations", token).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, source_id, kind, source_start_ticks, duration_ticks, note, created_at
                FROM source_annotations ORDER BY created_at, id;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                annotations.Add(new SourceAnnotation(
                    ReadGuid(reader, 0), ReadGuid(reader, 1), (SourceAnnotationKind)reader.GetInt32(2),
                    new TimeRange(new TimelineTime(reader.GetInt64(3)), new TimelineTime(reader.GetInt64(4))),
                    reader.GetString(5), ReadDateTimeOffset(reader, 6)));
        }

        var references = ImmutableArray.CreateBuilder<MediaAnalysisReference>();
        if (await HasTableAsync(connection, "analysis_references", token).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_id, source_fingerprint, pipeline_version, model, profile_id, profile_version, updated_at
                FROM analysis_references ORDER BY source_id, updated_at;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                references.Add(new MediaAnalysisReference(
                    ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt32(5), ReadDateTimeOffset(reader, 6)));
        }

        var conversation = AiConversation.Create();
        if (await HasTableAsync(connection, "ai_conversation", token).ConfigureAwait(false))
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, created_at, updated_at FROM ai_conversation WHERE singleton_id = 1;";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                    conversation = new AiConversation(
                        ReadGuid(reader, 0), ReadDateTimeOffset(reader, 1), ReadDateTimeOffset(reader, 2), []);
            }
            if (await HasTableAsync(connection, "ai_chat_messages", token).ConfigureAwait(false))
            {
                var messages = ImmutableArray.CreateBuilder<AiChatMessage>();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT message_json FROM ai_chat_messages ORDER BY message_order;";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var message = JsonSerializer.Deserialize<AiChatMessage>(reader.GetString(0))
                        ?? throw new InvalidDataException("Сообщение диалога ИИ повреждено.");
                    messages.Add(message);
                }
                conversation = conversation with { Messages = messages.ToImmutable() };
            }
        }
        conversation = conversation.RecoverInterruptedOperations();

        activeSequenceId ??= sequences[0].Id;
        var active = sequences.FirstOrDefault(item => item.Id == activeSequenceId.Value);
        if (active is null)
            throw new InvalidDataException("Активный вариант монтажа отсутствует.");

        // Relational timeline tables contain the active sequence for fast loading.
        // Its snapshot is refreshed from those authoritative rows so both representations stay identical.
        var synchronizedActive = active.CaptureTimeline(project, incrementRevision: false);
        return project with
        {
            Sequences = sequences.Select(item => item.Id == synchronizedActive.Id ? synchronizedActive : item).ToImmutableArray(),
            ActiveSequenceId = synchronizedActive.Id,
            SourceAnnotations = annotations.ToImmutable(),
            AnalysisReferences = references.ToImmutable(),
            MontagePlans = plans.ToImmutable(),
            AiConversation = conversation
        };
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        const string sql = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS metadata(
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS project(
                singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
                id TEXT NOT NULL UNIQUE CHECK(length(id) = 32),
                name TEXT NOT NULL CHECK(length(name) > 0),
                canvas_width INTEGER NOT NULL CHECK(canvas_width BETWEEN 320 AND 7680),
                canvas_height INTEGER NOT NULL CHECK(canvas_height BETWEEN 240 AND 4320),
                frame_rate_numerator INTEGER NOT NULL CHECK(frame_rate_numerator > 0),
                frame_rate_denominator INTEGER NOT NULL CHECK(frame_rate_denominator > 0),
                revision INTEGER NOT NULL CHECK(revision >= 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                in_point_ticks INTEGER NULL CHECK(in_point_ticks IS NULL OR in_point_ticks >= 0),
                out_point_ticks INTEGER NULL CHECK(out_point_ticks IS NULL OR out_point_ticks >= 0),
                CHECK(in_point_ticks IS NULL OR out_point_ticks IS NULL OR out_point_ticks > in_point_ticks)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS sequence_settings(
                singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
                audio_sample_rate INTEGER NOT NULL CHECK(audio_sample_rate BETWEEN 8000 AND 192000)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS sequences(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                name TEXT NOT NULL CHECK(length(name) > 0),
                revision INTEGER NOT NULL CHECK(revision >= 0),
                status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 2),
                target_format INTEGER NOT NULL CHECK(target_format BETWEEN 0 AND 2),
                parent_sequence_id TEXT NULL CHECK(parent_sequence_id IS NULL OR length(parent_sequence_id) = 32),
                montage_plan_id TEXT NULL CHECK(montage_plan_id IS NULL OR length(montage_plan_id) = 32),
                is_active INTEGER NOT NULL CHECK(is_active IN (0,1)),
                snapshot_json TEXT NOT NULL CHECK(length(snapshot_json) > 2)
            ) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_sequences_active ON sequences(is_active) WHERE is_active = 1;
            CREATE TABLE IF NOT EXISTS tracks(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                sequence_id TEXT NOT NULL CHECK(length(sequence_id) = 32),
                track_order INTEGER NOT NULL CHECK(track_order >= 0),
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
                track_index INTEGER NOT NULL CHECK(track_index >= 0),
                name TEXT NOT NULL CHECK(length(name) > 0),
                is_muted INTEGER NOT NULL CHECK(is_muted IN (0,1)),
                is_locked INTEGER NOT NULL CHECK(is_locked IN (0,1)),
                is_visible INTEGER NOT NULL CHECK(is_visible IN (0,1)),
                UNIQUE(sequence_id, track_order),
                UNIQUE(sequence_id, kind, track_index)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS media_sources(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                path TEXT NOT NULL CHECK(length(path) > 0),
                name TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
                duration_ticks INTEGER NOT NULL CHECK(duration_ticks > 0),
                has_audio INTEGER NOT NULL CHECK(has_audio IN (0,1)),
                width INTEGER NOT NULL CHECK(width >= 0),
                height INTEGER NOT NULL CHECK(height >= 0),
                frame_rate_numerator INTEGER NULL CHECK(frame_rate_numerator IS NULL OR frame_rate_numerator > 0),
                frame_rate_denominator INTEGER NULL CHECK(frame_rate_denominator IS NULL OR frame_rate_denominator > 0),
                video_codec TEXT NOT NULL,
                audio_codec TEXT NOT NULL,
                file_size INTEGER NOT NULL CHECK(file_size >= 0),
                last_write_utc_ticks INTEGER NOT NULL,
                fingerprint TEXT NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS media_source_details(
                source_id TEXT PRIMARY KEY REFERENCES media_sources(id) ON DELETE CASCADE,
                previous_path TEXT NOT NULL,
                online_state INTEGER NOT NULL CHECK(online_state BETWEEN 0 AND 3),
                fast_fingerprint TEXT NOT NULL,
                verified_fingerprint TEXT NOT NULL,
                streams_json TEXT NOT NULL,
                is_variable_frame_rate INTEGER NOT NULL CHECK(is_variable_frame_rate IN (0,1)),
                proxy_path TEXT NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS media_clips(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                sequence_id TEXT NOT NULL CHECK(length(sequence_id) = 32),
                clip_order INTEGER NOT NULL CHECK(clip_order >= 0),
                source_id TEXT NOT NULL REFERENCES media_sources(id) ON DELETE RESTRICT,
                track_id TEXT NOT NULL REFERENCES tracks(id) ON DELETE RESTRICT,
                start_ticks INTEGER NOT NULL CHECK(start_ticks >= 0),
                source_in_ticks INTEGER NOT NULL CHECK(source_in_ticks >= 0),
                duration_ticks INTEGER NOT NULL CHECK(duration_ticks > 0),
                link_group_id TEXT NULL CHECK(link_group_id IS NULL OR length(link_group_id) = 32),
                brightness REAL NULL CHECK(brightness IS NULL OR brightness BETWEEN -1 AND 1),
                contrast REAL NULL CHECK(contrast IS NULL OR contrast BETWEEN 0 AND 3),
                saturation REAL NULL CHECK(saturation IS NULL OR saturation BETWEEN 0 AND 3),
                temperature REAL NULL CHECK(temperature IS NULL OR temperature BETWEEN -1 AND 1),
                volume REAL NULL CHECK(volume IS NULL OR volume BETWEEN 0 AND 2),
                is_muted INTEGER NULL CHECK(is_muted IS NULL OR is_muted IN (0,1)),
                pan REAL NULL CHECK(pan IS NULL OR pan BETWEEN -1 AND 1),
                fade_in_ticks INTEGER NULL CHECK(fade_in_ticks IS NULL OR fade_in_ticks >= 0),
                fade_out_ticks INTEGER NULL CHECK(fade_out_ticks IS NULL OR fade_out_ticks >= 0),
                bass REAL NULL CHECK(bass IS NULL OR bass BETWEEN -20 AND 20),
                mid REAL NULL CHECK(mid IS NULL OR mid BETWEEN -20 AND 20),
                treble REAL NULL CHECK(treble IS NULL OR treble BETWEEN -20 AND 20)
            ) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_media_clips_sequence_order ON media_clips(sequence_id, clip_order);
            CREATE INDEX IF NOT EXISTS ix_media_clips_track_time ON media_clips(track_id, start_ticks, duration_ticks);
            CREATE INDEX IF NOT EXISTS ix_media_clips_source ON media_clips(source_id);
            CREATE INDEX IF NOT EXISTS ix_media_clips_link ON media_clips(link_group_id) WHERE link_group_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS video_clip_details(
                clip_id TEXT PRIMARY KEY REFERENCES media_clips(id) ON DELETE CASCADE,
                position_x REAL NOT NULL CHECK(position_x BETWEEN -5 AND 5),
                position_y REAL NOT NULL CHECK(position_y BETWEEN -5 AND 5),
                scale_x REAL NOT NULL CHECK(scale_x > 0 AND scale_x <= 100),
                scale_y REAL NOT NULL CHECK(scale_y > 0 AND scale_y <= 100),
                rotation REAL NOT NULL CHECK(rotation BETWEEN -360 AND 360),
                crop_left REAL NOT NULL CHECK(crop_left BETWEEN 0 AND 1),
                crop_top REAL NOT NULL CHECK(crop_top BETWEEN 0 AND 1),
                crop_right REAL NOT NULL CHECK(crop_right BETWEEN 0 AND 1),
                crop_bottom REAL NOT NULL CHECK(crop_bottom BETWEEN 0 AND 1),
                opacity REAL NOT NULL CHECK(opacity BETWEEN 0 AND 1),
                CHECK(crop_left + crop_right < 1),
                CHECK(crop_top + crop_bottom < 1)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS text_clips(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                sequence_id TEXT NOT NULL CHECK(length(sequence_id) = 32),
                clip_order INTEGER NOT NULL CHECK(clip_order >= 0),
                track_id TEXT NOT NULL REFERENCES tracks(id) ON DELETE RESTRICT,
                start_ticks INTEGER NOT NULL CHECK(start_ticks >= 0),
                duration_ticks INTEGER NOT NULL CHECK(duration_ticks > 0),
                text TEXT NOT NULL CHECK(length(text) > 0),
                font_family TEXT NOT NULL CHECK(length(font_family) > 0),
                font_size REAL NOT NULL CHECK(font_size BETWEEN 4 AND 500),
                color TEXT NOT NULL,
                x REAL NOT NULL CHECK(x BETWEEN 0 AND 1),
                y REAL NOT NULL CHECK(y BETWEEN 0 AND 1),
                rotation REAL NOT NULL CHECK(rotation BETWEEN -360 AND 360),
                box_width REAL NOT NULL CHECK(box_width > 0 AND box_width <= 1),
                box_height REAL NOT NULL CHECK(box_height > 0 AND box_height <= 1),
                is_subtitle INTEGER NOT NULL CHECK(is_subtitle IN (0,1))
            ) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_text_clips_sequence_order ON text_clips(sequence_id, clip_order);
            CREATE INDEX IF NOT EXISTS ix_text_clips_track_time ON text_clips(track_id, start_ticks, duration_ticks);
            CREATE TABLE IF NOT EXISTS markers(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                sequence_id TEXT NOT NULL CHECK(length(sequence_id) = 32),
                marker_order INTEGER NOT NULL CHECK(marker_order >= 0),
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 9),
                start_ticks INTEGER NOT NULL CHECK(start_ticks >= 0),
                duration_ticks INTEGER NOT NULL CHECK(duration_ticks > 0),
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                source_id TEXT NULL REFERENCES media_sources(id) ON DELETE SET NULL,
                source_start_ticks INTEGER NOT NULL CHECK(source_start_ticks >= 0),
                confidence REAL NOT NULL CHECK(confidence BETWEEN 0 AND 1),
                query TEXT NOT NULL
            ) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_markers_sequence_order ON markers(sequence_id, marker_order);
            CREATE INDEX IF NOT EXISTS ix_markers_time ON markers(start_ticks, duration_ticks);
            CREATE TABLE IF NOT EXISTS transitions(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                sequence_id TEXT NOT NULL CHECK(length(sequence_id) = 32),
                transition_order INTEGER NOT NULL CHECK(transition_order >= 0),
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 5),
                track_id TEXT NOT NULL REFERENCES tracks(id) ON DELETE RESTRICT,
                from_clip_id TEXT NOT NULL REFERENCES media_clips(id) ON DELETE CASCADE,
                to_clip_id TEXT NOT NULL REFERENCES media_clips(id) ON DELETE CASCADE,
                start_ticks INTEGER NOT NULL CHECK(start_ticks >= 0),
                duration_ticks INTEGER NOT NULL CHECK(duration_ticks > 0),
                CHECK(from_clip_id <> to_clip_id)
            ) STRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_transitions_sequence_order ON transitions(sequence_id, transition_order);
            CREATE INDEX IF NOT EXISTS ix_transitions_track_time ON transitions(track_id, start_ticks, duration_ticks);
            CREATE TABLE IF NOT EXISTS source_annotations(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                source_id TEXT NOT NULL REFERENCES media_sources(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
                source_start_ticks INTEGER NOT NULL CHECK(source_start_ticks >= 0),
                duration_ticks INTEGER NOT NULL CHECK(duration_ticks > 0),
                note TEXT NOT NULL,
                created_at TEXT NOT NULL
            ) STRICT;
            CREATE INDEX IF NOT EXISTS ix_source_annotations_source ON source_annotations(source_id, source_start_ticks);
            CREATE TABLE IF NOT EXISTS analysis_references(
                source_id TEXT NOT NULL REFERENCES media_sources(id) ON DELETE CASCADE,
                source_fingerprint TEXT NOT NULL,
                pipeline_version TEXT NOT NULL,
                model TEXT NOT NULL,
                profile_id TEXT NOT NULL,
                profile_version INTEGER NOT NULL CHECK(profile_version > 0),
                updated_at TEXT NOT NULL,
                PRIMARY KEY(source_id, pipeline_version, model, profile_id, profile_version)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS montage_plans(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                request_id TEXT NOT NULL CHECK(length(request_id) = 32),
                status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 4),
                target_format INTEGER NOT NULL CHECK(target_format BETWEEN 0 AND 2),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                plan_json TEXT NOT NULL CHECK(length(plan_json) > 2)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS montage_plan_items(
                plan_id TEXT NOT NULL REFERENCES montage_plans(id) ON DELETE CASCADE,
                item_id TEXT NOT NULL CHECK(length(item_id) = 32),
                item_order INTEGER NOT NULL CHECK(item_order >= 0),
                item_json TEXT NOT NULL CHECK(length(item_json) > 2),
                PRIMARY KEY(plan_id, item_id),
                UNIQUE(plan_id, item_order)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS ai_conversation(
                singleton_id INTEGER PRIMARY KEY CHECK(singleton_id = 1),
                id TEXT NOT NULL UNIQUE CHECK(length(id) = 32),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS ai_chat_messages(
                id TEXT PRIMARY KEY CHECK(length(id) = 32),
                conversation_id TEXT NOT NULL REFERENCES ai_conversation(id) ON DELETE CASCADE,
                message_order INTEGER NOT NULL CHECK(message_order >= 0),
                role INTEGER NOT NULL CHECK(role BETWEEN 0 AND 1),
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 5),
                operation_state INTEGER NOT NULL CHECK(operation_state BETWEEN 0 AND 4),
                created_at TEXT NOT NULL,
                message_json TEXT NOT NULL CHECK(length(message_json) > 2),
                UNIQUE(conversation_id, message_order)
            ) STRICT;
            CREATE TABLE IF NOT EXISTS checkpoints(
                id TEXT PRIMARY KEY NOT NULL CHECK(length(id) = 32),
                project_id TEXT NOT NULL CHECK(length(project_id) = 32),
                created_at TEXT NOT NULL,
                name TEXT NOT NULL CHECK(length(name) > 0),
                snapshot_json TEXT NOT NULL CHECK(length(snapshot_json) > 2)
            ) STRICT;
            CREATE INDEX IF NOT EXISTS ix_checkpoints_project_created ON checkpoints(project_id, created_at DESC);
            """;
        await ExecuteNonQueryAsync(connection, sql, token).ConfigureAwait(false);
    }

    private static async Task EnsureSupportedSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        string? value;
        try
        {
            value = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                "Этот .kadr использует старый JSON-формат и не поддерживается новым ядром. Создайте новый проект.", exception);
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            throw new InvalidDataException("Файл не является проектом Kadr Studio SQLite.");
        if (version > CurrentSchemaVersion)
            throw new InvalidDataException("Проект создан более новой версией Kadr Studio.");
        if (version < OldestReadableSchemaVersion)
            throw new InvalidDataException($"Для схемы проекта {version} отсутствует миграция до {CurrentSchemaVersion}.");
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<CheckpointDocument[]> ReadCheckpointDocumentsAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return [];
        await using var connection = await OpenAsync(path, readOnly: true, token).ConfigureAwait(false);
        await EnsureSupportedSchemaAsync(connection, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project_id, created_at, name, snapshot_json FROM checkpoints ORDER BY created_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var documents = new List<CheckpointDocument>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            documents.Add(new CheckpointDocument(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return documents.ToArray();
    }

    private static async Task WriteCheckpointAsync(SqliteConnection connection, CheckpointDocument document, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checkpoints(id, project_id, created_at, name, snapshot_json)
            VALUES($id, $projectId, $createdAt, $name, $snapshot)
            ON CONFLICT(id) DO UPDATE SET
                project_id = excluded.project_id,
                created_at = excluded.created_at,
                name = excluded.name,
                snapshot_json = excluded.snapshot_json;
            """;
        command.Parameters.AddWithValue("$id", document.Id.ToString("N"));
        command.Parameters.AddWithValue("$projectId", document.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$createdAt", document.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$name", document.Name);
        command.Parameters.AddWithValue("$snapshot", document.Snapshot);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly, CancellationToken token)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(token).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", token).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            var value = parameter.Value switch
            {
                null => DBNull.Value,
                bool boolean => boolean ? 1 : 0,
                _ => parameter.Value
            };
            command.Parameters.AddWithValue(parameter.Name, value);
        }
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.ParseExact(reader.GetString(ordinal), "N");

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static TimelineTime? ReadNullableTime(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : new TimelineTime(reader.GetInt64(ordinal));

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal)
        => reader.GetInt64(ordinal) != 0;

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static ProjectState PrepareForStorage(ProjectState project)
    {
        if (project.Sequences.IsDefaultOrEmpty)
        {
            var sequence = SequenceState.Capture(project, project.Id, "Исходный монтаж");
            return project with { Sequences = [sequence], ActiveSequenceId = sequence.Id };
        }
        if (project.ActiveSequence is null)
            throw new InvalidDataException("В проекте не выбран активный вариант монтажа.");
        return project.SynchronizeActiveSequence(incrementRevision: false);
    }

    private void EnsureValid(ProjectState project)
    {
        var result = _validator.Validate(project);
        if (!result.IsValid)
            throw new InvalidDataException("Проект не прошёл проверку: " + string.Join("; ", result.Errors.Select(item => item.Message)));
    }

    private static string NormalizeProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Путь проекта не указан.", nameof(path));
        return Path.GetFullPath(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record CheckpointDocument(
        Guid Id,
        Guid ProjectId,
        DateTimeOffset CreatedAt,
        string Name,
        string Snapshot)
    {
        public ProjectCheckpointInfo ToInfo() => new(Id, ProjectId, CreatedAt, Name);
    }

    private sealed record MediaSourceDetails(
        string PreviousPath,
        MediaOnlineState OnlineState,
        string FastFingerprint,
        string VerifiedFingerprint,
        string StreamsJson,
        bool IsVariableFrameRate,
        string ProxyPath);

    private sealed record VideoClipDetails(
        double PositionX,
        double PositionY,
        double ScaleX,
        double ScaleY,
        double Rotation,
        double CropLeft,
        double CropTop,
        double CropRight,
        double CropBottom,
        double Opacity);
}
