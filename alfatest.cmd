@echo off
setlocal EnableExtensions
cd /d "%~dp0"
cls

if not exist "%~dp0logs" mkdir "%~dp0logs"

echo Virtual Monitors Universe - ALPHA acceptance test
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\alfatest.ps1"
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" goto :done

echo.
echo ============================================
echo MULTI-VDD ISOLATION TEST
echo ============================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\multivdd-test.ps1"
set "ERR=%ERRORLEVEL%"

:done
echo.
echo Test finished with exit code %ERR%.
echo ALPHA log: %~dp0logs\alfatest.log
echo Multi-VDD log: %~dp0logs\multivddtest.log
pause
exit /b %ERR%
