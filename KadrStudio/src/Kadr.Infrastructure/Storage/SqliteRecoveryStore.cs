using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KadrStudio.Application.Storage;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;
using Microsoft.Data.Sqlite;

namespace KadrStudio.Infrastructure.Storage;

public sealed class SqliteRecoveryStore : IRecoveryStore
{
    public const int MaximumVersionsPerProject = 20;
    private readonly string _root;
    private readonly IProjectValidator _validator;

    public SqliteRecoveryStore(string? root = null, IProjectValidator? validator = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            AppContext.BaseDirectory,
            "LocalData",
            "Recovery"));
        _validator = validator ?? new ProjectValidator();
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(ProjectState project, string reason, CancellationToken cancellationToken = default)
    {
        var validation = _validator.Validate(project);
        if (!validation.IsValid)
            throw new InvalidDataException("Повреждённое состояние не записывается в recovery.");
        var finalPath = GetPath(project.Id);
        var temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var previous = File.Exists(finalPath)
                ? await ReadEntriesAsync(finalPath, cancellationToken).ConfigureAwait(false)
                : [];
            var snapshot = ProjectDocumentSerializer.Serialize(project);
            var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Изменение проекта" : reason.Trim();
            var latest = previous.MaxBy(item => item.SaveOrdinal);
            var ordinal = checked((latest?.SaveOrdinal ?? 0) + 1);
            var savedAt = DateTimeOffset.UtcNow;
            var entry = latest is not null && latest.Revision == project.Revision && latest.Snapshot == snapshot
                ? latest with { SavedAt = savedAt, SaveOrdinal = ordinal, Reason = normalizedReason }
                : new RecoveryEntry(
                    Guid.NewGuid(), project.Id, project.Name, project.Revision, project.UpdatedAt,
                    normalizedReason, snapshot, Checksum(snapshot), savedAt, ordinal);
            var entries = previous
                .Where(item => item.Id != entry.Id)
                .Append(entry)
                .OrderByDescending(item => item.SaveOrdinal)
                .Take(MaximumVersionsPerProject)
                .OrderBy(item => item.SaveOrdinal)
                .ToArray();
            await using var connection = await OpenAsync(temporaryPath, readOnly: false, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                PRAGMA user_version=2;
                CREATE TABLE recovery_entries(
                    id TEXT PRIMARY KEY CHECK(length(id)=32),
                    project_id TEXT NOT NULL CHECK(length(project_id)=32),
                    project_name TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision>=0),
                    updated_at TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    snapshot_json TEXT NOT NULL CHECK(length(snapshot_json)>2),
                    snapshot_checksum TEXT NOT NULL CHECK(length(snapshot_checksum)=64),
                    saved_at TEXT NOT NULL,
                    save_ordinal INTEGER NOT NULL UNIQUE CHECK(save_ordinal>0)
                ) STRICT;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in entries)
                await WriteEntryAsync(connection, item, cancellationToken).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<bool> IsLatestSnapshotAsync(ProjectState project, CancellationToken cancellationToken = default)
    {
        var latest = await LoadAsync(project.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        return latest is not null && ProjectDocumentSerializer.Serialize(latest) == ProjectDocumentSerializer.Serialize(project);
    }

    public async Task<ProjectState?> LoadAsync(
        Guid projectId,
        Guid? recoveryId = null,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(projectId);
        if (!File.Exists(path)) return null;
        var entries = await ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false);
        var entry = recoveryId is Guid id
            ? entries.SingleOrDefault(item => item.Id == id && item.ProjectId == projectId)
            : entries.Where(item => item.ProjectId == projectId).MaxBy(item => item.SaveOrdinal);
        if (entry is null) return null;
        VerifyChecksum(entry);
        var project = ProjectDocumentSerializer.Deserialize(entry.Snapshot);
        var validation = _validator.Validate(project);
        return validation.IsValid ? project : throw new InvalidDataException("Recovery-проект повреждён.");
    }

    public async Task<IReadOnlyList<RecoveryProjectInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RecoveryEntry>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.recovery.kadr", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.AddRange(await ReadEntriesAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (SqliteException)
            {
                // One damaged recovery file cannot hide all other recoverable projects.
            }
        }
        // Ordinals order saves within a document even after a clock rollback or
        // migration from legacy entries whose only timestamp belonged to the document.
        return results.GroupBy(item => item.ProjectId)
            .OrderByDescending(group => group.MaxBy(item => item.SaveOrdinal)!.SavedAt)
            .SelectMany(group => group.OrderByDescending(item => item.SaveOrdinal))
            .Select(entry => new RecoveryProjectInfo(
                entry.Id, entry.ProjectId, entry.Name, entry.Revision, entry.SavedAt, entry.Reason))
            .ToArray();
    }

    public async Task DeleteAsync(
        Guid projectId,
        Guid? recoveryId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(projectId);
        if (recoveryId is null || !File.Exists(path))
        {
            TryDelete(path);
            return;
        }
        await using var connection = await OpenAsync(path, readOnly: false, cancellationToken).ConfigureAwait(false);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='recovery_entries';";
            if (Convert.ToInt64(
                    await schema.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 0)
            {
                await connection.CloseAsync().ConfigureAwait(false);
                TryDelete(path);
                return;
            }
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recovery_entries WHERE id=$id AND project_id=$projectId;";
        command.Parameters.AddWithValue("$id", recoveryId.Value.ToString("N"));
        command.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM recovery_entries;";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            await connection.CloseAsync().ConfigureAwait(false);
            TryDelete(path);
        }
    }

    private string GetPath(Guid projectId) => Path.Combine(_root, $"{projectId:N}.recovery.kadr");

    private static async Task WriteEntryAsync(SqliteConnection connection, RecoveryEntry entry, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recovery_entries(
                id, project_id, project_name, revision, updated_at, reason, snapshot_json, snapshot_checksum, saved_at, save_ordinal)
            VALUES($id, $projectId, $name, $revision, $updatedAt, $reason, $snapshot, $checksum, $savedAt, $ordinal);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
        command.Parameters.AddWithValue("$projectId", entry.ProjectId.ToString("N"));
        command.Parameters.AddWithValue("$name", entry.Name);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$updatedAt", entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$reason", entry.Reason);
        command.Parameters.AddWithValue("$snapshot", entry.Snapshot);
        command.Parameters.AddWithValue("$checksum", entry.Checksum);
        command.Parameters.AddWithValue("$savedAt", entry.SavedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ordinal", entry.SaveOrdinal);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RecoveryEntry>> ReadEntriesAsync(string path, CancellationToken token)
    {
        await using var connection = await OpenAsync(path, readOnly: true, token).ConfigureAwait(false);
        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='recovery_entries';";
        var hasVersionedSchema = Convert.ToInt64(
            await schema.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        schema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('recovery_entries') WHERE name='save_ordinal';";
        var hasSaveOrdinal = Convert.ToInt64(await schema.ExecuteScalarAsync(token).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;
        await using var command = connection.CreateCommand();
        command.CommandText = hasSaveOrdinal
            ? """
                SELECT id, project_id, project_name, revision, updated_at, reason, snapshot_json, snapshot_checksum, saved_at, save_ordinal
                FROM recovery_entries ORDER BY save_ordinal;
                """
            : hasVersionedSchema
            ? """
                SELECT id, project_id, project_name, revision, updated_at, reason, snapshot_json, snapshot_checksum
                FROM recovery_entries ORDER BY updated_at;
                """
            : """
                SELECT project_id, project_name, revision, updated_at, reason, snapshot_json
                FROM recovery WHERE singleton_id=1;
                """;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var entries = new List<RecoveryEntry>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (hasVersionedSchema)
            {
                entries.Add(new RecoveryEntry(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    Guid.ParseExact(reader.GetString(1), "N"),
                    reader.GetString(2), reader.GetInt64(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    DateTimeOffset.Parse(reader.GetString(hasSaveOrdinal ? 8 : 4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    hasSaveOrdinal ? reader.GetInt64(9) : entries.Count + 1));
            }
            else
            {
                var snapshot = reader.GetString(5);
                entries.Add(new RecoveryEntry(
                    RecoveryId(snapshot), Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetInt64(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(4), snapshot, Checksum(snapshot),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), 1));
            }
        }
        return entries;
    }

    private static string Checksum(string snapshot)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));

    private static Guid RecoveryId(string snapshot)
        => new(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)).AsSpan(0, 16));

    private static void VerifyChecksum(RecoveryEntry entry)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(entry.Checksum), Convert.FromHexString(Checksum(entry.Snapshot))))
            throw new InvalidDataException("Контрольная сумма recovery-снимка не совпадает.");
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly, CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(token).ConfigureAwait(false);
        return connection;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record RecoveryEntry(
        Guid Id,
        Guid ProjectId,
        string Name,
        long Revision,
        DateTimeOffset UpdatedAt,
        string Reason,
        string Snapshot,
        string Checksum,
        DateTimeOffset SavedAt,
        long SaveOrdinal);
}
