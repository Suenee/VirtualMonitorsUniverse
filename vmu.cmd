@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if "%~1"=="" goto :usage

if /I "%~1"=="selftest" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\vmu\selftest.ps1"
    exit /b %ERRORLEVEL%
)

if /I "%~1"=="help" goto :usage
if /I "%~1"=="--help" goto :usage
if /I "%~1"=="-h" goto :usage

echo Unknown VMU command: %~1
echo.
:usage
echo Virtual Monitors Universe development CLI
echo.
echo Usage:
echo   vmu selftest    Run the VMU Core regression/acceptance self-test
echo   vmu help        Show this help
exit /b 2
