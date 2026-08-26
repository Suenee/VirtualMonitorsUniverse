@echo off
cls
setlocal EnableExtensions EnableDelayedExpansion

set "UPGRADE_REV=2.0-powershell-runner-bootstrap"
set "REPO_DIR=%~dp0"
if "!REPO_DIR:~-1!"=="\" set "REPO_DIR=!REPO_DIR:~0,-1!"
cd /d "!REPO_DIR!"

if not exist "!REPO_DIR!\logs" mkdir "!REPO_DIR!\logs" >nul 2>nul
set "BOOTSTRAP_LOG=!REPO_DIR!\logs\upgrade.log"

where git.exe >nul 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: Git was not found in PATH.
    powershell.exe -NoProfile -Command "Write-Host 'ERROR: Git was not found in PATH.' -ForegroundColor Red"
    exit /b 1
)

git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: This folder is not a Git working tree.
    powershell.exe -NoProfile -Command "Write-Host 'ERROR: This folder is not a Git working tree.' -ForegroundColor Red"
    exit /b 1
)

git fetch origin >nul 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: git fetch origin failed before PowerShell runner bootstrap.
    >> "!BOOTSTRAP_LOG!" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    powershell.exe -NoProfile -Command "Write-Host 'ERROR: git fetch origin failed before upgrade bootstrap.' -ForegroundColor Red"
    exit /b 1
)

set "RUNNER_TEMP=%TEMP%\VMU-upgrade-%RANDOM%-%RANDOM%.ps1"
git show origin/devel:upgrade.ps1 > "!RUNNER_TEMP!" 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: Could not extract origin/devel:upgrade.ps1.
    >> "!BOOTSTRAP_LOG!" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    powershell.exe -NoProfile -Command "Write-Host 'ERROR: Could not extract upgrade.ps1 from origin/devel.' -ForegroundColor Red"
    exit /b 1
)

rem IMPORTANT: this entire final block is parsed by CMD before PowerShell starts.
rem upgrade.ps1 may update upgrade.cmd on disk while it runs; the already-parsed
rem EXIT command therefore cannot accidentally continue in a newly replaced file.
(
    set "VMU_UPGRADE_REPO=!REPO_DIR!"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "!RUNNER_TEMP!"
    set "UPGRADE_RC=!ERRORLEVEL!"
    del /q "!RUNNER_TEMP!" >nul 2>nul
    exit /b !UPGRADE_RC!
)
