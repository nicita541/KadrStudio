using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KadrStudio.Application.Automation.Agent.Tools;

namespace KadrStudio.Application.Automation.Agent;

public static class AgentPlanFingerprint
{
    public static string Create(AgentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var builder = new StringBuilder();
        builder.Append(plan.Id).Append('|')
            .Append(plan.Version).Append('|')
            .Append(plan.Objective).Append('|')
            .Append(plan.Summary);
        foreach (var constraint in plan.Constraints)
        {
            builder.Append("\nconstraint:").Append(constraint);
        }

        foreach (var step in plan.Steps.OrderBy(step => step.Order))
        {
            builder.Append("\nstep:").Append(step.Id).Append('|')
                .Append(step.Order).Append('|')
                .Append(step.Title).Append('|')
                .Append(step.Description).Append('|')
                .Append(step.ExpectedEditingTool).Append('|')
                .Append(step.EvidenceRequirement).Append('|')
                .Append(step.ExpectedEffect);
            if (step.ExpectedEditingArguments is { ValueKind: JsonValueKind.Object } arguments)
            {
                builder.Append('|')
                    .Append(AgentActionApproval.CreateSignature(
                        step.ExpectedEditingTool ?? string.Empty,
                        arguments));
            }
            foreach (var sequence in step.EvidenceObservationSequences)
            {
                builder.Append("|e:").Append(sequence);
            }
            foreach (var invariant in step.ProtectedInvariants)
            {
                builder.Append("|i:").Append(invariant);
            }
            foreach (var check in step.VerificationChecks)
            {
                builder.Append("|v:").Append(check);
            }
        }

        return Hash(builder.ToString());
    }

    public static string CreateArguments(string toolName, JsonElement arguments)
        => Hash(AgentActionApproval.CreateSignature(toolName, arguments));

    private static string Hash(string value)
        => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
