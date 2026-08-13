@echo off
setlocal
cd /d "%~dp0"

echo Virtual Monitors Universe - ALPHA upgrade and validation
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\setup-alpha-v2.ps1"
set ERR=%ERRORLEVEL%
if not "%ERR%"=="0" goto :fail

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\validate-display-modes.ps1"
set ERR=%ERRORLEVEL%
if not "%ERR%"=="0" goto :fail

echo.
echo ALPHA upgrade and display-mode validation completed successfully.
echo Final expected state: one virtual monitor at 1920x1080 @ 60 Hz.
pause
exit /b 0

:fail
echo.
echo Upgrade failed with exit code %ERR%.
pause
exit /b %ERR%
