@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-release.ps1"
set "KADR_EXIT=%ERRORLEVEL%"
echo.
if not "%KADR_EXIT%"=="0" echo Сборка завершилась с ошибкой %KADR_EXIT%.
if "%KADR_EXIT%"=="0" echo Сборка завершена успешно.
pause
exit /b %KADR_EXIT%
