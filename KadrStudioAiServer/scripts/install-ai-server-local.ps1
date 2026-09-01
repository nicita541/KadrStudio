[CmdletBinding()]
param(
    [string]$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$DataRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\.kadr-ai')),
    [string]$InstallRoot = (Join-Path $DataRoot 'runtime'),
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Release',
    [switch]$SkipWorkerSetup,
    [switch]$SkipUserConfig
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)
$dataRootFull = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))
$installRootFull = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
if ($dataRootFull -match '^[A-Za-z]:\\?$') { throw 'DataRoot must be a dedicated directory, not a drive root.' }
if (-not $dataRootFull.StartsWith($repoRootFull + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataRoot must stay inside the Kadr Studio project.'
}
if (-not $installRootFull.StartsWith($dataRootFull + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InstallRoot must stay inside DataRoot.'
}
$project = Join-Path $repoRootFull 'src\Kadr.AiServer\KadrStudio.AiServer.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'Kadr AI Server project was not found.' }

New-Item -ItemType Directory -Path $dataRootFull -Force | Out-Null
$cacheRoot = Join-Path $dataRootFull 'cache'
$tempRoot = Join-Path $cacheRoot 'temp'
New-Item -ItemType Directory -Path $cacheRoot,$tempRoot -Force | Out-Null
$env:TEMP = $tempRoot
$env:TMP = $tempRoot
$env:DOTNET_CLI_HOME = Join-Path $cacheRoot 'dotnet'
$env:NUGET_PACKAGES = Join-Path $cacheRoot 'nuget'
& dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $installRootFull
if ($LASTEXITCODE -ne 0) { throw 'Kadr AI Server publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $installRootFull 'KadrStudio.AiServer.exe') -PathType Leaf)) {
    throw 'Published AI Server executable is missing.'
}

if (-not $SkipWorkerSetup) {
    & (Join-Path $PSScriptRoot 'setup-ai-workers.ps1') -AiRoot $dataRootFull -SkipUserConfig:$SkipUserConfig
    if ($LASTEXITCODE -ne 0) { throw 'Worker layout setup failed.' }
}

if (-not $SkipUserConfig) {
    [Environment]::SetEnvironmentVariable('KADR_AI_DATA_ROOT', $dataRootFull, 'User')
}
Write-Host "Kadr AI Server v2 installed at $installRootFull" -ForegroundColor Green
Write-Host 'Install worker dependencies and production models separately; .kadr-ai is ignored by Git.'
