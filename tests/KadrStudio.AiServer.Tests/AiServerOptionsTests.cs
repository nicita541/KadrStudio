using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Workers;

namespace KadrStudio.AiServer.Tests;

public sealed class AiServerOptionsTests
{
    [Fact]
    public void Workstation_planner_context_is_bounded_to_32k_by_default()
    {
        Assert.Equal(32_768, new AiServerOptions().MaxPlannerContextTokens);
    }

    [Fact]
    public void Planner_alias_and_backend_resolve_to_the_same_isolated_worker_role()
    {
        var options = new AiServerOptions
        {
            PlannerBackendModel = "Qwen/Qwen3-30B-A3B-Instruct-2507",
            PlannerPublicModelAlias = "kadr-planner:latest"
        };

        var alias = options.ResolvePlannerModel("kadr-planner:latest");
        var backend = options.ResolvePlannerModel("Qwen/Qwen3-30B-A3B-Instruct-2507");

        Assert.Equal(alias, backend);
        Assert.Equal("planner", alias.Role);
        Assert.Equal("Qwen/Qwen3-30B-A3B-Instruct-2507", alias.BackendModel);
    }

    [Fact]
    public void Unmanaged_reasoning_model_is_rejected()
    {
        var options = new AiServerOptions();

        Assert.Throws<InvalidOperationException>(() =>
            options.ResolvePlannerModel("unmanaged-model"));
    }

    [Theory]
    [InlineData("video-understanding", true)]
    [InlineData("director", true)]
    [InlineData("critic", true)]
    [InlineData("embedding", true)]
    [InlineData("audio-events", false)]
    public void Worker_accelerator_policy_matches_model_residency(string analyzer, bool expected)
    {
        Assert.Equal(expected, LoopbackGrpcWorkerGateway.UsesAccelerator(analyzer));
    }
}
