@echo off
setlocal EnableExtensions
set "VMU_REPO_ROOT=%~dp0"
if "%VMU_REPO_ROOT:~-1%"=="\" set "VMU_REPO_ROOT=%VMU_REPO_ROOT:~0,-1%"
set "VMU_CLI=%~dp0.runtime\cli\vmu.dll"

if not exist "%VMU_CLI%" (
    echo VMU CLI is not built yet.
    echo Run upgrade.cmd first.
    exit /b 1
)

dotnet "%VMU_CLI%" %*
exit /b %ERRORLEVEL%
