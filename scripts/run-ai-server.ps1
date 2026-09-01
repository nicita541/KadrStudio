[CmdletBinding()]
param(
    [string]$DataRoot = '',
    [string]$InstallRoot = '',
    [string]$Listen = 'http://127.0.0.1:5080',
    [string]$ApiKey,
    [string]$VisionModel = 'Qwen/Qwen3-VL-8B-Instruct-GGUF',
    [string]$PlannerModel = 'Qwen/Qwen3-30B-A3B-Instruct-2507',
    [switch]$BuildIfMissing
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) { Join-Path $projectRoot '.kadr-ai' } else { $DataRoot }
$InstallRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) { Join-Path $DataRoot 'runtime' } else { $InstallRoot }

function Test-IsLoopbackHost {
    param([Parameter(Mandatory)][string]$HostName)
    if ($HostName.Equals('localhost', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $address = $null
    return [System.Net.IPAddress]::TryParse($HostName, [ref]$address) -and
        [System.Net.IPAddress]::IsLoopback($address)
}

$dataRootFull = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($DataRoot))
if ($dataRootFull -match '^[A-Za-z]:\\?$') { throw 'DataRoot must be a dedicated directory, not a drive root.' }
if (-not $dataRootFull.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataRoot must stay inside the Kadr Studio project.'
}
$installRootFull = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($InstallRoot))
$serverExe = Join-Path $installRootFull 'KadrStudio.AiServer.exe'
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    if (-not $BuildIfMissing) {
        throw "Kadr AI Server runtime was not found at $serverExe. Run install-ai-server-local.ps1 first."
    }
    & (Join-Path $PSScriptRoot 'install-ai-server-local.ps1') -DataRoot $dataRootFull -InstallRoot $installRootFull
    if ($LASTEXITCODE -ne 0) { throw 'AI Server installation failed.' }
}

$listenUris = @($Listen.Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
if ($listenUris.Count -eq 0) { throw 'At least one listen URL is required.' }
$exposesNetwork = $false
foreach ($listenUrl in $listenUris) {
    $uri = $null
    if (-not [Uri]::TryCreate($listenUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https')) {
        throw "Invalid listen URL: $listenUrl"
    }
    if (-not (Test-IsLoopbackHost -HostName $uri.Host)) { $exposesNetwork = $true }
}
if ($exposesNetwork -and [string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'A non-loopback AI server must be started with -ApiKey.'
}

$workersRoot = Join-Path $dataRootFull 'workers'
$modelsRoot = Join-Path $dataRootFull 'models'
$runtimeDataRoot = Join-Path $dataRootFull 'data'
$cacheRoot = Join-Path $dataRootFull 'cache'
$tempRoot = Join-Path $cacheRoot 'temp'
New-Item -ItemType Directory -Path $runtimeDataRoot,$cacheRoot,$tempRoot -Force | Out-Null
$requiredWorkers = @('video-understanding', 'audio-events', 'embedding', 'director', 'critic')
$missingWorkers = @($requiredWorkers | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $workersRoot "$_\worker-manifest.json") -PathType Leaf)
})
if ($missingWorkers.Count -gt 0) {
    throw "Worker setup is incomplete. Missing: $($missingWorkers -join ', '). Run setup-ai-workers.ps1."
}

function Resolve-ProjectModelDirectory {
    param(
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$DefaultDirectory,
        [Parameter(Mandatory)][string]$DisplayName
    )
    $configured = [Environment]::GetEnvironmentVariable($EnvironmentName)
    $candidate = if ([string]::IsNullOrWhiteSpace($configured)) {
        Join-Path $modelsRoot $DefaultDirectory
    } else {
        $configured
    }
    $resolved = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($candidate))
    if (-not $resolved.StartsWith(
            [System.IO.Path]::GetFullPath($modelsRoot) + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $modelsRoot $DefaultDirectory))
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $modelsRoot $DefaultDirectory))
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$DisplayName was not found at $resolved. Run install-production-models.ps1."
    }
    return $resolved
}

function Resolve-ProjectModelFile {
    param(
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$DefaultRelativePath,
        [Parameter(Mandatory)][string]$DisplayName
    )
    $configured = [Environment]::GetEnvironmentVariable($EnvironmentName)
    $candidate = if ([string]::IsNullOrWhiteSpace($configured)) {
        Join-Path $modelsRoot $DefaultRelativePath
    } else {
        $configured
    }
    $resolved = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($candidate))
    if (-not $resolved.StartsWith(
            [System.IO.Path]::GetFullPath($modelsRoot) + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $modelsRoot $DefaultRelativePath))
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        $resolved = [System.IO.Path]::GetFullPath((Join-Path $modelsRoot $DefaultRelativePath))
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$DisplayName was not found at $resolved. Run install-production-models.ps1."
    }
    return $resolved
}

$localVisionModel = Resolve-ProjectModelFile `
    'KADR_VISION_MODEL' `
    'qwen3-vl-8b-instruct-q4-k-m\Qwen3VL-8B-Instruct-Q4_K_M.gguf' `
    'Vision GGUF model'
$localVisionProjector = Resolve-ProjectModelFile `
    'KADR_VISION_MMPROJ' `
    'qwen3-vl-8b-instruct-q4-k-m\mmproj-Qwen3VL-8B-Instruct-Q8_0.gguf' `
    'Vision multimodal projector'
