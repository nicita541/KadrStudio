using System.Collections.Immutable;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Automation;

public sealed record MediaAnalysisRequest(
    ImmutableArray<Guid> SourceIds,
    GameEditingProfile Profile,
    string Model,
    bool DeepAnalysis,
    bool IsBackground = false);

public sealed record MontagePlanningContext(
    ProjectState Project,
    MontageRequest Request,
    ImmutableDictionary<Guid, MediaAnalysisManifest> Manifests,
    MontagePlan? PreviousPlan = null,
    string RevisionRequest = "");

public sealed record MontagePlanValidationResult(
    ValidationResult Validation,
    ImmutableArray<string> Warnings)
{
    public bool IsValid => Validation.IsValid;
}

public sealed record MontageDraftCompilation(
    SequenceState Sequence,
    ImmutableArray<string> Warnings);

public sealed record MontagePreparationResult(
    ImmutableDictionary<Guid, MediaAnalysisManifest> Manifests,
    MontagePlan Plan,
    ImmutableArray<string> Warnings,
    ImmutableArray<MontageDecision> PendingDecisions);

public interface IMediaAnalysisPipeline
{
    Task<ImmutableDictionary<Guid, MediaAnalysisManifest>> AnalyzeSourcesAsync(
        ProjectState project,
        MediaAnalysisRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IMontagePlanningProvider
{
    Task<MontagePlan> CreatePlanAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default);

    Task<MontagePlan> RevisePlanAsync(
        MontagePlanningContext context,
        CancellationToken cancellationToken = default);
}

public interface IMontagePlanValidator
{
    MontagePlanValidationResult Validate(ProjectState project, MontagePlan plan);
}

public interface IMontagePlanCompiler
{
    MontageDraftCompilation Compile(
        ProjectState project,
        MontagePlan plan,
        IReadOnlyDictionary<Guid, MediaAnalysisManifest>? manifests = null);
}

public interface IAiMontageCoordinator
{
    Task<MontagePreparationResult> PreparePlanAsync(
        ProjectState project,
        MediaAnalysisRequest analysisRequest,
        MontageRequest montageRequest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ImmutableDictionary<Guid, MediaAnalysisManifest>> AnalyzeSourcesAsync(
        ProjectState project,
        MediaAnalysisRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<MontagePlan> CreatePlanAsync(
        ProjectState project,
        MontageRequest request,
        ImmutableDictionary<Guid, MediaAnalysisManifest> manifests,
        CancellationToken cancellationToken = default);

    Task<MontagePlan> RevisePlanAsync(
        ProjectState project,
        MontagePlan plan,
        string revisionRequest,
        ImmutableDictionary<Guid, MediaAnalysisManifest> manifests,
        CancellationToken cancellationToken = default);

    MontagePlanValidationResult ValidatePlan(ProjectState project, MontagePlan plan);

    MontagePlan ResolveDecision(
        ProjectState project,
        MontagePlan plan,
        Guid decisionId,
        string answer,
        TimelineTime? resolvedTime = null);

    MontageDraftCompilation CreateDraft(
        ProjectState project,
        MontagePlan plan,
        IReadOnlyDictionary<Guid, MediaAnalysisManifest>? manifests = null);
}

public sealed class AiMontageCoordinator(
    IMediaAnalysisPipeline analysis,
    IMontagePlanningProvider planning,
    IMontagePlanValidator? validator = null,
    IMontagePlanCompiler? compiler = null) : IAiMontageCoordinator
{
    private readonly IMontagePlanValidator _validator = validator ?? new MontagePlanValidator();
    private readonly IMontagePlanCompiler _compiler = compiler ?? new MontagePlanCompiler();

    public async Task<MontagePreparationResult> PreparePlanAsync(
        ProjectState project,
        MediaAnalysisRequest analysisRequest,
        MontageRequest montageRequest,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var analysisProgress = progress is null
            ? null
            : new Progress<double>(value => progress.Report(Math.Clamp(value, 0, 1) * 0.9));
        var manifests = await analysis.AnalyzeSourcesAsync(
            project, analysisRequest, analysisProgress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = await CreatePlanAsync(
            project,
            montageRequest,
            manifests,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        var pending = plan.Decisions.IsDefault
            ? ImmutableArray<MontageDecision>.Empty
            : plan.Decisions.Where(item => !item.IsResolved).ToImmutableArray();
        return new MontagePreparationResult(manifests, plan, plan.Warnings, pending);
    }

    public Task<ImmutableDictionary<Guid, MediaAnalysisManifest>> AnalyzeSourcesAsync(
        ProjectState project,
        MediaAnalysisRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => analysis.AnalyzeSourcesAsync(project, request, progress, cancellationToken);

    public async Task<MontagePlan> CreatePlanAsync(
        ProjectState project,
        MontageRequest request,
        ImmutableDictionary<Guid, MediaAnalysisManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        RejectLegacyPreset(request.Preset);
        var plan = await planning.CreatePlanAsync(
            new MontagePlanningContext(project, request, manifests), cancellationToken).ConfigureAwait(false);
        return EnsureSafeOrConflict(project, plan);
    }

    public async Task<MontagePlan> RevisePlanAsync(
        ProjectState project,
        MontagePlan plan,
        string revisionRequest,
        ImmutableDictionary<Guid, MediaAnalysisManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(revisionRequest)) return plan;
        var request = new MontageRequest(
            plan.RequestId,
            new MontageScope(MontageScopeKind.SelectedSources, plan.Dependencies.SourceFingerprints.Keys.ToImmutableArray()),
            plan.TargetFormat,
            plan.MinimumDuration,
            plan.TargetDuration,
            plan.MaximumDuration,
            revisionRequest.Trim(),
            plan.ProfileSnapshot,
            plan.Constraints,
            plan.PresetSnapshot);
        var revised = await planning.RevisePlanAsync(
            new MontagePlanningContext(project, request, manifests, plan, revisionRequest.Trim()),
            cancellationToken).ConfigureAwait(false);
        EnsureLockedItemsPreserved(plan, revised);
        return EnsureSafeOrConflict(project, revised);
    }

    public MontagePlanValidationResult ValidatePlan(ProjectState project, MontagePlan plan)
        => _validator.Validate(project, plan);

    public MontagePlan ResolveDecision(
        ProjectState project,
        MontagePlan plan,
        Guid decisionId,
        string answer,
        TimelineTime? resolvedTime = null)
        => throw new InvalidOperationException(
            "Старые сценарные решения больше не выполняются. Постройте новый универсальный AI-план.");

    public MontageDraftCompilation CreateDraft(
        ProjectState project,
        MontagePlan plan,
        IReadOnlyDictionary<Guid, MediaAnalysisManifest>? manifests = null)
        => _compiler.Compile(project, plan, manifests);

    private MontagePlan EnsureSafeOrConflict(ProjectState project, MontagePlan plan)
    {
        var validation = _validator.Validate(project, plan);
        if (validation.IsValid) return plan;
        var errors = validation.Validation.Errors;
        if (errors.All(item => item.Code == "montage.too-long") &&
            plan.Constraints.Any(item => item.Kind == SourceAnnotationKind.Required && item.IsHard))
        {
            return plan with
            {
                Status = MontagePlanStatus.Draft,
                Warnings = plan.Warnings.AddRange(errors.Select(item => item.Message).Distinct()),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        throw new InvalidOperationException(string.Join("; ", errors.Select(item => item.Message)));
    }

    private static void EnsureLockedItemsPreserved(MontagePlan before, MontagePlan after)
    {
        foreach (var locked in before.Items.Where(item => item.IsLocked))
        {
            if (!after.Items.Any(item => item.Id == locked.Id && item == locked))
                throw new InvalidOperationException("ИИ попытался изменить заблокированный пункт плана.");
        }
    }

    private static void RejectLegacyPreset(AutomationPreset? preset)
    {
        if (preset?.Recipe == AutomationRecipeKind.MergeEpisodes)
        {
            throw new InvalidOperationException(
                "Сохранённый сценарий «Объединить серии» доступен только для чтения. " +
                "Опишите задачу универсальному AI-агенту и постройте новый план.");
        }
    }
}
