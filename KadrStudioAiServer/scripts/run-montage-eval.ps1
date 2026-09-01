[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Model,
    [Parameter(Mandatory)] [string]$ModelDirectory,
    [Parameter(Mandatory)] [ValidateSet('Planner', 'Vision')] [string]$ModelKind,
    [Parameter(Mandatory)] [string]$Revision,
    [string]$CasesDirectory = $(
        $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.kadr-ai'))
        Join-Path $root 'eval\cases'
    ),
    [string]$OutputPath = $(
        $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.kadr-ai'))
        Join-Path $root 'eval\reports\montage-eval.json'
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$requiredProfiles = @(
    'generic', 'film-series', 'anime-episode',
    'talking-head', 'podcast', 'short-form-highlights'
)

function Get-ModelHash([string]$directory) {
    $files = @(Get-ChildItem -LiteralPath $directory -File -Recurse |
        Where-Object { $_.Name -ne 'kadr-snapshot.json' } |
        Sort-Object FullName)
    if ($files.Count -eq 0) { throw 'Model directory contains no weight/config files.' }
    if (@($files | Where-Object { $_.Name.EndsWith('.kadr-part', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw 'Model directory contains an incomplete resumable download.'
    }
    $hash = [System.Security.Cryptography.IncrementalHash]::CreateHash(
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($directory, $file.FullName)
        $hash.AppendData([System.Text.Encoding]::UTF8.GetBytes($relative))
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $buffer = New-Object byte[] 1048576
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $hash.AppendData($buffer, 0, $read)
            }
        } finally { $stream.Dispose() }
    }
    return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
}

$modelPath = [System.IO.Path]::GetFullPath($ModelDirectory)
$casesPath = [System.IO.Path]::GetFullPath($CasesDirectory)
$aiRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.kadr-ai'))
foreach ($path in @($modelPath, $casesPath, [System.IO.Path]::GetFullPath($OutputPath))) {
    if (-not $path.StartsWith($aiRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Models, eval cases and reports must stay inside the project .kadr-ai directory.'
    }
}
if (-not (Test-Path -LiteralPath $modelPath -PathType Container)) { throw 'Model directory does not exist.' }
if (-not (Test-Path -LiteralPath $casesPath -PathType Container)) { throw 'Montage-eval cases directory does not exist.' }
$modelHash = Get-ModelHash $modelPath
$caseFiles = @(Get-ChildItem -LiteralPath $casesPath -Filter '*.json' -File)
if ($caseFiles.Count -eq 0) { throw 'No montage-eval case results were found.' }
$cases = @()
foreach ($file in $caseFiles) {
    $case = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($field in @('caseId', 'profile', 'model', 'modelHash', 'sourcePreserved',
                         'technicalCompleted', 'humanUsable', 'repeatedGraphWithoutEvidence',
                         'contextOverflow')) {
        if ($null -eq $case.PSObject.Properties[$field]) {
            throw "$($file.Name) is missing required field '$field'."
        }
    }
    if ([string]$case.model -ne $Model -or [string]$case.modelHash -ne $modelHash) {
        throw "$($file.Name) was produced by another model snapshot."
    }
    if ([string]$case.profile -notin $requiredProfiles) {
        throw "$($file.Name) uses unsupported profile '$($case.profile)'."
    }
    $cases += $case
}
if (@($cases | Group-Object caseId | Where-Object Count -gt 1).Count -gt 0) {
    throw 'Montage-eval contains duplicate caseId values.'
}

$profileCounts = [ordered]@{}
foreach ($profile in $requiredProfiles) {
    $profileCounts[$profile] = @($cases | Where-Object profile -eq $profile).Count
}
$total = $cases.Count
$sourceRate = @($cases | Where-Object sourcePreserved -eq $true).Count / [double]$total
$technicalRate = @($cases | Where-Object technicalCompleted -eq $true).Count / [double]$total
$usableRate = @($cases | Where-Object humanUsable -eq $true).Count / [double]$total
$repeatViolations = @($cases | Where-Object repeatedGraphWithoutEvidence -eq $true).Count
$contextOverflows = @($cases | Where-Object contextOverflow -eq $true).Count
$passed = $total -ge 30 -and
    @($profileCounts.Values | Where-Object { $_ -lt 5 }).Count -eq 0 -and
    $sourceRate -eq 1 -and $technicalRate -ge 0.95 -and $usableRate -ge 0.80 -and
    $repeatViolations -eq 0 -and $contextOverflows -eq 0

$report = [ordered]@{
    schemaVersion = 1
    model = $Model
    modelKind = $ModelKind
    modelHash = $modelHash
    revision = $Revision
    totalCases = $total
    profileCounts = $profileCounts
    sourcePreservationRate = $sourceRate
    technicalCompletionRate = $technicalRate
    humanUsableRate = $usableRate
    repeatedGraphWithoutEvidence = $repeatViolations
    contextOverflowCount = $contextOverflows
    passed = $passed
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
}
$outputFull = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $outputFull) -Force | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputFull -Encoding UTF8
Write-Host "Montage eval: source=$($sourceRate.ToString('P1')), technical=$($technicalRate.ToString('P1')), usable=$($usableRate.ToString('P1'))."
if (-not $passed) { throw "Montage eval did not pass. Report: $outputFull" }
Write-Host "Montage eval passed: $outputFull" -ForegroundColor Green
