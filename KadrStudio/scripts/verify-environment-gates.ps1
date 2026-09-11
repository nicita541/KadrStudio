[CmdletBinding()]
param(
    [ValidateSet('real-ai', 'benchmarks', 'all')]
    [string] $Gate = 'all'
)

$failures = [System.Collections.Generic.List[string]]::new()

function Require-Value {
    param([string] $Name, [string] $ExpectedValue)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        $script:failures.Add("$Name is not set.")
    }
    elseif ($ExpectedValue -and $value -ne $ExpectedValue) {
        $script:failures.Add("$Name must be '$ExpectedValue' (found '$value').")
    }
}

function Require-FileValue {
    param([string] $Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        $script:failures.Add("$Name is not set.")
    }
    elseif (-not [System.IO.File]::Exists($value)) {
        $script:failures.Add("$Name fixture does not exist: $value")
    }
}

if ($Gate -in @('real-ai', 'all')) {
    Require-Value 'KADR_RUN_REAL_ANIME_AI' '1'
    Require-FileValue 'KADR_ANIME_EPISODE_PATH'
}

if ($Gate -in @('benchmarks', 'all')) {
    Require-FileValue 'KADR_ANALYSIS_PROXY_BENCHMARK_SOURCE'
    Require-Value 'KADR_ANALYSIS_PROXY_BENCHMARK_OUTPUT'
    Require-FileValue 'KADR_PREVIEW_BENCHMARK_SOURCE'
    Require-Value 'KADR_PREVIEW_BENCHMARK_OUTPUT'
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Environment gate '$Gate' failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine(" - $failure")
    }
    exit 2
}

[Console]::Out.WriteLine("Environment gate '$Gate' is ready.")
exit 0
