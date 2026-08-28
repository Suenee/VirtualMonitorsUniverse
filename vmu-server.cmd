@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "VMU_SERVER=%~dp0.runtime\server\VirtualMonitorsUniverse.Server.exe"

if not exist "%VMU_SERVER%" (
    echo VMU Server is not built yet.
    echo Run upgrade.cmd first.
    exit /b 1
)

start "" "%VMU_SERVER%"
exit /b 0
