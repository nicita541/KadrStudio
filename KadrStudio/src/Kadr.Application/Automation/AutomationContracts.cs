using System.Collections.Immutable;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Core.Validation;

namespace KadrStudio.Application.Automation;

public sealed record ProjectAutomationSnapshot(
    Guid ProjectId,
    long BaseRevision,
    DateTimeOffset CapturedAt,
    ProjectState State,
    string SourceFingerprint);

public sealed record AutomationProposal(
    Guid Id,
    Guid ProjectId,
    long BaseRevision,
    DateTimeOffset CreatedAt,
    string Title,
    string Summary,
    string Producer,
    ImmutableArray<IEditCommand> Commands,
    bool CreateCheckpoint = true);

public sealed record AutomationApplyResult(
    bool Applied,
    bool IsStale,
    ProjectState State,
    string Message);

public interface IAutomationProposalValidator
{
    ValidationResult Validate(ProjectState current, AutomationProposal proposal);
}

public sealed class AutomationProposalValidator(IProjectValidator? projectValidator = null) : IAutomationProposalValidator
{
    private readonly IProjectValidator _projectValidator = projectValidator ?? new ProjectValidator();

    public ValidationResult Validate(ProjectState current, AutomationProposal proposal)
    {
        var errors = new List<ValidationError>();
        if (proposal.Id == Guid.Empty) errors.Add(new("automation.id", "Proposal ID cannot be empty."));
        if (proposal.ProjectId != current.Id) errors.Add(new("automation.project", "Proposal belongs to another project."));
        if (proposal.BaseRevision != current.Revision) errors.Add(new("automation.stale", "Project changed after automation started."));
        if (proposal.Commands.IsDefaultOrEmpty) errors.Add(new("automation.empty", "Proposal contains no commands."));
        if (errors.Count > 0) return new ValidationResult(errors);

        var candidate = current;
        try
        {
            foreach (var command in proposal.Commands) candidate = command.Apply(candidate);
        }
        catch (Exception exception)
        {
            errors.Add(new ValidationError("automation.command", exception.Message));
            return new ValidationResult(errors);
        }
        return _projectValidator.Validate(candidate);
    }
}

public sealed class AutomationProposalApplier(IAutomationProposalValidator? validator = null)
{
    private readonly IAutomationProposalValidator _validator = validator ?? new AutomationProposalValidator();

    public AutomationApplyResult Apply(IEditorSession session, AutomationProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        var validation = _validator.Validate(session.State, proposal);
        if (!validation.IsValid)
        {
            var stale = validation.Errors.Any(item => item.Code == "automation.stale");
            return new AutomationApplyResult(
                false, stale, session.State,
                string.Join("; ", validation.Errors.Select(item => item.Message)));
        }

        var result = session.Execute(new EditTransaction(
            proposal.Title,
            proposal.Commands,
            proposal.CreateCheckpoint,
            proposal.CreateCheckpoint ? $"Before: {proposal.Title}" : null));
        return new AutomationApplyResult(result.Changed, false, result.State, proposal.Summary);
    }
}
