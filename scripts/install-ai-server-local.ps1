param(
    [string]$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$DataRoot = 'F:\KadrStudioData\AiServer',
    [string]$InstallRoot = (Join-Path $DataRoot 'runtime'),
    [string]$OllamaRuntimeRoot = (Join-Path $DataRoot 'ollama-runtime'),
    [string]$ModelsRoot = (Join-Path $DataRoot 'ollama-models'),
    [switch]$FrameworkDependent,
    [switch]$SkipOllamaMigration,
    [switch]$SkipModelMigration
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-CompleteOllamaRuntime {
    param([Parameter(Mandatory = $true)][string]$Root)

    return (Test-Path -LiteralPath (Join-Path $Root 'ollama.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Root 'lib\ollama\llama-server.exe') -PathType Leaf)
}

function Resolve-FullOllamaRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$ExternalRuntimeRoot
    )

    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $candidates.Add((Join-Path $ExternalRuntimeRoot 'ollama.exe'))

    if ($env:LOCALAPPDATA) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'))
    }

    $command = Get-Command ollama.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        $command = Get-Command ollama -ErrorAction SilentlyContinue
    }
    if ($command) {
        $candidates.Add($command.Source)
    }

    # Old KadrStudio layout is deliberately last: it is a one-time migration source,
    # not a permanent runtime dependency of the new AI server.
    $candidates.Add((Join-Path $RepositoryRoot 'AI\ollama.exe'))
    $candidates.Add((Join-Path $RepositoryRoot 'tools\ollama.exe'))
    $candidates.Add((Join-Path $RepositoryRoot 'release\KadrStudio-win-x64\ai\ollama.exe'))

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $root = Split-Path -Parent $candidate
        if (Test-CompleteOllamaRuntime -Root $root) {
            return [pscustomobject]@{
                Executable = [System.IO.Path]::GetFullPath($candidate)
                Root = [System.IO.Path]::GetFullPath($root)
            }
        }
    }

    return $null
}

function Install-DirectoryAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$StagingPath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $parent = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null

    $previous = "$DestinationPath.previous-$([Guid]::NewGuid().ToString('N'))"
    $hadPrevious = Test-Path -LiteralPath $DestinationPath

    try {
        if ($hadPrevious) {
            Move-Item -LiteralPath $DestinationPath -Destination $previous
        }

        Move-Item -LiteralPath $StagingPath -Destination $DestinationPath
    }
    catch {
        $installError = $_.Exception.Message

        if (Test-Path -LiteralPath $DestinationPath) {
            Remove-Item -LiteralPath $DestinationPath -Recurse -Force -ErrorAction SilentlyContinue
        }

        if ($hadPrevious -and (Test-Path -LiteralPath $previous)) {
            try {
                Move-Item -LiteralPath $previous -Destination $DestinationPath
            }
            catch {
                throw "Failed to install $Label and failed to restore the previous runtime. Previous copy is preserved at '$previous'. Install error: $installError. Restore error: $($_.Exception.Message)"
            }
        }

        throw "Failed to install $Label safely: $installError"
    }
    finally {
        if (Test-Path -LiteralPath $StagingPath) {
            Remove-Item -LiteralPath $StagingPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if (Test-Path -LiteralPath $previous) {
        try {
            Remove-Item -LiteralPath $previous -Recurse -Force
        }
        catch {
            Write-Warning "Installed $Label successfully, but the previous external runtime could not be deleted: $previous. Remove it manually when no process is using it."
        }
    }
}

function Test-OllamaModelStore {
    param([Parameter(Mandatory = $true)][string]$Path)

    $blobs = Join-Path $Path 'blobs'
    $manifests = Join-Path $Path 'manifests'
    if (-not (Test-Path -LiteralPath $blobs -PathType Container) -or
        -not (Test-Path -LiteralPath $manifests -PathType Container)) {
        return $false
    }

    $blobFile = Get-ChildItem -LiteralPath $blobs -File -Force | Select-Object -First 1
    $manifestFile = Get-ChildItem -LiteralPath $manifests -File -Recurse -Force | Select-Object -First 1
    return $null -ne $blobFile -and $null -ne $manifestFile
}

function Test-DirectoryEmpty {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $true
    }

    return $null -eq (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1)
}

