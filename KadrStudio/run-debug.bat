@echo off
setlocal
cd /d "%~dp0"
where dotnet.exe >nul 2>nul
if errorlevel 1 (
  echo Не найден .NET SDK. Сначала запустите build-release.bat.
  pause
  exit /b 1
)
dotnet run --project "%~dp0src\Kadr\KadrStudio.csproj" -c Debug
pause

