using Xunit;
using Xunit.Sdk;

namespace KadrStudio.Integration.Tests;

public enum TestEnvironmentGate
{
    LiveAnimeFixture,
    RealAnimeAi,
    AnalysisProxyBenchmark,
    PreviewBenchmark
}

public sealed record TestEnvironmentGateResult(bool CanRun, bool MustFail, string Message);

public static class TestEnvironment
{
    public const string RequiredModeVariable = "KADR_REQUIRE_ENVIRONMENT_TESTS";

    public static TestEnvironmentGateResult Evaluate(
        TestEnvironmentGate gate,
        IReadOnlyDictionary<string, string?> environment,
        Func<string, bool> fileExists)
    {
        var missing = new List<string>();
        var pathVariable = gate switch
        {
            TestEnvironmentGate.LiveAnimeFixture or TestEnvironmentGate.RealAnimeAi => "KADR_ANIME_EPISODE_PATH",
            TestEnvironmentGate.AnalysisProxyBenchmark => "KADR_ANALYSIS_PROXY_BENCHMARK_SOURCE",
            TestEnvironmentGate.PreviewBenchmark => "KADR_PREVIEW_BENCHMARK_SOURCE",
            _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, null)
        };

        if (gate == TestEnvironmentGate.RealAnimeAi && Value(environment, "KADR_RUN_REAL_ANIME_AI") != "1")
            missing.Add("KADR_RUN_REAL_ANIME_AI=1");

        var fixturePath = Value(environment, pathVariable);
        if (string.IsNullOrWhiteSpace(fixturePath))
            missing.Add(pathVariable);
        else if (!fileExists(fixturePath))
            missing.Add($"{pathVariable} file '{fixturePath}'");

        var outputVariable = gate switch
        {
            TestEnvironmentGate.AnalysisProxyBenchmark => "KADR_ANALYSIS_PROXY_BENCHMARK_OUTPUT",
            TestEnvironmentGate.PreviewBenchmark => "KADR_PREVIEW_BENCHMARK_OUTPUT",
            _ => null
        };
        if (outputVariable is not null && string.IsNullOrWhiteSpace(Value(environment, outputVariable)))
            missing.Add(outputVariable);

        if (missing.Count == 0)
            return new TestEnvironmentGateResult(true, false, string.Empty);

        var required = Value(environment, RequiredModeVariable) == "1";
        var disposition = required ? "Required environment gate failed" : "Optional environment test skipped";
        return new TestEnvironmentGateResult(
            false,
            required,
            $"{disposition} for {gate}: missing {string.Join(", ", missing)}.");
    }

    public static string Require(TestEnvironmentGate gate)
    {
        var result = Evaluate(gate, CaptureEnvironment(), File.Exists);
        if (!result.CanRun)
            throw new XunitException(result.Message);

        return gate switch
        {
            TestEnvironmentGate.LiveAnimeFixture or TestEnvironmentGate.RealAnimeAi =>
                Path.GetFullPath(Environment.GetEnvironmentVariable("KADR_ANIME_EPISODE_PATH")!),
            TestEnvironmentGate.AnalysisProxyBenchmark =>
                Path.GetFullPath(Environment.GetEnvironmentVariable("KADR_ANALYSIS_PROXY_BENCHMARK_SOURCE")!),
            TestEnvironmentGate.PreviewBenchmark =>
                Path.GetFullPath(Environment.GetEnvironmentVariable("KADR_PREVIEW_BENCHMARK_SOURCE")!),
            _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, null)
        };
    }

    internal static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var names = new[]
        {
            RequiredModeVariable,
            "KADR_RUN_REAL_ANIME_AI",
            "KADR_ANIME_EPISODE_PATH",
            "KADR_ANALYSIS_PROXY_BENCHMARK_SOURCE",
            "KADR_ANALYSIS_PROXY_BENCHMARK_OUTPUT",
            "KADR_PREVIEW_BENCHMARK_SOURCE",
            "KADR_PREVIEW_BENCHMARK_OUTPUT"
        };
        return names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    }

    private static string? Value(IReadOnlyDictionary<string, string?> environment, string name)
        => environment.TryGetValue(name, out var value) ? value : null;
}

public sealed class EnvironmentRequiredFactAttribute : FactAttribute
{
    public EnvironmentRequiredFactAttribute(TestEnvironmentGate gate)
    {
        var result = TestEnvironment.Evaluate(gate, TestEnvironment.CaptureEnvironment(), File.Exists);
        if (!result.CanRun && !result.MustFail)
            Skip = result.Message;
    }
}
