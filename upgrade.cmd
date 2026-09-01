@echo off
cls
setlocal EnableExtensions EnableDelayedExpansion

set "UPGRADE_REV=2.4-network-safe"
set "REPOSITORY_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "REPOSITORY_BRANCH=devel"
set "ORIGINAL_ARGS=%*"
set "REPO_DIR=%~dp0"
if "!REPO_DIR:~-1!"=="\" set "REPO_DIR=!REPO_DIR:~0,-1!"

rem Use pushd instead of cd /d. Besides mapped drives (for example N:), pushd
rem also supports UNC paths by assigning a temporary drive letter for cmd.exe.
pushd "!REPO_DIR!" >nul 2>nul
if errorlevel 1 (
    echo ERROR: Repository path is not accessible:
    echo   !REPO_DIR!
    exit /b 1
)

rem Keep .NET/NuGet caches on the local computer even when the source tree is on
rem a NAS or mapped network drive. NUGET_PACKAGES is an officially supported
rem override for the global packages folder and avoids unnecessary network I/O.
if defined LOCALAPPDATA (
    set "VMU_LOCAL_STATE=%LOCALAPPDATA%\VirtualMonitorsUniverse"
) else (
    set "VMU_LOCAL_STATE=%TEMP%\VirtualMonitorsUniverse"
)
set "DOTNET_CLI_HOME=!VMU_LOCAL_STATE!\dotnet-home"
set "NUGET_PACKAGES=!VMU_LOCAL_STATE!\nuget\packages"
set "NUGET_HTTP_CACHE_PATH=!VMU_LOCAL_STATE!\nuget\http-cache"
set "NUGET_SCRATCH=!VMU_LOCAL_STATE!\nuget\scratch"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
for %%D in ("!DOTNET_CLI_HOME!" "!NUGET_PACKAGES!" "!NUGET_HTTP_CACHE_PATH!" "!NUGET_SCRATCH!") do if not exist "%%~D" mkdir "%%~D" >nul 2>nul

rem Trust only this explicitly selected working tree for this process. This
rem prevents Git's dubious-ownership protection from breaking a repository that
rem is intentionally hosted on a network share, without modifying global config.
set "GIT_CONFIG_COUNT=1"
set "GIT_CONFIG_KEY_0=safe.directory"
set "GIT_CONFIG_VALUE_0=!REPO_DIR!"

set "DO_TEST=0"
set "DO_RUN=0"
:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--test" (set "DO_TEST=1"&shift&goto parse_args)
if /I "%~1"=="--run" (set "DO_RUN=1"&shift&goto parse_args)
echo ERROR: Unknown upgrade option: %~1
popd >nul 2>nul
exit /b 2
:args_done

set "TEST_LABEL=no"
set "RUN_LABEL=no"
if "!DO_TEST!"=="1" set "TEST_LABEL=yes"
if "!DO_RUN!"=="1" set "RUN_LABEL=yes"
echo Requested post actions: test=!TEST_LABEL!, run=!RUN_LABEL!
echo Local build cache: !VMU_LOCAL_STATE!

if not exist "!REPO_DIR!\logs" mkdir "!REPO_DIR!\logs" >nul 2>nul
set "BOOTSTRAP_LOG=!REPO_DIR!\logs\upgrade.log"

rem ---------------------------------------------------------------------------
rem Git bootstrap
rem ---------------------------------------------------------------------------
rem A copied upgrade.cmd can bootstrap a new Windows computer. Prefer the
rem standard Windows package manager instead of downloading an installer from an
rem unversioned URL. After installation, refresh the common Git PATH locations
rem because the current cmd.exe process does not inherit environment changes.
where git.exe >nul 2>nul
if errorlevel 1 (
    echo Git was not found. Installing Git for Windows...
    >> "!BOOTSTRAP_LOG!" echo INFO: Git was not found in PATH. Starting winget bootstrap.

    where winget.exe >nul 2>nul
    if errorlevel 1 (
        >> "!BOOTSTRAP_LOG!" echo ERROR: Neither Git nor winget was found.
        powershell.exe -NoProfile -Command "Write-Host 'ERROR: Git is missing and Windows Package Manager (winget) is unavailable.' -ForegroundColor Red"
        powershell.exe -NoProfile -Command "Write-Host 'Install Microsoft App Installer / winget, then run upgrade.cmd again.' -ForegroundColor Yellow"
        popd >nul 2>nul
        exit /b 1
    )

    winget install --id Git.Git --exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements
    set "GIT_INSTALL_RC=!ERRORLEVEL!"
    if not "!GIT_INSTALL_RC!"=="0" (
        >> "!BOOTSTRAP_LOG!" echo ERROR: winget failed to install Git. Exit code !GIT_INSTALL_RC!.
        powershell.exe -NoProfile -Command "Write-Host 'ERROR: winget could not install Git for Windows.' -ForegroundColor Red"
        popd >nul 2>nul
        exit /b !GIT_INSTALL_RC!
    )

    if exist "%ProgramFiles%\Git\cmd\git.exe" set "PATH=%ProgramFiles%\Git\cmd;!PATH!"
    if exist "%ProgramFiles(x86)%\Git\cmd\git.exe" set "PATH=%ProgramFiles(x86)%\Git\cmd;!PATH!"
    if exist "%LocalAppData%\Programs\Git\cmd\git.exe" set "PATH=%LocalAppData%\Programs\Git\cmd;!PATH!"

    where git.exe >nul 2>nul
    if errorlevel 1 (
        >> "!BOOTSTRAP_LOG!" echo ERROR: Git installation completed but git.exe is still unavailable.
        powershell.exe -NoProfile -Command "Write-Host 'ERROR: Git was installed but git.exe is not available to this process.' -ForegroundColor Red"
        powershell.exe -NoProfile -Command "Write-Host 'Open a new Command Prompt and run upgrade.cmd again.' -ForegroundColor Yellow"
        popd >nul 2>nul
        exit /b 1
    )

    for /f "delims=" %%G in ('git --version 2^>nul') do set "GIT_VERSION=%%G"
    echo Git installed successfully: !GIT_VERSION!
    >> "!BOOTSTRAP_LOG!" echo INFO: Git bootstrap completed: !GIT_VERSION!.
)

