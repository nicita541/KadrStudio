using System.Collections.Immutable;

namespace KadrStudio.Application.Automation.Agent.Persistence;

public sealed record AgentTaskPersistenceEnvelope(
    int FormatVersion,
    AgentTaskState Task)
{
    public const int CurrentFormatVersion = 2;

    public static AgentTaskPersistenceEnvelope Create(AgentTaskState task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var normalizedPlan = task.Plan is null
            ? null
            : task.Plan with
            {
                Constraints = task.Plan.Constraints.IsDefault ? [] : task.Plan.Constraints,
                Steps = task.Plan.Steps.Select(step => step with
                {
                    EvidenceObservationSequences = step.EvidenceObservationSequences.IsDefault
                        ? []
                        : step.EvidenceObservationSequences,
                    ProtectedInvariants = step.ProtectedInvariants.IsDefault
                        ? []
                        : step.ProtectedInvariants,
                    VerificationChecks = step.VerificationChecks.IsDefault
                        ? []
                        : step.VerificationChecks
                }).ToImmutableArray()
            };
        var normalized = task with
        {
            Brief = task.Brief is null
                ? null
                : AgentTaskBrief.Create(
                    task.Brief.Kind,
                    task.Brief.Goal,
                    task.Brief.Scope,
                    task.Brief.ProtectedElements.IsDefault ? [] : task.Brief.ProtectedElements,
                    task.Brief.Constraints.IsDefault ? [] : task.Brief.Constraints,
                    task.Brief.AcceptanceCriteria.IsDefault ? [] : task.Brief.AcceptanceCriteria,
                    task.Brief.Assumptions.IsDefault ? [] : task.Brief.Assumptions,
                    task.Brief.MissingInformation.IsDefault ? [] : task.Brief.MissingInformation,
                    task.Brief.InvestigationStrategy),
            Plan = normalizedPlan,
            Questions = task.Questions.IsDefault
                ? []
                : task.Questions.Select(question => question with
                {
                    Options = question.AvailableOptions
                }).ToImmutableArray(),
            Journal = task.Journal.IsDefault ? [] : task.Journal,
            EvidenceLedger = task.Evidence.Select(evidence => evidence with
            {
                Facts = evidence.Facts.IsDefault ? [] : evidence.Facts
            }).ToImmutableArray()
        };
        return new AgentTaskPersistenceEnvelope(CurrentFormatVersion, normalized);
    }
}
