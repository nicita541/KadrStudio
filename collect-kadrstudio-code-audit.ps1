param(
    [string]$RepoRoot = 'F:\KadrStudio',
    [string]$OutputZip = '',
    [int]$MaxFileSizeMB = 25,
    [switch]$IncludeBinaries
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Normalize-RelPath([string]$Path) {
    return ($Path -replace '\\', '/').TrimStart('./')
}

function Is-RuntimePath([string]$RelativePath) {
    $p = '/' + (Normalize-RelPath $RelativePath).ToLowerInvariant() + '/'
    $blocked = @(
        '/.git/',
        '/bin/',
        '/obj/',
        '/.vs/',
        '/localdata/',
        '/.kadr-ai/',
        '/release/',
        '/artifacts/',
        '/testresults/',
        '/__pycache__/',
        '/node_modules/',
        '/packages/'
    )

    foreach ($item in $blocked) {
        if ($p.Contains($item)) {
            return $true
        }
    }

    return $false
}

function Is-BinaryExtension([string]$Path) {
    $ext = [IO.Path]::GetExtension($Path).ToLowerInvariant()

    $binaryExtensions = @(
        '.exe', '.dll', '.pdb', '.so', '.dylib',
        '.zip', '.7z', '.rar', '.tar', '.gz', '.bz2', '.xz',
        '.msi', '.msix', '.appx', '.nupkg',
        '.mp4', '.mkv', '.mov', '.avi', '.webm', '.wmv', '.m4v',
        '.mp3', '.wav', '.flac', '.aac', '.ogg', '.opus', '.m4a',
        '.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp', '.tif', '.tiff', '.ico',
        '.gguf', '.onnx', '.pt', '.pth', '.safetensors', '.bin',
        '.db', '.sqlite', '.sqlite3', '.kadr',
        '.iso', '.img'
    )

    return $binaryExtensions -contains $ext
}

function Get-GitOutput([string[]]$Arguments) {
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $result = & git @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed:`n$($result -join [Environment]::NewLine)"
        }
        return @($result)
    }
    finally {
        $ErrorActionPreference = $old
    }
}

# -----------------------------------------------------------------------------
# Validate repository
# -----------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "RepoRoot does not exist: $RepoRoot"
}

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCmd) {
    throw "Git was not found in PATH."
}

$resolvedRepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$topLevel = (Get-GitOutput @('-C', $resolvedRepoRoot, 'rev-parse', '--show-toplevel') | Select-Object -First 1).Trim()

if (-not $topLevel) {
    throw "Could not determine Git root."
}

$topLevel = (Resolve-Path -LiteralPath $topLevel).Path

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputZip = Join-Path $topLevel "KadrStudio-CodeAudit-$stamp.zip"
}
elseif (-not [IO.Path]::IsPathRooted($OutputZip)) {
    $OutputZip = Join-Path $topLevel $OutputZip
}

$outputParent = Split-Path -Parent $OutputZip
if (-not (Test-Path -LiteralPath $outputParent)) {
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("KadrStudio-CodeAudit-" + [Guid]::NewGuid().ToString('N'))
$metaDir = Join-Path $stage '_AUDIT_META'
New-Item -ItemType Directory -Path $metaDir -Force | Out-Null

$copied = New-Object System.Collections.Generic.List[string]
$skipped = New-Object System.Collections.Generic.List[string]

try {
    Write-Step "Repository"
    Write-Host "Git root: $topLevel"
    Write-Host "Output:   $OutputZip"

    # -------------------------------------------------------------------------
    # Ask Git for exactly:
    #   - tracked files (-c)
    #   - untracked, non-ignored files (-o --exclude-standard)
    #
    # This respects:
    #   .gitignore
    #   .git/info/exclude
    #   global Git excludes
    # -------------------------------------------------------------------------
    Write-Step "Reading Git file set"

    $gitFiles = Get-GitOutput @(
        '-c', 'core.quotepath=false',
        '-C', $topLevel,
        'ls-files',
        '-c',
        '-o',
        '--exclude-standard'
    )

    $fileSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($line in $gitFiles) {
        $rel = Normalize-RelPath ([string]$line)
        if (-not [string]::IsNullOrWhiteSpace($rel)) {
            [void]$fileSet.Add($rel)
        }
    }

    Write-Host "Git-visible files: $($fileSet.Count)"

    # -------------------------------------------------------------------------
    # Copy relevant files while preserving repository layout.
    # -------------------------------------------------------------------------
    Write-Step "Copying audit files"

    $maxBytes = [int64]$MaxFileSizeMB * 1MB

    foreach ($rel in ($fileSet | Sort-Object)) {
        $src = Join-Path $topLevel ($rel -replace '/', '\')

        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
            $skipped.Add("$rel`tMISSING")
            continue
        }

        if (Is-RuntimePath $rel) {
            $skipped.Add("$rel`tRUNTIME/CACHE DIRECTORY")
            continue
        }

        $info = Get-Item -LiteralPath $src

        if ($info.Length -gt $maxBytes) {
            $skipped.Add("$rel`tTOO LARGE ($([Math]::Round($info.Length / 1MB, 2)) MiB)")
            continue
        }

        if ((-not $IncludeBinaries) -and (Is-BinaryExtension $rel)) {
            $skipped.Add("$rel`tBINARY/MEDIA/MODEL")
            continue
        }

        $dst = Join-Path $stage ($rel -replace '/', '\')
        $dstDir = Split-Path -Parent $dst

        if (-not (Test-Path -LiteralPath $dstDir)) {
            New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        }

        Copy-Item -LiteralPath $src -Destination $dst -Force
        $copied.Add($rel)
    }

    # -------------------------------------------------------------------------
    # Metadata useful for code audit.
    # -------------------------------------------------------------------------
    Write-Step "Writing Git metadata"

    $branch = (Get-GitOutput @('-C', $topLevel, 'branch', '--show-current') | Select-Object -First 1)
    $commit = (Get-GitOutput @('-C', $topLevel, 'rev-parse', 'HEAD') | Select-Object -First 1)
    $remote = ''
    try {
        $remote = (Get-GitOutput @('-C', $topLevel, 'remote', 'get-url', 'origin') | Select-Object -First 1)
    } catch {
        $remote = 'NO ORIGIN REMOTE'
    }

    @(
        "Created:       $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
        "Repository:    $topLevel"
        "Branch:        $branch"
        "Commit:        $commit"
        "Origin:        $remote"
        "Git file set:  $($fileSet.Count)"
        "Copied files:  $($copied.Count)"
        "Skipped files: $($skipped.Count)"
        "Max file size: $MaxFileSizeMB MiB"
        "Binaries:      $(if ($IncludeBinaries) { 'included when under size limit' } else { 'excluded' })"
        ""
        "Collection rule:"
        "git ls-files -c -o --exclude-standard"
        ""
        "The archive contains WORKING TREE file contents, including modified and"
        "untracked files that are not ignored by Git."
    ) | Set-Content -LiteralPath (Join-Path $metaDir 'MANIFEST.txt') -Encoding UTF8

    Get-GitOutput @(
        '-c', 'core.quotepath=false',
        '-C', $topLevel,
        'status',
        '--short',
        '--branch'
    ) | Set-Content -LiteralPath (Join-Path $metaDir 'GIT_STATUS.txt') -Encoding UTF8

    Get-GitOutput @(
        '-c', 'core.quotepath=false',
        '-C', $topLevel,
        'diff',
        '--stat'
    ) | Set-Content -LiteralPath (Join-Path $metaDir 'GIT_DIFF_STAT.txt') -Encoding UTF8

    $copied | Set-Content -LiteralPath (Join-Path $metaDir 'COPIED_FILES.txt') -Encoding UTF8
    $skipped | Set-Content -LiteralPath (Join-Path $metaDir 'SKIPPED_FILES.txt') -Encoding UTF8

    # Quick inventory by extension.
    $extensionStats = $copied |
        ForEach-Object {
            $ext = [IO.Path]::GetExtension($_).ToLowerInvariant()
            if ([string]::IsNullOrEmpty($ext)) { $ext = '<no extension>' }
            $ext
        } |
        Group-Object |
        Sort-Object Count -Descending |
        ForEach-Object { "{0,6}  {1}" -f $_.Count, $_.Name }

    $extensionStats | Set-Content -LiteralPath (Join-Path $metaDir 'EXTENSION_COUNTS.txt') -Encoding UTF8

    # -------------------------------------------------------------------------
    # Zip
    # -------------------------------------------------------------------------
    Write-Step "Creating ZIP"

    if (Test-Path -LiteralPath $OutputZip) {
        Remove-Item -LiteralPath $OutputZip -Force
    }

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $OutputZip -CompressionLevel Optimal -Force

    $zipInfo = Get-Item -LiteralPath $OutputZip

    Write-Host ""
    Write-Host "DONE" -ForegroundColor Green
    Write-Host "Copied:  $($copied.Count) files"
    Write-Host "Skipped: $($skipped.Count) files"
    Write-Host "ZIP:     $OutputZip"
    Write-Host "Size:    $([Math]::Round($zipInfo.Length / 1MB, 2)) MiB"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
