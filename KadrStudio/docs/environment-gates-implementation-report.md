# E0.2 environment gate implementation report

Date: 2026-09-08

## Result

Environment-dependent integration tests no longer return successfully when their fixtures are absent.

- Optional runs use `EnvironmentRequiredFactAttribute`, so xUnit reports a skip with the exact missing variables or files.
- `KADR_REQUIRE_ENVIRONMENT_TESTS=1` makes the same missing prerequisite an immediate failing assertion before FFmpeg, MediaHost, benchmark, or AI work starts.
- All five gated tests carry `Category=EnvironmentRequired`; the two benchmarks also carry `Environment=Benchmark`, the live fixture tests `Environment=AnimeFixture`, and the production AI test `Environment=RealAI`.
- The real anime AI pipeline always creates `AiServerV2Client` with `requireProduction: true`. `KADR_REAL_AI_REQUIRE_PRODUCTION` can no longer weaken this qualification when the test executes.
- `scripts/verify-environment-gates.ps1` is an executable preflight. `-Gate real-ai`, `-Gate benchmarks`, or `-Gate all` returns exit code 2 when required opt-ins, source fixtures, or output destinations are absent.

The shared helper validates configured source files with `File.Exists`. Benchmark bodies and GPU/AI behavior were not changed or executed. The pre-existing diagnostics work in `MediaHostIntegrationTests.cs` was preserved; only its environment gate lines were edited for E0.2.

## Behavior covered

`TestEnvironmentGateTests.cs` exercises the decision helper directly with controlled dictionaries and a controlled file predicate. These tests catch three behavioral regressions:

1. missing optional prerequisites being treated as runnable or required failures;
2. required release mode being silently skipped;
3. a complete benchmark environment being rejected.

No test asserts source text. The PowerShell preflight was executed and checked through its process exit code and output.

## Verification evidence

Deterministic helper tests:

```text
dotnet test .\tests\KadrStudio.Integration.Tests\KadrStudio.Integration.Tests.csproj -c Release -m:1 -nr:false --filter "FullyQualifiedName~TestEnvironmentGateTests"
Passed: 3, Failed: 0, Skipped: 0
```

Optional discovery with all gate variables cleared:

```text
dotnet test ... --filter "Category=EnvironmentRequired" --logger "console;verbosity=normal"
Total: 5, Skipped: 5, Failed: 0
```

xUnit printed a specific skip reason for every test, including:

```text
Optional environment test skipped for RealAnimeAi: missing KADR_RUN_REAL_ANIME_AI=1, KADR_ANIME_EPISODE_PATH.
```

Required release behavior with `KADR_REQUIRE_ENVIRONMENT_TESTS=1` and the real-AI inputs cleared:

```text
dotnet test ... --filter "FullyQualifiedName~RealAnimeAiPipelineIntegrationTests"
Exit code: 1
Failed: 1
Required environment gate failed for RealAnimeAi: missing KADR_RUN_REAL_ANIME_AI=1, KADR_ANIME_EPISODE_PATH.
```

The failure occurred in 38 ms at `TestEnvironment.Require`, before the external workload.

Executable preflight behavior:

```text
.\scripts\verify-environment-gates.ps1 -Gate real-ai
Exit code: 2

.\scripts\verify-environment-gates.ps1 -Gate benchmarks
Exit code: 0 (controlled existing-file fixtures and output values)
```

`git diff --check` completed successfully for all E0.2-owned files, with only the repository's existing LF-to-CRLF working-copy notices.

## TDD record

The three helper tests were authored before `TestEnvironment` and named the concrete behavior each production branch must preserve. The requested shared-output coordination prevented running the RED build immediately; while waiting for the root agent's build clearance, the minimal helper implementation was added. Consequently there is no observed failing-test transcript to claim as RED evidence. After clearance, the focused GREEN run passed all three tests. Independent negative-path execution then demonstrated the required-mode failure and the preflight's nonzero exit without invoking any heavy workload.

## Use

Normal developer runs need no extra setting and show unavailable environment tests as skips. A release qualification run that requires these fixtures should set:

```powershell
$env:KADR_REQUIRE_ENVIRONMENT_TESTS = '1'
```

Before running the relevant tests, invoke the matching preflight:

```powershell
.\scripts\verify-environment-gates.ps1 -Gate all
```
