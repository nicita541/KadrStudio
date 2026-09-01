[CmdletBinding()]
param(
    [string]$AiRoot = '',
    [switch]$InstallDependencies,
    [switch]$SkipUserConfig,
    [string]$TorchIndexUrl = 'https://download.pytorch.org/whl/cu128'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$AiRoot = if ([string]::IsNullOrWhiteSpace($AiRoot)) { Join-Path $sourceRoot '.kadr-ai' } else { $AiRoot }
$workerSource = Join-Path $sourceRoot 'workers\python'
$resolvedRoot = [System.IO.Path]::GetFullPath($AiRoot)
if ([string]::IsNullOrWhiteSpace($resolvedRoot) -or $resolvedRoot -match '^[A-Za-z]:\\?$') {
    throw 'AiRoot must be a dedicated directory, not a drive root.'
}
if (-not $resolvedRoot.StartsWith($sourceRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AiRoot must stay inside the Kadr Studio project.'
}

$workersRoot = Join-Path $resolvedRoot 'workers'
$modelsRoot = Join-Path $resolvedRoot 'models'
$dataRoot = Join-Path $resolvedRoot 'data'
$evalRoot = Join-Path $resolvedRoot 'eval'
$cacheRoot = Join-Path $resolvedRoot 'cache'
$tempRoot = Join-Path $cacheRoot 'temp'
$runtimeRoot = Join-Path $workersRoot 'runtime'
$directories = @($resolvedRoot, $workersRoot, $modelsRoot, $dataRoot, $evalRoot, $cacheRoot, $tempRoot)
foreach ($directory in $directories) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
$env:TEMP = $tempRoot
$env:TMP = $tempRoot
$env:PIP_CACHE_DIR = Join-Path $cacheRoot 'pip'
$env:HF_HOME = Join-Path $cacheRoot 'huggingface'
$env:TORCH_HOME = Join-Path $cacheRoot 'torch'
$env:CUDA_CACHE_PATH = Join-Path $cacheRoot 'cuda'
$env:TORCHINDUCTOR_CACHE_DIR = Join-Path $cacheRoot 'torchinductor'
$env:TRITON_CACHE_DIR = Join-Path $cacheRoot 'triton'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'

$packageTarget = Join-Path $workersRoot 'kadr_worker'
New-Item -ItemType Directory -Path $packageTarget -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workerSource 'kadr_worker\__init__.py') -Destination $packageTarget -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'kadr_worker\proto_wire.py') -Destination $packageTarget -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'kadr_worker\analyzers.py') -Destination $packageTarget -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'kadr_worker\animesr_upscale.py') -Destination $packageTarget -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'kadr_worker\server.py') -Destination $packageTarget -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'requirements.lock') -Destination (Join-Path $workersRoot 'requirements.lock') -Force

if ($InstallDependencies) {
    $python = Get-Command python -ErrorAction Stop
    & $python.Source -m venv $runtimeRoot
    if ($LASTEXITCODE -ne 0) { throw 'Python venv creation failed.' }
    $runtimePython = Join-Path $runtimeRoot 'Scripts\python.exe'
    & $runtimePython -m pip install --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw 'pip bootstrap failed.' }
    & $runtimePython -m pip install 'torch==2.8.0' --index-url $TorchIndexUrl
    if ($LASTEXITCODE -ne 0) { throw 'CUDA 12.8 PyTorch installation failed.' }
    & $runtimePython -m pip install --requirement (Join-Path $workersRoot 'requirements.lock')
    if ($LASTEXITCODE -ne 0) { throw 'Worker dependency installation failed.' }
}

$runtimePython = Join-Path $runtimeRoot 'Scripts\python.exe'
$analyzers = @(
    @{ Name = 'video-understanding'; Port = 52101 },
    @{ Name = 'audio-events'; Port = 52102 },
    @{ Name = 'asr-align'; Port = 52103 },
    @{ Name = 'diarization'; Port = 52104 },
    @{ Name = 'embedding'; Port = 52105 },
    @{ Name = 'director'; Port = 52106 },
    @{ Name = 'critic'; Port = 52107 },
    @{ Name = 'anime-upscale'; Port = 52108 }
)
foreach ($item in $analyzers) {
    $directory = Join-Path $workersRoot $item.Name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $manifest = [ordered]@{
        analyzer = $item.Name
        protocolVersion = '2'
        executable = 'runtime/Scripts/python.exe'
        arguments = @('-m', 'kadr_worker.server', '--analyzer', $item.Name)
        port = $item.Port
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'worker-manifest.json') -Encoding UTF8
}

$layout = [ordered]@{
    schemaVersion = 1
    root = $resolvedRoot
    workers = $workersRoot
    models = $modelsRoot
    data = $dataRoot
    eval = $evalRoot
    runtimeReady = (Test-Path -LiteralPath $runtimePython)
    configuredAt = [DateTimeOffset]::UtcNow.ToString('O')
}
$layout | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolvedRoot 'layout.json') -Encoding UTF8

$dataParent = Split-Path -Parent $resolvedRoot
try {
    $gitRoot = (& git -C $dataParent rev-parse --show-toplevel 2>$null).Trim()
    if ($LASTEXITCODE -eq 0 -and $gitRoot) {
        $gitRoot = [System.IO.Path]::GetFullPath($gitRoot)
        $gitPrefix = $gitRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedRoot.StartsWith($gitPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'AI runtime is outside the resolved Git repository.'
        }
        $relative = $resolvedRoot.Substring($gitPrefix.Length).Replace('\', '/')
        $excludePath = Join-Path $gitRoot '.git\info\exclude'
        $excludeDirectory = Split-Path -Parent $excludePath
        New-Item -ItemType Directory -Path $excludeDirectory -Force | Out-Null
        $pattern = "/$relative/"
        $existing = if (Test-Path -LiteralPath $excludePath) { Get-Content -LiteralPath $excludePath } else { @() }
        if ($existing -notcontains $pattern) { Add-Content -LiteralPath $excludePath -Value $pattern -Encoding UTF8 }
        $status = & git -C $gitRoot status --short -- $relative
        if ($status) { throw "AI runtime is still visible to Git: $status" }
    }
} catch {
    throw "Failed to protect AI runtime from Git commits: $($_.Exception.Message)"
}

if (-not $SkipUserConfig) {
    [Environment]::SetEnvironmentVariable('KADR_AI_DATA_ROOT', $resolvedRoot, 'User')
    [Environment]::SetEnvironmentVariable('KADR_AI_WORKERS_ROOT', $workersRoot, 'User')
    [Environment]::SetEnvironmentVariable('KADR_AI_PRODUCTION_MODELS_ROOT', $modelsRoot, 'User')
}

Write-Host "Kadr AI worker layout prepared at $resolvedRoot"
if (-not (Test-Path -LiteralPath $runtimePython)) {
    Write-Host 'Runtime is not installed. Re-run with -InstallDependencies after selecting the CUDA/PyTorch build for this machine.'
}
