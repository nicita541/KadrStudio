using Xunit;

namespace KadrStudio.Integration.Tests;

public sealed class TestEnvironmentGateTests
{
    [Fact]
    public void Optional_gate_reports_missing_prerequisites_as_a_skip()
    {
        var environment = new Dictionary<string, string?>();

        var result = TestEnvironment.Evaluate(
            TestEnvironmentGate.RealAnimeAi,
            environment,
            _ => false);

        Assert.False(result.CanRun);
        Assert.False(result.MustFail);
        Assert.Contains("KADR_RUN_REAL_ANIME_AI=1", result.Message);
        Assert.Contains("KADR_ANIME_EPISODE_PATH", result.Message);
    }

    [Fact]
    public void Required_release_gate_fails_when_a_fixture_is_absent()
    {
        var environment = new Dictionary<string, string?>
        {
            [TestEnvironment.RequiredModeVariable] = "1",
            ["KADR_RUN_REAL_ANIME_AI"] = "1",
            ["KADR_ANIME_EPISODE_PATH"] = "missing.mkv"
        };

        var result = TestEnvironment.Evaluate(
            TestEnvironmentGate.RealAnimeAi,
            environment,
            _ => false);

        Assert.False(result.CanRun);
        Assert.True(result.MustFail);
        Assert.Contains("missing.mkv", result.Message);
    }

    [Fact]
    public void Benchmark_gate_accepts_an_existing_source_and_output_destination()
    {
        var environment = new Dictionary<string, string?>
        {
            ["KADR_PREVIEW_BENCHMARK_SOURCE"] = "fixture.mp4",
            ["KADR_PREVIEW_BENCHMARK_OUTPUT"] = "results.json"
        };

        var result = TestEnvironment.Evaluate(
            TestEnvironmentGate.PreviewBenchmark,
            environment,
            path => path == "fixture.mp4");

        Assert.True(result.CanRun);
        Assert.False(result.MustFail);
        Assert.Equal(string.Empty, result.Message);
    }
}
