using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Models;

namespace KadrStudio.Services;

public sealed class PowerShellModelPackInstaller : IModelPackInstaller
{
    private const string ManifestRelativePath = "config/ai-model-pack.production.json";
    private const string InstallerRelativePath = "scripts/install-production-models.ps1";
    private readonly string _workspaceRoot;

    public PowerShellModelPackInstaller()
        : this(FindWorkspaceRoot(Directory.GetCurrentDirectory(), AppContext.BaseDirectory)
               ?? throw new DirectoryNotFoundException("Корень KadrStudio.sln не найден."))
    {
    }

    public PowerShellModelPackInstaller(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (!File.Exists(Path.Combine(_workspaceRoot, "KadrStudio.sln")))
            throw new DirectoryNotFoundException("Корень KadrStudio.sln не найден.");
    }

    public async Task<ModelPackManifest> GetPlanAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveWorkspaceFile(ManifestRelativePath);
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var artifacts = ImmutableArray.CreateBuilder<ModelArtifact>();

        foreach (var model in root.GetProperty("artifacts").EnumerateArray())
        {
            var id = RequiredString(model, "id");
            var displayName = RequiredString(model, "displayName");
            var targetDirectory = RequiredString(model, "targetDirectory");
            var experimental = model.TryGetProperty("experimental", out var experimentalValue) &&
                               experimentalValue.ValueKind == JsonValueKind.True;
            if (model.TryGetProperty("files", out var files))
            {
                var repository = RequiredString(model, "repository");
                var revision = RequiredString(model, "revision");
                foreach (var file in files.EnumerateArray())
                {
                    var relativePath = RequiredString(file, "path");
                    artifacts.Add(new ModelArtifact(
                        id, displayName, repository, revision, relativePath,
                        Path.Combine(targetDirectory, relativePath),
                        file.GetProperty("size").GetInt64(), RequiredString(file, "sha256"),
                        IsLocal: false, IsExperimental: experimental));
                }
            }
            else
            {
                var local = model.GetProperty("localFile");
                var relativePath = RequiredString(local, "targetPath");
                artifacts.Add(new ModelArtifact(
                    id, displayName, "local", "workspace", relativePath,
                    Path.Combine(targetDirectory, relativePath),
                    local.GetProperty("size").GetInt64(), RequiredString(local, "sha256"),
                    IsLocal: true, IsExperimental: true));
            }
        }

        var manifest = new ModelPackManifest(
            root.GetProperty("schemaVersion").GetInt32(),
            RequiredString(root, "id"), RequiredString(root, "displayName"),
            root.GetProperty("modelBudgetBytes").GetInt64(),
            root.GetProperty("aiRootBudgetBytes").GetInt64(), artifacts.ToImmutable());
        Validate(manifest);
        return manifest;
    }

    public async Task<ModelPackInstallationStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(cancellationToken).ConfigureAwait(false);
        var aiRoot = Path.Combine(_workspaceRoot, ".kadr-ai");
        var installationPath = Path.Combine(aiRoot, "model-pack.installation.json");
        if (!File.Exists(installationPath))
            return new ModelPackInstallationStatus(false, 0, null,
                plan.Artifacts.Select(item => item.TargetRelativePath).ToImmutableArray());

        try
        {
            await using var stream = File.OpenRead(installationPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var markerMatches = root.GetProperty("schemaVersion").GetInt32() == 1 &&
                                string.Equals(RequiredString(root, "manifestId"), plan.Id,
                                    StringComparison.Ordinal) &&
                                root.GetProperty("payloadBytes").GetInt64() == plan.TotalSizeBytes;
            var verifiedAt = root.TryGetProperty("installedAt", out var installedAtValue) &&
                             DateTimeOffset.TryParse(installedAtValue.GetString(), out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            var modelsRoot = Path.GetFullPath(Path.Combine(aiRoot, "models"));
            var modelsPrefix = modelsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var invalid = ImmutableArray.CreateBuilder<string>();
            long installedBytes = 0;
            foreach (var artifact in plan.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(modelsRoot, artifact.TargetRelativePath));
                if (!target.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(target) || new FileInfo(target).Length != artifact.SizeBytes)
                {
                    invalid.Add(artifact.TargetRelativePath);
                    continue;
                }
                installedBytes += artifact.SizeBytes;
            }
            if (!markerMatches && invalid.Count == 0)
                invalid.Add("model-pack.installation.json");
            return new ModelPackInstallationStatus(
                markerMatches && invalid.Count == 0,
                installedBytes,
                markerMatches ? verifiedAt : null,
                invalid.ToImmutable());
        }
        catch (JsonException)
        {
            return new ModelPackInstallationStatus(false, 0, null,
                ImmutableArray.Create("model-pack.installation.json"));
        }
        catch (KeyNotFoundException)
        {
            return new ModelPackInstallationStatus(false, 0, null,
                ImmutableArray.Create("model-pack.installation.json"));
        }
    }

    public async Task InstallAsync(
        IProgress<ModelPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await GetPlanAsync(cancellationToken).ConfigureAwait(false);
        var installer = ResolveWorkspaceFile(InstallerRelativePath);
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = _workspaceRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(installer);
        start.ArgumentList.Add("-ConfirmDownload");

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить установщик моделей.");
        var errors = new StringBuilder();
        var output = ReadLinesAsync(process.StandardOutput, line =>
        {
            var fraction = ParseProgress(line, plan);
            progress?.Report(new ModelPackInstallProgress(fraction, line));
        }, cancellationToken);
        var error = ReadLinesAsync(process.StandardError, line => errors.AppendLine(line), cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(errors.Length > 0 ? errors.ToString().Trim() : "Установщик моделей завершился с ошибкой.");
        progress?.Report(new ModelPackInstallProgress(1, "Пакет моделей установлен и проверен."));
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> lineHandler, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            lineHandler(line);
    }

    private static double ParseProgress(string line, ModelPackManifest manifest)
    {
        for (var i = 0; i < manifest.Artifacts.Length; i++)
            if (line.Contains(manifest.Artifacts[i].RelativePath, StringComparison.OrdinalIgnoreCase))
                return (double)i / Math.Max(1, manifest.Artifacts.Length);
        return 0;
    }

    private string ResolveWorkspaceFile(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        var prefix = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new FileNotFoundException("Файл пакета моделей не найден.", full);
        return full;
    }

    private static void Validate(ModelPackManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Artifacts.IsDefaultOrEmpty)
            throw new InvalidDataException("Manifest пакета моделей имеет неподдерживаемый формат.");
        if (manifest.TotalSizeBytes > manifest.ModelBudgetBytes)
            throw new InvalidDataException("Пакет превышает лимит 30 ГБ.");
        if (manifest.Artifacts.Any(artifact => artifact.SizeBytes <= 0 || artifact.Sha256.Length != 64 ||
                                               (!artifact.IsLocal && artifact.Revision.Length != 40)))
            throw new InvalidDataException("Manifest содержит незакреплённый или непроверяемый артефакт.");
    }

    private static string RequiredString(JsonElement element, string name)
        => element.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"В manifest отсутствует {name}.");

    private static string? FindWorkspaceRoot(params string[] starts)
    {
        foreach (var start in starts.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "KadrStudio.sln"))) return directory.FullName;
                directory = directory.Parent;
            }
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
