using System.Globalization;
using KadrStudio.Application.Storage;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;
using Microsoft.Data.Sqlite;

namespace KadrStudio.Infrastructure.Storage;

public sealed class SqliteRecoveryStore : IRecoveryStore
{
    private readonly string _root;
    private readonly IProjectValidator _validator;

    public SqliteRecoveryStore(string? root = null, IProjectValidator? validator = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kadr Studio", "Recovery"));
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
            await using var connection = await OpenAsync(temporaryPath, readOnly: false, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=FULL;
                CREATE TABLE recovery(
                    singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1),
                    project_id TEXT NOT NULL CHECK(length(project_id)=32),
                    project_name TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision>=0),
                    updated_at TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    snapshot_json TEXT NOT NULL CHECK(length(snapshot_json)>2)
                ) STRICT;
                INSERT INTO recovery(singleton_id, project_id, project_name, revision, updated_at, reason, snapshot_json)
                VALUES(1, $projectId, $name, $revision, $updatedAt, $reason, $snapshot);
                """;
            command.Parameters.AddWithValue("$projectId", project.Id.ToString("N"));
            command.Parameters.AddWithValue("$name", project.Name);
            command.Parameters.AddWithValue("$revision", project.Revision);
            command.Parameters.AddWithValue("$updatedAt", project.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$reason", string.IsNullOrWhiteSpace(reason) ? "Изменение проекта" : reason.Trim());
            command.Parameters.AddWithValue("$snapshot", ProjectDocumentSerializer.Serialize(project));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<ProjectState?> LoadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(projectId);
        if (!File.Exists(path)) return null;
        await using var connection = await OpenAsync(path, readOnly: true, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM recovery WHERE singleton_id=1 AND project_id=$projectId;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString("N"));
        var snapshot = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (snapshot is null) return null;
        var project = ProjectDocumentSerializer.Deserialize(snapshot);
        var validation = _validator.Validate(project);
        return validation.IsValid ? project : throw new InvalidDataException("Recovery-проект повреждён.");
    }

    public async Task<IReadOnlyList<RecoveryProjectInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<RecoveryProjectInfo>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.recovery.kadr", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = await OpenAsync(path, readOnly: true, cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT project_id, project_name, revision, updated_at, reason FROM recovery WHERE singleton_id=1;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) continue;
                results.Add(new RecoveryProjectInfo(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetString(4)));
            }
            catch (SqliteException)
            {
                // One damaged recovery file cannot hide all other recoverable projects.
            }
        }
        return results.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public Task DeleteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryDelete(GetPath(projectId));
        return Task.CompletedTask;
    }

    private string GetPath(Guid projectId) => Path.Combine(_root, $"{projectId:N}.recovery.kadr");

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
}
