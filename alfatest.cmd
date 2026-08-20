@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo Virtual Monitors Universe - ALPHA acceptance test
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\run-alfatest-ccd.ps1"
set "ERR=%ERRORLEVEL%"

echo.
echo Test finished with exit code %ERR%.
echo Log: %~dp0alfatest.log
pause
exit /b %ERR%
