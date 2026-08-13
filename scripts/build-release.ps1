param(
    [switch]$SkipSdkInstall,
    [switch]$BuildInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'src\Kadr\KadrStudio.csproj'
$releaseRoot = Join-Path $repoRoot 'release'
$publishPath = Join-Path $releaseRoot 'KadrStudio-win-x64'
$zipPath = Join-Path $releaseRoot 'KadrStudio-win-x64.zip'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Не найден проект: $projectPath"
}

$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if (-not $dotnetCommand -and -not $SkipSdkInstall) {
    $wingetCommand = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $wingetCommand) {
        throw 'Не найден .NET 10 SDK и недоступен winget. Установите .NET 10 SDK с сайта dotnet.microsoft.com.'
    }

    Write-Host 'Устанавливается .NET 10 SDK…' -ForegroundColor Cyan
    & winget.exe install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget завершился с кодом $LASTEXITCODE"
    }

    $machineDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $machineDotnet) {
        $dotnetCommand = Get-Item -LiteralPath $machineDotnet
    } else {
        $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    }
}

if (-not $dotnetCommand) {
    throw 'Не найден .NET 10 SDK. Установите его и повторите сборку.'
}

$dotnetExe = $dotnetCommand.Source
Write-Host "Используется SDK: $(& $dotnetExe --version)" -ForegroundColor DarkGray

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

Write-Host 'Проверка проекта…' -ForegroundColor Cyan
& $dotnetExe restore $projectPath --runtime win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet restore завершился с кодом $LASTEXITCODE" }

& $dotnetExe build $projectPath -c Release --runtime win-x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build завершился с кодом $LASTEXITCODE" }

Write-Host 'Создание переносимой Windows-сборки…' -ForegroundColor Cyan
& $dotnetExe publish $projectPath -c Release --runtime win-x64 --self-contained true --no-restore -o $publishPath
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'KadrStudio.exe'))) {
    throw 'Сборка не создала KadrStudio.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'tools\ffmpeg.exe'))) {
    throw 'В сборку не попал tools\ffmpeg.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'tools\ffprobe.exe'))) {
    throw 'В сборку не попал tools\ffprobe.exe.'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'libvlc\win-x64\libvlc.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $publishPath 'libvlc\win-x64\libvlccore.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $publishPath 'libvlc\win-x64\plugins'))) {
    throw 'В сборку не попал полный x64 runtime LibVLC.'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $publishPath -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Готово: $publishPath" -ForegroundColor Green
Write-Host "Архив: $zipPath" -ForegroundColor Green

if ($BuildInstaller) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if (-not $iscc) {
        throw 'Для установщика требуется Inno Setup 6 (ISCC.exe). Переносимая сборка уже готова.'
    }
    & $iscc.Source (Join-Path $repoRoot 'installer\KadrStudio.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с кодом $LASTEXITCODE" }
}