$localDirectorModel = Resolve-ProjectModelFile `
    'KADR_DIRECTOR_MODEL' `
    'qwen3-30b-a3b-instruct-2507-q4-k-m\Qwen3-30B-A3B-Instruct-2507-Q4_K_M.gguf' `
    'Director GGUF model'
$localEmbeddingModel = Resolve-ProjectModelDirectory `
    'KADR_EMBEDDING_MODEL' 'multilingual-minilm-l12-int8' 'Embedding model'
$localAsrModel = Resolve-ProjectModelDirectory 'KADR_ASR_MODEL' 'faster-whisper-large-v3' 'ASR model'
$localLlamaServer = [System.IO.Path]::GetFullPath((Join-Path $dataRootFull 'runtime\llama.cpp\llama-server.exe'))
if (-not (Test-Path -LiteralPath $localLlamaServer -PathType Leaf)) {
    $localLlamaServer = Get-ChildItem -LiteralPath (Join-Path $dataRootFull 'runtime\llama.cpp') `
        -Filter 'llama-server.exe' -File -Recurse -ErrorAction SilentlyContinue | `
        Select-Object -ExpandProperty FullName -First 1
}
if ([string]::IsNullOrWhiteSpace($localLlamaServer)) {
    throw 'llama-server.exe is missing inside the project runtime. Run install-production-models.ps1.'
}

$env:KADR_AI_DATA_ROOT = $dataRootFull
$env:KADR_AI_WORKERS_ROOT = $workersRoot
$env:KADR_AI_PRODUCTION_MODELS_ROOT = $modelsRoot
$env:KADR_AI_RUNTIME_DATA_ROOT = $runtimeDataRoot
$env:KADR_AI_VISION_MODEL = $VisionModel
$env:KADR_AI_PLANNER_MODEL = $PlannerModel
$env:KADR_VISION_MODEL = $localVisionModel
$env:KADR_VISION_MMPROJ = $localVisionProjector
$env:KADR_DIRECTOR_MODEL = $localDirectorModel
$env:KADR_EMBEDDING_MODEL = $localEmbeddingModel
$env:KADR_ASR_MODEL = $localAsrModel
$env:KADR_REASONING_BACKEND = 'llama.cpp'
$env:KADR_LLAMA_SERVER = $localLlamaServer
$env:KADR_PLANNER_CONTEXT_TOKENS = $(if ($env:KADR_PLANNER_CONTEXT_TOKENS) { $env:KADR_PLANNER_CONTEXT_TOKENS } else { '32768' })
$env:KADR_LLAMA_GPU_LAYERS = $(if ($env:KADR_LLAMA_GPU_LAYERS) { $env:KADR_LLAMA_GPU_LAYERS } else { '12' })
$env:KADR_VISION_CONTEXT_TOKENS = $(if ($env:KADR_VISION_CONTEXT_TOKENS) { $env:KADR_VISION_CONTEXT_TOKENS } else { '8192' })
$env:KADR_VISION_GPU_LAYERS = $(if ($env:KADR_VISION_GPU_LAYERS) { $env:KADR_VISION_GPU_LAYERS } else { '99' })
$env:KADR_LLAMA_THREADS = $(if ($env:KADR_LLAMA_THREADS) { $env:KADR_LLAMA_THREADS } else { '12' })
$configuredVisionFrames = if ($env:KADR_VISION_MAX_FRAMES) { [int]$env:KADR_VISION_MAX_FRAMES } else { 48 }
$env:KADR_VISION_MAX_FRAMES = [Math]::Max(48, $configuredVisionFrames).ToString(
    [System.Globalization.CultureInfo]::InvariantCulture)
$env:KADR_VISION_BATCH_FRAMES = $(if ($env:KADR_VISION_BATCH_FRAMES) { $env:KADR_VISION_BATCH_FRAMES } else { '12' })
$env:TEMP = $tempRoot
$env:TMP = $tempRoot
$env:HF_HOME = Join-Path $cacheRoot 'huggingface'
$env:TORCH_HOME = Join-Path $cacheRoot 'torch'
$env:CUDA_CACHE_PATH = Join-Path $cacheRoot 'cuda'
$env:TORCHINDUCTOR_CACHE_DIR = Join-Path $cacheRoot 'torchinductor'
$env:TRITON_CACHE_DIR = Join-Path $cacheRoot 'triton'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:KADR_AI_URLS = $Listen
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Remove-Item Env:KADR_AI_API_KEY -ErrorAction SilentlyContinue
} else {
    $env:KADR_AI_API_KEY = $ApiKey
}

Write-Host 'Kadr AI Server v2' -ForegroundColor Cyan
Write-Host "Listen:        $Listen"
Write-Host "Workers:       $workersRoot"
Write-Host "Vision model:  $VisionModel"
Write-Host "Planner model: $PlannerModel"
Write-Host "Model store:   $modelsRoot"
Write-Host 'Anime OP/ED: pinned Vision plus the constrained 30B Director brief are enabled.' -ForegroundColor Yellow
Write-Host 'General 30B RoughCut/Critic roles remain gated.' -ForegroundColor Yellow
& $serverExe
exit $LASTEXITCODE
