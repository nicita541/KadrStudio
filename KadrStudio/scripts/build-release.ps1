param(
    [switch]$SkipSdkInstall,
    [switch]$BuildInstaller
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repoRoot 'KadrStudio.sln'
$projectPath = Join-Path $repoRoot 'src\Kadr\KadrStudio.csproj'
$releaseRoot = Join-Path $repoRoot 'release'
$publishPath = Join-Path $releaseRoot 'KadrStudio-win-x64'
$zipPath = Join-Path $releaseRoot 'KadrStudio-win-x64.zip'

function Assert-PathWithinRepository([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Путь сборки вышел за пределы репозитория: $fullPath"
    }
}

Assert-PathWithinRepository $releaseRoot
Assert-PathWithinRepository $publishPath
Assert-PathWithinRepository $zipPath

if (-not (Test-Path -LiteralPath $solutionPath) -or -not (Test-Path -LiteralPath $projectPath)) {
    throw "Не найдены solution или WPF-проект в $repoRoot"
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
    $publishPrefix = [System.IO.Path]::GetFullPath($publishPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    foreach ($item in Get-ChildItem -LiteralPath $publishPath -Force) {
        $itemPath = [System.IO.Path]::GetFullPath($item.FullName)
        if (-not $itemPath.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Элемент публикации вышел за пределы каталога сборки: $itemPath"
        }
        Remove-Item -LiteralPath $itemPath -Recurse -Force
    }
}
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

Write-Host 'Восстановление, сборка и тестирование всего решения…' -ForegroundColor Cyan
& $dotnetExe restore $solutionPath --disable-parallel --disable-build-servers -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw "dotnet restore solution завершился с кодом $LASTEXITCODE" }

& $dotnetExe build $solutionPath -c Release --no-restore -m:1 -nr:false -warnaserror
if ($LASTEXITCODE -ne 0) { throw "dotnet build solution завершился с кодом $LASTEXITCODE" }

& $dotnetExe test $solutionPath -c Release --no-build --no-restore -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw "dotnet test завершился с кодом $LASTEXITCODE" }

& $dotnetExe restore $projectPath --runtime win-x64 --disable-parallel --disable-build-servers -m:1 -nr:false
if ($LASTEXITCODE -ne 0) { throw "dotnet restore win-x64 завершился с кодом $LASTEXITCODE" }

Write-Host 'Создание переносимой Windows-сборки…' -ForegroundColor Cyan
& $dotnetExe publish $projectPath -c Release --runtime win-x64 --self-contained true --no-restore --disable-build-servers -m:1 -warnaserror -o $publishPath
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
if (-not (Test-Path -LiteralPath (Join-Path $publishPath 'mediahost\Kadr.MediaHost.exe'))) {
    throw 'В сборку не попал Kadr.MediaHost.exe.'
}


Write-Host 'Проверка запуска готового приложения…' -ForegroundColor Cyan
$publishedExe = Join-Path $publishPath 'KadrStudio.exe'
$smokeProcess = Start-Process -FilePath $publishedExe -ArgumentList '--launch-smoke' -PassThru -WindowStyle Hidden
if (-not $smokeProcess.WaitForExit(30000)) {
    Stop-Process -Id $smokeProcess.Id -Force -ErrorAction SilentlyContinue
    throw 'Готовое приложение не завершило launch smoke за 30 секунд.'
}
if ($smokeProcess.ExitCode -ne 0) {
    throw "Launch smoke готового приложения завершился с кодом $($smokeProcess.ExitCode)."
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
