@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "VMU_REPO_ROOT=%CD%"

if not exist "%~dp0.runtime\cli\vmu.dll" (
    echo VMU CLI is not built yet.
    echo Run upgrade.cmd first.
    exit /b 1
)

dotnet "%~dp0.runtime\cli\vmu.dll" %*
exit /b %ERRORLEVEL%
