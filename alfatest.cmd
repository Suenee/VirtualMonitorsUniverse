@echo off
setlocal EnableExtensions
cd /d "%~dp0"
cls

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
echo ALPHA log: %~dp0alfatest.log
echo Multi-VDD log: %~dp0multivddtest.log
pause
exit /b %ERR%
