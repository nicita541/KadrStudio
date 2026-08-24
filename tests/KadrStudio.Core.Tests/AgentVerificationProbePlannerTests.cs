using KadrStudio.Application.Automation.Agent;
using KadrStudio.Application.Automation.Agent.Tools;
using KadrStudio.Application.Automation.Agent.Verification;

namespace KadrStudio.Core.Tests;

public sealed class AgentVerificationProbePlannerTests
{
    [Fact]
    public void Batch_ripple_ranges_are_mapped_to_post_edit_join_coordinates()
    {
        var plan = Publish(AgentEvidenceRequirement.All);

        var probes = AgentVerificationProbePlanner.Create(plan);

        Assert.Equal(6, probes.Length);
        Assert.All(probes.Take(3), probe => Assert.Equal(146, probe.DraftBoundarySeconds));
        Assert.All(probes.Skip(3), probe => Assert.Equal(1_200, probe.DraftBoundarySeconds));
        Assert.Equal(
            new[]
            {
                AgentEvidenceCapabilities.Frames,
                AgentEvidenceCapabilities.Audio,
                AgentEvidenceCapabilities.Transcript
            },
            probes.Take(3).Select(probe => probe.RequiredCapabilities));
    }

    [Fact]
    public void Single_channel_plan_rechecks_the_same_content_channel_after_edit()
    {
        var plan = Publish(AgentEvidenceRequirement.Frames);

        var probes = AgentVerificationProbePlanner.Create(plan);

        Assert.Equal(2, probes.Length);
        Assert.All(probes, probe =>
            Assert.Equal(AgentEvidenceCapabilities.Frames, probe.RequiredCapabilities));
    }

    private static AgentPlan Publish(AgentEvidenceRequirement requirement)
    {
        var orchestrator = new AiAgentOrchestrator();
        orchestrator.StartTask(Guid.NewGuid(), Guid.NewGuid(), "Удалить два диапазона.");
        orchestrator.BeginPlanning();
        return orchestrator.PublishPlan(AgentPlanDraft.Create(
            "Удалить подтверждённые диапазоны.",
            "Одно атомарное пакетное действие.",
            ["Не менять source."],
            [new AgentPlanStepDraft(
                "Удалить диапазоны",
                "Удалить два доказанных смысловых блока.",
                "ripple_delete_ranges",
                [1, 2],
                AgentToolJson.ToElement(new
                {
                    ranges = new[]
                    {
                        new { start_seconds = 146d, end_seconds = 238d },
                        new { start_seconds = 1_292d, end_seconds = 1_380d }
                    }
                }),
                requirement)])).Plan!;
    }
}
