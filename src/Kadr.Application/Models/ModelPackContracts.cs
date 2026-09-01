using System.Collections.Immutable;

namespace KadrStudio.Application.Models;

public sealed record ModelArtifact(
    string Id,
    string DisplayName,
    string Repository,
    string Revision,
    string RelativePath,
    string TargetRelativePath,
    long SizeBytes,
    string Sha256,
    bool IsLocal,
    bool IsExperimental);

public sealed record ModelPackManifest(
    int SchemaVersion,
    string Id,
    string DisplayName,
    long ModelBudgetBytes,
    long AiRootBudgetBytes,
    ImmutableArray<ModelArtifact> Artifacts)
{
    public long TotalSizeBytes => Artifacts.Sum(artifact => artifact.SizeBytes);
}

public sealed record ModelPackInstallProgress(
    double Value,
    string Message,
    string ArtifactId = "");

public sealed record ModelPackInstallationStatus(
    bool IsInstalled,
    long InstalledBytes,
    DateTimeOffset? VerifiedAt,
    ImmutableArray<string> MissingOrInvalidPaths);

public interface IModelPackInstaller
{
    Task<ModelPackManifest> GetPlanAsync(CancellationToken cancellationToken = default);

    Task<ModelPackInstallationStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task InstallAsync(
        IProgress<ModelPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
