[CmdletBinding()]
param(
    [string]$AiRoot = '',
    [string]$ManifestPath = '',
    [string]$AnimeSrCheckpoint = 'F:\upscalerAI\AnimeSR-X\checkpoints\experiments\first_short_run\best\AnimeSR-X_first_short_run_best.pth',
    [switch]$ConfirmDownload,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$AiRoot = if ([string]::IsNullOrWhiteSpace($AiRoot)) { Join-Path $projectRoot '.kadr-ai' } else { $AiRoot }
$ManifestPath = if ([string]::IsNullOrWhiteSpace($ManifestPath)) { Join-Path $projectRoot 'config\ai-model-pack.production.json' } else { $ManifestPath }
$resolvedRoot = [System.IO.Path]::GetFullPath($AiRoot)
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
if (-not $resolvedRoot.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'AiRoot must stay inside the Kadr Studio project.'
}
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw "Model manifest not found: $ManifestPath" }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw "Unsupported model manifest schema: $($manifest.schemaVersion)" }
$modelsRoot = Join-Path $resolvedRoot 'models'
$python = Join-Path $resolvedRoot 'workers\runtime\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw 'Worker runtime is missing. Run setup-ai-workers.ps1 -InstallDependencies first.'
}

function Get-DirectoryBytes([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return [long]0 }
    return [long](Get-ChildItem -LiteralPath $path -File -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
}

function Test-VerifiedFile([string]$path, [long]$size, [string]$sha256) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne $size) { return $false }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.Equals($sha256, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-VerifiedDirectoryIdentity([string]$path, [string]$revision) {
    $files = @(Get-ChildItem -LiteralPath $path -File -Recurse |
        Where-Object { $_.Name -ne 'kadr-snapshot.json' -and -not $_.Name.EndsWith('.kadr-part', [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object FullName)
    if ($files.Count -eq 0) { throw "Model directory contains no payload: $path" }
    $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    foreach ($file in $files) {
        $relative = [System.IO.Path]::GetRelativePath($path, $file.FullName)
        $sha.AppendData([System.Text.Encoding]::UTF8.GetBytes($relative))
        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $buffer = New-Object byte[] 1048576
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) { $sha.AppendData($buffer, 0, $read) }
        } finally { $stream.Dispose() }
    }
    $lastWrite = ($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    return [ordered]@{
        path = [System.IO.Path]::GetFullPath($path)
        sizeBytes = [long]($files | Measure-Object -Property Length -Sum).Sum
        lastWriteTimeUtc = ([DateTimeOffset]$lastWrite).ToUniversalTime().ToString('O')
        sha256 = [Convert]::ToHexString($sha.GetHashAndReset()).ToLowerInvariant()
        revision = $revision
    }
}

$rows = [System.Collections.Generic.List[object]]::new()
[long]$packBytes = 0
foreach ($artifact in $manifest.artifacts) {
    if ($artifact.files) {
        foreach ($file in $artifact.files) {
            $packBytes += [long]$file.size
            $rows.Add([pscustomobject]@{
                Role = $artifact.id
                Model = $artifact.displayName
                File = $file.path
                Bytes = [long]$file.size
                Sha256 = $file.sha256
            })
        }
    } elseif ($artifact.localFile) {
        $packBytes += [long]$artifact.localFile.size
        $rows.Add([pscustomobject]@{
            Role = $artifact.id
            Model = $artifact.displayName
            File = $artifact.localFile.targetPath
            Bytes = [long]$artifact.localFile.size
            Sha256 = $artifact.localFile.sha256
        })
    } else {
        throw "Artifact $($artifact.id) contains no exact files."
    }
}
if ($packBytes -gt [long]$manifest.modelBudgetBytes) {
    throw "Manifest exceeds model budget: $packBytes > $($manifest.modelBudgetBytes) bytes."
}

Write-Host "Pinned model pack: $($manifest.displayName)"
$rows | Select-Object Role,Model,File,@{n='SizeGiB';e={[math]::Round($_.Bytes / 1GB, 3)}},Sha256 | Format-Table -AutoSize -Wrap | Out-Host
Write-Host ("Exact payload: {0:N0} bytes ({1:N2} GiB)." -f $packBytes, ($packBytes / 1GB))
Write-Host ("Hard limits: models {0:N2} GiB; complete .kadr-ai {1:N2} GiB." -f `
    ([long]$manifest.modelBudgetBytes / 1GB), ([long]$manifest.aiRootBudgetBytes / 1GB))
if ($PlanOnly) { return }
if (-not $ConfirmDownload) {
    throw 'Download was not confirmed. Review the exact table and rerun with -ConfirmDownload.'
}

$driveName = [System.IO.Path]::GetPathRoot($resolvedRoot).Substring(0,1)
$drive = Get-PSDrive -Name $driveName
$currentRootBytes = Get-DirectoryBytes $resolvedRoot
$expectedRootBytes = $currentRootBytes
[long]$remainingBytes = 0
foreach ($artifact in $manifest.artifacts) {
    $target = Join-Path $modelsRoot $artifact.targetDirectory
    $files = if ($artifact.files) { @($artifact.files) } else { @($artifact.localFile) }
    foreach ($file in $files) {
        $relative = if ($artifact.files) { [string]$file.path } else { [string]$file.targetPath }
        $destination = Join-Path $target $relative.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        [long]$existingBytes = 0
        if (Test-Path -LiteralPath $destination -PathType Leaf) { $existingBytes += (Get-Item -LiteralPath $destination).Length }
        $partial = $destination + '.kadr-part'
        if (Test-Path -LiteralPath $partial -PathType Leaf) { $existingBytes += (Get-Item -LiteralPath $partial).Length }
        if (-not (Test-VerifiedFile $destination ([long]$file.size) ([string]$file.sha256))) {
            $expectedRootBytes = $expectedRootBytes - $existingBytes + [long]$file.size
            $remainingBytes += [math]::Max([long]0, [long]$file.size - $existingBytes)
        }
    }
}
$requiredHeadroom = $remainingBytes + 2GB
if ([long]$drive.Free -lt $requiredHeadroom) {
    throw "Not enough free space. Required headroom is $requiredHeadroom bytes; available is $($drive.Free)."
}
if ($expectedRootBytes -gt [long]$manifest.aiRootBudgetBytes) {
    throw "Installation would exceed the complete .kadr-ai budget. Current=$currentRootBytes, expected=$expectedRootBytes."
}

New-Item -ItemType Directory -Path $modelsRoot -Force | Out-Null
foreach ($artifact in $manifest.artifacts) {
    $target = Join-Path $modelsRoot $artifact.targetDirectory
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if ($artifact.files) {
        if ($artifact.revision -notmatch '^[0-9a-f]{40}$') { throw "Revision for $($artifact.id) is not pinned." }
        foreach ($file in $artifact.files) {
            $destination = Join-Path $target ([string]$file.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            if (Test-VerifiedFile $destination ([long]$file.size) ([string]$file.sha256)) {
                Write-Host "Verified, skip: $($artifact.id)/$($file.path)"
                continue
            }
            & $python (Join-Path $PSScriptRoot 'download-hf-snapshot.py') `
                --repository $artifact.repository `
                --revision $artifact.revision `
                --target $target `
                --include $file.path `
                --expected-size $file.size `
                --sha256 $file.sha256 `
                --chunk-mib 64
            if ($LASTEXITCODE -ne 0) { throw "Download failed for $($artifact.id)/$($file.path)." }
        }
    } else {
        if (-not (Test-VerifiedFile $AnimeSrCheckpoint ([long]$artifact.localFile.size) ([string]$artifact.localFile.sha256))) {
            throw "AnimeSR-X checkpoint is missing or does not match the approved SHA-256: $AnimeSrCheckpoint"
        }
        $destination = Join-Path $target $artifact.localFile.targetPath
        if (-not (Test-VerifiedFile $destination ([long]$artifact.localFile.size) ([string]$artifact.localFile.sha256))) {
            Copy-Item -LiteralPath $AnimeSrCheckpoint -Destination $destination -Force
        }
    }
}

$modelsBytes = Get-DirectoryBytes $modelsRoot
$rootBytes = Get-DirectoryBytes $resolvedRoot
$allowedFiles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($artifact in $manifest.artifacts) {
    $target = Join-Path $modelsRoot $artifact.targetDirectory
    if ($artifact.files) {
        foreach ($file in $artifact.files) {
            [void]$allowedFiles.Add([System.IO.Path]::GetFullPath((Join-Path $target ([string]$file.path).Replace('/', [System.IO.Path]::DirectorySeparatorChar))))
        }
    } else {
        [void]$allowedFiles.Add([System.IO.Path]::GetFullPath((Join-Path $target ([string]$artifact.localFile.targetPath))))
    }
}
$visionArtifact = @($manifest.artifacts | Where-Object id -eq 'vision' | Select-Object -First 1)
if ($visionArtifact.Count -ne 1) { throw 'Pinned vision artifact is missing from the model manifest.' }
$visionWeight = @($visionArtifact[0].files | Where-Object { [string]$_.path -notmatch '^mmproj-' } | Select-Object -First 1)
if ($visionWeight.Count -ne 1) { throw 'Pinned vision GGUF is missing from the model manifest.' }
$capabilitiesRoot = Join-Path $modelsRoot 'capabilities'
$visionModelName = [string]$visionArtifact[0].repository
$safeVisionName = -join ($visionModelName.ToCharArray() | ForEach-Object {
    if ([char]::IsLetterOrDigit($_) -or $_ -in @('-', '_')) { $_ } else { '_' }
})
$visionCapabilityPath = [System.IO.Path]::GetFullPath((Join-Path $capabilitiesRoot "$safeVisionName.json"))
[void]$allowedFiles.Add($visionCapabilityPath)
$plannerArtifact = @($manifest.artifacts | Where-Object id -eq 'planner' | Select-Object -First 1)
if ($plannerArtifact.Count -ne 1) { throw 'Pinned planner artifact is missing from the model manifest.' }
$plannerWeight = @($plannerArtifact[0].files | Select-Object -First 1)
if ($plannerWeight.Count -ne 1) { throw 'Pinned planner GGUF is missing from the model manifest.' }
# The public server alias is stable even though the pinned GGUF is distributed
# by lmstudio-community. Capability checks intentionally use that stable alias.
$plannerModelName = 'Qwen/Qwen3-30B-A3B-Instruct-2507'
$safePlannerName = -join ($plannerModelName.ToCharArray() | ForEach-Object {
    if ([char]::IsLetterOrDigit($_) -or $_ -in @('-', '_')) { $_ } else { '_' }
})
$plannerCapabilityPath = [System.IO.Path]::GetFullPath((Join-Path $capabilitiesRoot "$safePlannerName.json"))
[void]$allowedFiles.Add($plannerCapabilityPath)
$unexpected = @(Get-ChildItem -LiteralPath $modelsRoot -File -Recurse | Where-Object {
    -not $allowedFiles.Contains([System.IO.Path]::GetFullPath($_.FullName))
})
if ($unexpected.Count -gt 0) {
    throw "Model store contains files outside the approved manifest: $($unexpected.FullName -join ', ')"
}
if ($modelsBytes -gt [long]$manifest.modelBudgetBytes) { throw "Installed models exceed budget: $modelsBytes bytes." }
if ($rootBytes -gt [long]$manifest.aiRootBudgetBytes) { throw "Complete .kadr-ai exceeds budget: $rootBytes bytes." }

# First release qualification is intentionally narrower than a general montage
# eval. Vision classifies the measured candidates; the 30B model receives only
# a compact Director brief for the exact anime OP/ED workflow. RoughCut and
# Critic roles remain gated until their separate montage eval is completed.
New-Item -ItemType Directory -Path $capabilitiesRoot -Force | Out-Null
$visionModelDirectory = Join-Path $modelsRoot ([string]$visionArtifact[0].targetDirectory)
$visionIdentity = Get-VerifiedDirectoryIdentity $visionModelDirectory ([string]$visionArtifact[0].revision)
$visionCapability = [ordered]@{
    model = $visionModelName
    modelHash = [string]$visionIdentity.sha256
    tokenizer = 'qwen3-vl-gguf'
    contextWindowTokens = 8192
    supportedRoles = @('VideoUnderstanding')
    supportedProfiles = @('anime-episode')
    montageEvalRevision = 'pinned-real-anime-op-ed-v1'
    montageEvalPassed = $false
    productionApproved = $true
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    qualificationKind = 'pinned-anime-exact'
    verifiedIdentity = $visionIdentity
}
$visionCapabilityPart = $visionCapabilityPath + '.part'
$visionCapability | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $visionCapabilityPart -Encoding UTF8
Move-Item -LiteralPath $visionCapabilityPart -Destination $visionCapabilityPath -Force
$plannerModelDirectory = Join-Path $modelsRoot ([string]$plannerArtifact[0].targetDirectory)
$plannerIdentity = Get-VerifiedDirectoryIdentity $plannerModelDirectory ([string]$plannerArtifact[0].revision)
$plannerCapability = [ordered]@{
    model = $plannerModelName
    modelHash = [string]$plannerIdentity.sha256
    tokenizer = 'qwen3-gguf'
    contextWindowTokens = 32768
    supportedRoles = @('Director')
    supportedProfiles = @('anime-episode')
    montageEvalRevision = 'pinned-anime-director-brief-v1'
    montageEvalPassed = $false
    productionApproved = $true
    evaluatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    qualificationKind = 'pinned-anime-director'
    verifiedIdentity = $plannerIdentity
}
$plannerCapabilityPart = $plannerCapabilityPath + '.part'
$plannerCapability | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $plannerCapabilityPart -Encoding UTF8
Move-Item -LiteralPath $plannerCapabilityPart -Destination $plannerCapabilityPath -Force
$modelsBytes = Get-DirectoryBytes $modelsRoot
$rootBytes = Get-DirectoryBytes $resolvedRoot
if ($modelsBytes -gt [long]$manifest.modelBudgetBytes) { throw "Installed models exceed budget: $modelsBytes bytes." }
if ($rootBytes -gt [long]$manifest.aiRootBudgetBytes) { throw "Complete .kadr-ai exceeds budget: $rootBytes bytes." }

$installation = [ordered]@{
    schemaVersion = 1
    manifestId = $manifest.id
    installedAt = [DateTimeOffset]::UtcNow.ToString('O')
    payloadBytes = $packBytes
    modelsBytes = $modelsBytes
    aiRootBytes = $rootBytes
    artifacts = $manifest.artifacts
}
$installationPath = Join-Path $resolvedRoot 'model-pack.installation.json'
$installationPart = $installationPath + '.part'
$installation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $installationPart -Encoding UTF8
Move-Item -LiteralPath $installationPart -Destination $installationPath -Force
Write-Host "Verified model pack installed. Models=$modelsBytes bytes; complete .kadr-ai=$rootBytes bytes."
Write-Host "Activated pinned anime exact profile for $visionModelName and constrained Director brief for $plannerModelName."
Write-Host 'General RoughCut/Critic planner use remains gated.'
