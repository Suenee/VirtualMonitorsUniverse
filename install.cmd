@echo off
cls
setlocal EnableExtensions EnableDelayedExpansion

rem ---------------------------------------------------------------------------
rem Virtual Monitors Universe - fresh installer
rem
rem This file is intentionally self-contained so it can be copied to a new
rem Windows computer and executed from any local or network location.
rem ---------------------------------------------------------------------------
set "INSTALL_REV=1.1-network-safe"
set "REPOSITORY_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "REPOSITORY_BRANCH=devel"
set "TARGET_DIR=N:\WORK\GitHub\VirtualMonitorsUniverse"

echo ============================================
echo Virtual Monitors Universe - FRESH INSTALL
echo ============================================
echo Target: %TARGET_DIR%
echo Branch: %REPOSITORY_BRANCH%
echo Installer: %INSTALL_REV%
echo.

rem Keep build caches and temporary .NET state on the local computer. This is
rem important when the repository itself lives on a mapped network drive.
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

rem ---------------------------------------------------------------------------
rem Git bootstrap
rem ---------------------------------------------------------------------------
where git.exe >nul 2>nul
if errorlevel 1 (
    echo [1/5] Git for Windows was not found. Installing it...
    where winget.exe >nul 2>nul
    if errorlevel 1 (
        echo ERROR: Neither Git nor Windows Package Manager ^(winget^) is available.
        echo Install Microsoft App Installer / winget and run install.cmd again.
        goto failed
    )

    winget install --id Git.Git --exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements
    if errorlevel 1 (
        echo ERROR: Git for Windows installation failed.
        goto failed
    )

    if exist "%ProgramFiles%\Git\cmd\git.exe" set "PATH=%ProgramFiles%\Git\cmd;!PATH!"
    if exist "%ProgramFiles(x86)%\Git\cmd\git.exe" set "PATH=%ProgramFiles(x86)%\Git\cmd;!PATH!"
    if exist "%LocalAppData%\Programs\Git\cmd\git.exe" set "PATH=%LocalAppData%\Programs\Git\cmd;!PATH!"

    where git.exe >nul 2>nul
    if errorlevel 1 (
        echo ERROR: Git was installed but git.exe is still unavailable to this process.
        echo Open a new Command Prompt and run install.cmd again.
        goto failed
    )
) else (
    echo [1/5] Git for Windows: OK
)

for /f "delims=" %%G in ('git --version 2^>nul') do echo       %%G

rem ---------------------------------------------------------------------------
rem Target validation
rem ---------------------------------------------------------------------------
echo [2/5] Checking target directory...
for %%P in ("%TARGET_DIR%\..") do set "TARGET_PARENT=%%~fP"
if not exist "!TARGET_PARENT!" mkdir "!TARGET_PARENT!" >nul 2>nul
if not exist "!TARGET_PARENT!" (
    echo ERROR: Cannot create or access target parent directory:
    echo   !TARGET_PARENT!
    echo Check that network drive N: is connected and writable.
    goto failed
)

if exist "%TARGET_DIR%\.git" (
    echo Existing VMU Git working copy found.
    echo Fresh install will reuse it and run the current upgrade pipeline.
    goto run_upgrade
)

if exist "%TARGET_DIR%" (
    dir /b "%TARGET_DIR%" 2>nul | findstr . >nul
    if not errorlevel 1 (
        echo ERROR: Target directory exists, is not empty, and is not a VMU Git repository:
        echo   %TARGET_DIR%
        echo No files were deleted.
        goto failed
    )
)

rem ---------------------------------------------------------------------------
rem Clone
rem ---------------------------------------------------------------------------
echo [3/5] Cloning VMU DEVEL from GitHub...
git clone --branch "%REPOSITORY_BRANCH%" --single-branch "%REPOSITORY_URL%" "%TARGET_DIR%"
if errorlevel 1 (
    echo ERROR: Repository clone failed.
    goto failed
)

if not exist "%TARGET_DIR%\upgrade.cmd" (
    echo ERROR: Clone completed, but upgrade.cmd is missing.
    goto failed
)

:run_upgrade
rem Process-local safe.directory avoids changing the user's global Git config,
rem while allowing a trusted working tree hosted by a NAS/network share.
set "GIT_CONFIG_COUNT=1"
set "GIT_CONFIG_KEY_0=safe.directory"
set "GIT_CONFIG_VALUE_0=%TARGET_DIR%"

echo [4/5] Running dependency, build, unit-test and publish pipeline...
rem Do not run "vmu selftest" here. VMU selftest is an invasive final ALPHA
rem hardware acceptance test: it requires a clean VDD baseline and installs and
rem removes real display-class device nodes. Installation success is determined
rem by upgrade.ps1, which already performs restore, build, unit tests and publish.
call "%TARGET_DIR%\upgrade.cmd"
set "INSTALL_RC=!ERRORLEVEL!"
if not "!INSTALL_RC!"=="0" (
    echo ERROR: VMU upgrade/build validation failed.
    goto failed
)

echo [5/5] Verifying installed launchers...
if not exist "%TARGET_DIR%\vmu.cmd" (
    echo ERROR: vmu.cmd was not found after installation.
    goto failed
)
if not exist "%TARGET_DIR%\vmu-server.cmd" (
    echo ERROR: vmu-server.cmd was not found after installation.
    goto failed
)

echo.
echo ============================================
echo INSTALL COMPLETED SUCCESSFULLY
echo ============================================
echo Location: %TARGET_DIR%
echo Branch: %REPOSITORY_BRANCH%
echo Local build cache: !VMU_LOCAL_STATE!
echo.
echo Start CLI:    %TARGET_DIR%\vmu.cmd
echo Start server: %TARGET_DIR%\vmu-server.cmd
echo.
echo Optional hardware acceptance test:
echo   %TARGET_DIR%\vmu.cmd selftest
exit /b 0

:failed
echo.
echo ============================================
echo INSTALL FAILED
echo ============================================
echo Target: %TARGET_DIR%
echo No existing non-VMU files were intentionally removed.
pause
exit /b 1
