@echo off
setlocal
cd /d "%~dp0"
echo Virtual Monitors Universe - ALPHA setup
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\setup-alpha.ps1"
set ERR=%ERRORLEVEL%
echo.
if not "%ERR%"=="0" (
  echo Setup failed with exit code %ERR%.
) else (
  echo Setup completed successfully.
)
pause
exit /b %ERR%
