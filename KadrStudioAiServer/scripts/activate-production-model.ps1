[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Model,
    [Parameter(Mandatory)] [string]$ModelDirectory,
    [Parameter(Mandatory)] [string]$Tokenizer,
    [Parameter(Mandatory)] [string]$EvalResult,
    [ValidateSet('Planner', 'Vision')] [string]$ModelKind = 'Planner',
    [int]$ContextWindowTokens = 0,
    [string[]]$Roles = @(),
    [string[]]$Profiles = @(
        'profile-selection', 'generic', 'film-series', 'anime-episode',
        'talking-head', 'podcast', 'short-form-highlights'
    )
)

$ErrorActionPreference = 'Stop'
if ($Roles.Count -eq 0) {
    $Roles = if ($ModelKind -eq 'Vision') {
        @('VideoUnderstanding')
    } else {
        @(
            'Director', 'RoughCut', 'StoryContinuity', 'Rhythm', 'DialogueAudio',
            'CompositionReframe', 'Captions', 'Critic'
        )
    }
}
if ($ContextWindowTokens -eq 0) {
    $ContextWindowTokens = if ($ModelKind -eq 'Vision') { 262144 } else { 32768 }
}
if ($ContextWindowTokens -lt 8192) { throw 'ContextWindowTokens must be at least 8192.' }
$aiRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.kadr-ai'))
$modelPath = [System.IO.Path]::GetFullPath($ModelDirectory)
$modelsRoot = [System.IO.Path]::GetFullPath((Join-Path $aiRoot 'models'))
if (-not $modelPath.StartsWith($modelsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Production model must live inside KADR_AI_DATA_ROOT\models.'
}
if (-not (Test-Path -LiteralPath $modelPath -PathType Container)) { throw 'Model directory does not exist.' }
$eval = Get-Content -LiteralPath $EvalResult -Raw | ConvertFrom-Json
$files = Get-ChildItem -LiteralPath $modelPath -File -Recurse |
    Where-Object { $_.Name -ne 'kadr-snapshot.json' } |
    Sort-Object FullName
if ($files.Count -eq 0) { throw 'Model directory contains no files.' }
if (@($files | Where-Object { $_.Name.EndsWith('.kadr-part', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
    throw 'Model directory contains an incomplete resumable download.'
}
$sha = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($modelPath, $file.FullName)
    $sha.AppendData([System.Text.Encoding]::UTF8.GetBytes($relative))
    $stream = [System.IO.File]::OpenRead($file.FullName)
    try {
        $buffer = New-Object byte[] 1048576
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $sha.AppendData($buffer, 0, $read) }
    } finally { $stream.Dispose() }
}
$modelHash = [Convert]::ToHexString($sha.GetHashAndReset()).ToLowerInvariant()
$modelSizeBytes = [long]($files | Measure-Object -Property Length -Sum).Sum
$modelLastWriteTimeUtc = ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
if ($eval.schemaVersion -ne 1 -or $eval.passed -ne $true -or
    [string]::IsNullOrWhiteSpace([string]$eval.revision) -or
    [string]$eval.model -ne $Model -or [string]$eval.modelKind -ne $ModelKind -or
    [string]$eval.modelHash -ne $modelHash -or [int]$eval.totalCases -lt 30 -or
    [double]$eval.sourcePreservationRate -ne 1 -or
    [double]$eval.technicalCompletionRate -lt 0.95 -or
    [double]$eval.humanUsableRate -lt 0.80 -or
    [int]$eval.repeatedGraphWithoutEvidence -ne 0 -or
    [int]$eval.contextOverflowCount -ne 0) {
    throw 'Montage eval report is incomplete, belongs to another model snapshot, or did not pass release thresholds.'
}
foreach ($profile in $Profiles) {
    if ($profile -eq 'profile-selection') { continue }
    $count = $eval.profileCounts.PSObject.Properties[$profile]
    if ($null -eq $count -or [int]$count.Value -lt 5) {
        throw "Montage eval has fewer than five cases for profile '$profile'."
    }
}
$safeName = -join ($Model.ToCharArray() | ForEach-Object { if ([char]::IsLetterOrDigit($_) -or $_ -in @('-', '_')) { $_ } else { '_' } })
$capabilities = Join-Path $modelsRoot 'capabilities'
New-Item -ItemType Directory -Path $capabilities -Force | Out-Null
$manifest = [ordered]@{
    model = $Model
    modelHash = $modelHash
    tokenizer = $Tokenizer
    contextWindowTokens = $ContextWindowTokens
    supportedRoles = $Roles
    supportedProfiles = $Profiles
    montageEvalRevision = [string]$eval.revision
    montageEvalPassed = $true
    productionApproved = $true
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    verifiedIdentity = [ordered]@{
        path = $modelPath
        sizeBytes = $modelSizeBytes
        lastWriteTimeUtc = ([DateTimeOffset]$modelLastWriteTimeUtc).ToUniversalTime().ToString('O')
        sha256 = $modelHash
        revision = [string]$eval.revision
    }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $capabilities "$safeName.json") -Encoding UTF8
Write-Host "Activated $Model with hash $modelHash and eval $($eval.revision)."
