@echo off
cls
setlocal EnableExtensions EnableDelayedExpansion

set "UPGRADE_REV=2.1-post-actions"
set "REPO_DIR=%~dp0"
if "!REPO_DIR:~-1!"=="\" set "REPO_DIR=!REPO_DIR:~0,-1!"
cd /d "!REPO_DIR!"

set "DO_TEST=0"
set "DO_RUN=0"
:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--test" (set "DO_TEST=1"&shift&goto parse_args)
if /I "%~1"=="--run" (set "DO_RUN=1"&shift&goto parse_args)
echo ERROR: Unknown upgrade option: %~1
exit /b 2
:args_done

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

rem The runner may update this file while executing, so keep post-actions in this
rem already parsed block. They run only after a completely successful upgrade.
(
    set "VMU_UPGRADE_REPO=!REPO_DIR!"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "!RUNNER_TEMP!"
    set "UPGRADE_RC=!ERRORLEVEL!"
    del /q "!RUNNER_TEMP!" >nul 2>nul
    if not "!UPGRADE_RC!"=="0" exit /b !UPGRADE_RC!

    if "!DO_TEST!"=="1" (
        echo.
        echo ============================================
        echo Running VMU CLI selftest
        echo ============================================
        call "!REPO_DIR!\vmu.cmd" selftest
        set "TEST_RC=!ERRORLEVEL!"
        if not "!TEST_RC!"=="0" (
            powershell.exe -NoProfile -Command "Write-Host 'CLI selftest failed. --run will not be executed.' -ForegroundColor Red"
            exit /b !TEST_RC!
        )
    )

    if "!DO_RUN!"=="1" (
        tasklist /FI "IMAGENAME eq VirtualMonitorsUniverse.Server.exe" 2>nul | find /I "VirtualMonitorsUniverse.Server.exe" >nul
        if errorlevel 1 (
            echo Starting VMU Server...
            start "" "!REPO_DIR!\vmu-server.cmd"
        ) else (
            echo VMU Server is already running; --run skipped.
        )
    )
    exit /b 0
)
