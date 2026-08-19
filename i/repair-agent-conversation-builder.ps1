param(
    [string]$RepoRoot = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)

function Get-Sha256Upper {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

$relativePath = 'src\Kadr\ViewModels\MainViewModel.cs'
$source = Join-Path $packageRoot $relativePath
$destination = Join-Path $repoRootFull $relativePath

$expectedOldHash = '912B4B78107AC971B4F02ED97772F4053EEF1BDC0BC90BCF8BED9808EBE9EDF3'
$expectedNewHash = '307B9566E6C2E41AC67308050DA95BE4564A5991A771882201E1C12C15C101FF'

Write-Host "Kadr Agent Stages 6-8 / conversation builder fix"
Write-Host "Repo: $repoRootFull"
Write-Host ""

if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Fix package file not found: $relativePath"
}

if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
    throw "Installed file not found: $relativePath"
}

$packageHash = Get-Sha256Upper -Path $source
if ($packageHash -ne $expectedNewHash) {
    throw "Fix package integrity check failed."
}

$currentHash = Get-Sha256Upper -Path $destination

if ($currentHash -eq $expectedNewHash) {
    Write-Host "Already fixed: $relativePath"
}
elseif ($currentHash -eq $expectedOldHash) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupRoot = Join-Path $repoRootFull ".agent-stages6-8-conversation-fix-backup\$stamp"
    $backup = Join-Path $backupRoot $relativePath
    $backupDirectory = Split-Path -Parent $backup

    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Copy-Item -LiteralPath $destination -Destination $backup -Force
    Copy-Item -LiteralPath $source -Destination $destination -Force

    # Force MSBuild to see the source as newer than previously built binaries.
    (Get-Item -LiteralPath $destination).LastWriteTimeUtc = [DateTime]::UtcNow

    if ((Get-Sha256Upper -Path $destination) -ne $expectedNewHash) {
        throw "Installed file hash mismatch."
    }

    Write-Host "Fixed: $relativePath"
    Write-Host "Backup: $backupRoot"
}
else {
    throw "Refusing to overwrite MainViewModel.cs because it differs from the installed Stages 6-8 version."
}

$git = Get-Command git.exe -ErrorAction SilentlyContinue
if ($git) {
    Write-Host ""
    Push-Location $repoRootFull
    try {
        & $git.Source diff --check
        if ($LASTEXITCODE -ne 0) {
            throw "git diff --check failed."
        }
        Write-Host "git diff --check: OK"
    }
    finally {
        Pop-Location
    }
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "dotnet.exe was not found."
}

Write-Host ""
Write-Host "Cleaning affected build outputs..."
$cleanPaths = @(
    'src\Kadr\bin',
    'src\Kadr\obj',
    'tests\KadrStudio.UiAdapters.Tests\bin',
    'tests\KadrStudio.UiAdapters.Tests\obj'
)
foreach ($relativeCleanPath in $cleanPaths) {
    $fullCleanPath = Join-Path $repoRootFull $relativeCleanPath
    Remove-Item -LiteralPath $fullCleanPath -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Clean: OK"

$testProject = Join-Path $repoRootFull 'tests\KadrStudio.UiAdapters.Tests\KadrStudio.UiAdapters.Tests.csproj'
Write-Host ""
Write-Host "Running UI adapter tests..."
Push-Location $repoRootFull
try {
    & $dotnet.Source test $testProject -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "UI adapter tests failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$appProject = Join-Path $repoRootFull 'src\Kadr\KadrStudio.csproj'
Write-Host ""
Write-Host "Building KadrStudio with warnings as errors..."
Push-Location $repoRootFull
try {
    & $dotnet.Source build $appProject -c Release -warnaserror
    if ($LASTEXITCODE -ne 0) {
        throw "KadrStudio build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Conversation builder fix validation: OK"

if ($git) {
    Write-Host ""
    Write-Host "Git status:"
    Push-Location $repoRootFull
    try {
        & $git.Source status --short
    }
    finally {
        Pop-Location
    }
}
