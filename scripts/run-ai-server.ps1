param(
    [string]$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$DataRoot = 'F:\KadrStudioData\AiServer',
    [string]$InstallRoot = (Join-Path $DataRoot 'runtime'),
    [string]$Listen = 'http://127.0.0.1:5080',
    [string]$ApiKey,
    [string]$BackendModel = 'qwen3-vl:4b-instruct',
    [string]$PublicModelAlias = 'kadr-vision:latest',
    [string]$OllamaEndpoint = 'http://127.0.0.1:11436',
    [string]$ModelsRoot = (Join-Path $DataRoot 'ollama-models'),
    [string]$OllamaExe,
    [switch]$NoAutoPull,
    [switch]$NoManageOllama,
    [switch]$BuildIfMissing
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-IsLoopbackHost {
    param([Parameter(Mandatory = $true)][string]$HostName)

    if ($HostName.Equals('localhost', [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $address = $null
    if ([System.Net.IPAddress]::TryParse($HostName, [ref]$address)) {
        return [System.Net.IPAddress]::IsLoopback($address)
    }

    return $false
}

function Resolve-OllamaExecutable {
    param(
        [string]$ExplicitPath,
        [string]$DataDirectory
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates.Add([Environment]::ExpandEnvironmentVariables($ExplicitPath))
    }

    $candidates.Add((Join-Path $DataDirectory 'ollama-runtime\ollama.exe'))

    $command = Get-Command ollama.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        $command = Get-Command ollama -ErrorAction SilentlyContinue
    }
    if ($command) {
        $candidates.Add($command.Source)
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

$repoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)
$installRootFull = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($InstallRoot))
$serverExe = Join-Path $installRootFull 'KadrStudio.AiServer.exe'

if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    if (-not $BuildIfMissing) {
        throw "Kadr AI Server runtime not found at $serverExe. Run scripts\install-ai-server-local.ps1 first or use -BuildIfMissing."
    }

    & (Join-Path $repoRootFull 'scripts\install-ai-server-local.ps1') `
        -RepoRoot $repoRootFull `
        -DataRoot $DataRoot `
        -InstallRoot $installRootFull
}

$listenUris = @(
    $Listen.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($listenUris.Count -eq 0) {
    throw 'At least one listen URL is required.'
}

$exposesNetwork = $false
foreach ($listenUrl in $listenUris) {
    $uri = $null
    if (-not [Uri]::TryCreate($listenUrl, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https')) {
        throw "Invalid listen URL: $listenUrl"
    }

    if (-not (Test-IsLoopbackHost -HostName $uri.Host)) {
        $exposesNetwork = $true
    }
}

if ($exposesNetwork -and [string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'A non-loopback AI server must be started with -ApiKey. The server also rejects unauthenticated remote requests.'
}

$resolvedOllama = Resolve-OllamaExecutable `
    -ExplicitPath $OllamaExe `
    -DataDirectory ([System.IO.Path]::GetFullPath($DataRoot))
if (-not $NoManageOllama -and -not $resolvedOllama) {
    Write-Warning 'Ollama executable was not found. Install Ollama or pass -OllamaExe. The server will stay up but readiness will fail.'
}

$modelsRootFull = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($ModelsRoot))
New-Item -ItemType Directory -Path $modelsRootFull -Force | Out-Null

$env:KADR_AI_URLS = $Listen
$env:KADR_AI_MODEL = $BackendModel
$env:KADR_AI_PUBLIC_MODEL = $PublicModelAlias
$env:KADR_AI_OLLAMA_ENDPOINT = $OllamaEndpoint
$env:KADR_AI_MODELS_ROOT = $modelsRootFull
$env:KADR_AI_MANAGE_OLLAMA = $(if ($NoManageOllama) { 'false' } else { 'true' })
$env:KADR_AI_AUTO_PULL = $(if ($NoAutoPull) { 'false' } else { 'true' })

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Remove-Item Env:KADR_AI_API_KEY -ErrorAction SilentlyContinue
} else {
    $env:KADR_AI_API_KEY = $ApiKey
}

if ($resolvedOllama) {
    $env:KADR_AI_OLLAMA_EXE = $resolvedOllama
} else {
    Remove-Item Env:KADR_AI_OLLAMA_EXE -ErrorAction SilentlyContinue
}

Write-Host 'Kadr AI Server' -ForegroundColor Cyan
Write-Host "Listen:       $Listen"
Write-Host "Ollama:       $OllamaEndpoint"
Write-Host "Model alias:  $PublicModelAlias"
Write-Host "Backend model:$BackendModel"
Write-Host "Models root:  $modelsRootFull"
if ($resolvedOllama) {
    Write-Host "Ollama exe:   $resolvedOllama"
}
Write-Host ''
Write-Host 'Kadr Studio on this PC connects to http://127.0.0.1:5080 by default.' -ForegroundColor Green
if ($exposesNetwork) {
    Write-Host 'Remote clients must set KADR_STUDIO_AI_ENDPOINT and KADR_STUDIO_AI_API_KEY.' -ForegroundColor Yellow
}
Write-Host 'Press Ctrl+C to stop the AI server.' -ForegroundColor DarkGray
Write-Host ''

& $serverExe
exit $LASTEXITCODE