function Resolve-LegacyProjectModelStore {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    # We have used more than one development layout over time. Recognize only
    # directories that are verifiably Ollama stores (blobs + manifests). Unknown
    # untracked folders are deliberately not touched.
    $candidates = @(
        (Join-Path $RepositoryRoot 'AI\models'),
        (Join-Path $RepositoryRoot 'models'),
        (Join-Path $RepositoryRoot '.ollama\models'),
        (Join-Path $RepositoryRoot '.ollama')
    )

    foreach ($candidate in $candidates) {
        if (Test-OllamaModelStore -Path $candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Remove-EmptyLegacyParent {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$MovedStore
    )

    $legacyOllamaRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.ollama'))
    $parent = Split-Path -Parent $MovedStore
    if ($parent.Equals($legacyOllamaRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $legacyOllamaRoot -PathType Container) -and
        (Test-DirectoryEmpty -Path $legacyOllamaRoot)) {
        Remove-Item -LiteralPath $legacyOllamaRoot -Force
        Write-Host 'Removed now-empty legacy .ollama directory from the repository.' -ForegroundColor Green
    }
}

function Move-LegacyProjectModels {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $legacyRoot = Resolve-LegacyProjectModelStore -RepositoryRoot $RepositoryRoot
    if ([string]::IsNullOrWhiteSpace($legacyRoot)) {
        return $false
    }

    $legacyFull = [System.IO.Path]::GetFullPath($legacyRoot)
    $destinationFull = [System.IO.Path]::GetFullPath($DestinationRoot)
    if ($legacyFull.Equals($destinationFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if (-not (Test-DirectoryEmpty -Path $destinationFull)) {
        Write-Warning "Legacy project model store was found at $legacyFull, but destination already contains data: $destinationFull. It was left untouched to avoid an unsafe merge."
        return $false
    }

    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) {
        $git = Get-Command git -ErrorAction SilentlyContinue
    }
    if ($git) {
        $safeDirectory = ([System.IO.Path]::GetFullPath($RepositoryRoot)).Replace('\', '/')
        $tracked = @(
            & $git.Source `
                -c "safe.directory=$safeDirectory" `
                -C $RepositoryRoot `
                ls-files -- 'AI/models' 'models' '.ollama' 2>$null)
        if ($LASTEXITCODE -eq 0 -and $tracked.Count -gt 0) {
            Write-Warning 'A recognized legacy model-store location contains git-tracked files, so no project-local model store will be moved automatically.'
            return $false
        }
    }

    $destinationParent = Split-Path -Parent $destinationFull
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    if (Test-Path -LiteralPath $destinationFull) {
        Remove-Item -LiteralPath $destinationFull -Recurse -Force
    }

    Write-Host 'Moving existing Ollama model store out of the project...' -ForegroundColor Cyan
    Write-Host "From: $legacyFull"
    Write-Host "To:   $destinationFull"

    $sourceVolume = [System.IO.Path]::GetPathRoot($legacyFull)
    $destinationVolume = [System.IO.Path]::GetPathRoot($destinationFull)
    if ($sourceVolume.Equals($destinationVolume, [StringComparison]::OrdinalIgnoreCase)) {
        Move-Item -LiteralPath $legacyFull -Destination $destinationFull
    } else {
        $robocopy = Get-Command robocopy.exe -ErrorAction SilentlyContinue
        if (-not $robocopy) {
            throw 'The old model store and the new model root are on different drives, and robocopy.exe was not found. Pass -ModelsRoot on the same drive or move the model store manually.'
        }

        New-Item -ItemType Directory -Path $destinationFull -Force | Out-Null
        & $robocopy.Source $legacyFull $destinationFull /E /MOVE /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NP
        $robocopyExit = $LASTEXITCODE
        if ($robocopyExit -gt 7) {
            throw "robocopy failed while moving the legacy model store (exit code $robocopyExit). Source data was not intentionally deleted after the failure."
        }

        if (-not (Test-OllamaModelStore -Path $destinationFull)) {
            throw 'Cross-drive model migration did not produce a valid Ollama blobs/manifests layout. Any remaining source data was preserved.'
        }

        if (Test-Path -LiteralPath $legacyFull) {
            $remainingFile = Get-ChildItem -LiteralPath $legacyFull -File -Recurse -Force | Select-Object -First 1
            if ($remainingFile) {
                throw "Cross-drive model migration left source files in '$legacyFull'. They were preserved for safety; inspect the source and destination before retrying."
            }

            Remove-Item -LiteralPath $legacyFull -Recurse -Force
        }
    }

    if (-not (Test-OllamaModelStore -Path $destinationFull)) {
        throw 'Legacy model migration finished without a valid Ollama blobs/manifests layout.'
    }
    if (Test-Path -LiteralPath $legacyFull) {
        throw 'Legacy model migration did not remove the old project-local model store.'
    }

    Remove-EmptyLegacyParent -RepositoryRoot $RepositoryRoot -MovedStore $legacyFull
    Write-Host 'Existing models moved outside the repository; no second model copy is kept by this script.' -ForegroundColor Green
    return $true
}

$repoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)
$projectPath = Join-Path $repoRootFull 'src\Kadr.AiServer\KadrStudio.AiServer.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Kadr AI Server project not found: $projectPath"
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
}
if (-not $dotnet) {
    throw '.NET 10 SDK was not found.'
}

$installRootFull = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($InstallRoot))
$ollamaRuntimeRootFull = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($OllamaRuntimeRoot))
$modelsRootFull = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($ModelsRoot))

$installParent = Split-Path -Parent $installRootFull
$ollamaParent = Split-Path -Parent $ollamaRuntimeRootFull
New-Item -ItemType Directory -Path $installParent -Force | Out-Null
New-Item -ItemType Directory -Path $ollamaParent -Force | Out-Null

$runId = [Guid]::NewGuid().ToString('N')
$serverStaging = "$installRootFull.staging-$runId"
$ollamaStaging = "$ollamaRuntimeRootFull.staging-$runId"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('KadrStudio\AiServerBuild\' + $runId)
$artifactsRoot = Join-Path $tempRoot 'artifacts'

New-Item -ItemType Directory -Path $serverStaging -Force | Out-Null
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

Write-Host 'Publishing Kadr AI Server outside the repository...' -ForegroundColor Cyan
Write-Host "Source:  $projectPath"
Write-Host "Runtime: $installRootFull"
Write-Host "Temp:    $tempRoot"
Write-Host ''

$arguments = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $serverStaging,
    '--artifacts-path', $artifactsRoot,
    '--disable-build-servers',
    '-m:1',
    '-nr:false',
    '-p:TreatWarningsAsErrors=true'
)

if ($FrameworkDependent) {
    $arguments += @('--self-contained', 'false')
} else {
    $arguments += @('--self-contained', 'true')
}

try {
    & $dotnet.Source @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $stagedServerExe = Join-Path $serverStaging 'KadrStudio.AiServer.exe'
    if (-not (Test-Path -LiteralPath $stagedServerExe -PathType Leaf)) {
        throw "Published server executable not found: $stagedServerExe"
    }

    Install-DirectoryAtomically `
        -StagingPath $serverStaging `
        -DestinationPath $installRootFull `
        -Label 'Kadr AI Server runtime'

    $serverExe = Join-Path $installRootFull 'KadrStudio.AiServer.exe'
    Write-Host ''
    Write-Host 'Kadr AI Server runtime installed.' -ForegroundColor Green
    Write-Host "Executable: $serverExe"

    if (-not $SkipOllamaMigration) {
        if (Test-CompleteOllamaRuntime -Root $ollamaRuntimeRootFull) {
            Write-Host 'External Ollama runtime is already complete; migration skipped.' -ForegroundColor DarkGray
        } else {
            $ollamaSource = Resolve-FullOllamaRuntime `
                -RepositoryRoot $repoRootFull `
                -ExternalRuntimeRoot $ollamaRuntimeRootFull
            if ($ollamaSource) {
                New-Item -ItemType Directory -Path $ollamaStaging -Force | Out-Null
                Copy-Item -LiteralPath $ollamaSource.Executable `
                    -Destination (Join-Path $ollamaStaging 'ollama.exe') -Force
                Copy-Item -LiteralPath (Join-Path $ollamaSource.Root 'lib') `
                    -Destination (Join-Path $ollamaStaging 'lib') -Recurse -Force

                if (-not (Test-CompleteOllamaRuntime -Root $ollamaStaging)) {
                    throw 'Ollama migration did not contain ollama.exe + lib\ollama\llama-server.exe.'
                }

                Install-DirectoryAtomically `
                    -StagingPath $ollamaStaging `
                    -DestinationPath $ollamaRuntimeRootFull `
                    -Label 'Ollama runtime'

                Write-Host 'Ollama runtime copied outside the repository.' -ForegroundColor Green
                Write-Host "Ollama:     $(Join-Path $ollamaRuntimeRootFull 'ollama.exe')"
            } else {
                Write-Warning 'A complete Ollama runtime was not found for migration. Install Ollama normally or pass -OllamaExe when starting the server.'
            }
        }
    }

    if (-not $SkipModelMigration) {
        [void](Move-LegacyProjectModels `
            -RepositoryRoot $repoRootFull `
            -DestinationRoot $modelsRootFull)
    }

    New-Item -ItemType Directory -Path $modelsRootFull -Force | Out-Null
    Write-Host "Models root: $modelsRootFull"
    Write-Host 'No model/build working files were created in the repository.' -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $serverStaging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $ollamaStaging -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