rem ---------------------------------------------------------------------------
rem Repository bootstrap
rem ---------------------------------------------------------------------------
git rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 (
    rem This mode is intended for a standalone copy of upgrade.cmd. Put the file
    rem in the parent directory where the VMU folder should be created.
    set "CLONE_DIR=!REPO_DIR!\VirtualMonitorsUniverse"
    echo.
    echo No VMU Git working tree was found.
    echo Cloning !REPOSITORY_BRANCH! into:
    echo   !CLONE_DIR!
    >> "!BOOTSTRAP_LOG!" echo INFO: No working tree found. Cloning !REPOSITORY_URL! branch !REPOSITORY_BRANCH! to !CLONE_DIR!.

    if exist "!CLONE_DIR!\.git" (
        powershell.exe -NoProfile -Command "Write-Host 'ERROR: Target folder already contains a Git repository.' -ForegroundColor Red"
        popd >nul 2>nul
        exit /b 1
    )
    if exist "!CLONE_DIR!" (
        dir /b "!CLONE_DIR!" 2>nul | findstr . >nul
        if not errorlevel 1 (
            powershell.exe -NoProfile -Command "Write-Host 'ERROR: Target folder already exists and is not empty:' -ForegroundColor Red"
            echo !CLONE_DIR!
            popd >nul 2>nul
            exit /b 1
        )
    )

    git clone --branch "!REPOSITORY_BRANCH!" --single-branch "!REPOSITORY_URL!" "!CLONE_DIR!"
    if errorlevel 1 (
        >> "!BOOTSTRAP_LOG!" echo ERROR: Repository clone failed.
        powershell.exe -NoProfile -Command "Write-Host 'ERROR: VMU repository clone failed.' -ForegroundColor Red"
        popd >nul 2>nul
        exit /b 1
    )

    echo.
    echo Repository cloned successfully.
    echo Continuing with the repository upgrade script...
    call "!CLONE_DIR!\upgrade.cmd" !ORIGINAL_ARGS!
    set "CHILD_RC=!ERRORLEVEL!"
    popd >nul 2>nul
    exit /b !CHILD_RC!
)

rem ---------------------------------------------------------------------------
rem Normal in-repository upgrade
rem ---------------------------------------------------------------------------
git fetch origin >nul 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: git fetch origin failed before PowerShell runner bootstrap.
    >> "!BOOTSTRAP_LOG!" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    powershell.exe -NoProfile -Command "Write-Host 'ERROR: git fetch origin failed before upgrade bootstrap.' -ForegroundColor Red"
    popd >nul 2>nul
    exit /b 1
)

set "RUNNER_TEMP=%TEMP%\VMU-upgrade-%RANDOM%-%RANDOM%.ps1"
git show origin/devel:upgrade.ps1 > "!RUNNER_TEMP!" 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: Could not extract origin/devel:upgrade.ps1.
    >> "!BOOTSTRAP_LOG!" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    powershell.exe -NoProfile -Command "Write-Host 'ERROR: Could not extract upgrade.ps1 from origin/devel.' -ForegroundColor Red"
    popd >nul 2>nul
    exit /b 1
)

rem The runner may update this file while executing, so keep post-actions in this
rem already parsed block. They run only after a completely successful upgrade.
(
    set "VMU_UPGRADE_REPO=!REPO_DIR!"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "!RUNNER_TEMP!"
    set "UPGRADE_RC=!ERRORLEVEL!"
    del /q "!RUNNER_TEMP!" >nul 2>nul
    if not "!UPGRADE_RC!"=="0" (
        popd >nul 2>nul
        exit /b !UPGRADE_RC!
    )

    if "!DO_TEST!"=="1" (
        echo.
        echo ============================================
        echo Running VMU CLI selftest
        echo ============================================
        call "!REPO_DIR!\vmu.cmd" selftest
        set "TEST_RC=!ERRORLEVEL!"
        if not "!TEST_RC!"=="0" (
            powershell.exe -NoProfile -Command "Write-Host 'CLI selftest failed. --run will not be executed.' -ForegroundColor Red"
            popd >nul 2>nul
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

    echo.
    echo ============================================
    echo Upgrade post actions complete
    echo ============================================
    echo Post actions: test=!TEST_LABEL!, run=!RUN_LABEL!
    popd >nul 2>nul
    exit /b 0
)
