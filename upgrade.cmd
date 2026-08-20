@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "REPO_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "DEFAULT_BRANCH=feature/alpha-driver-poc"

if /I "%~1"=="--worker" goto :worker
if /I "%~1"=="--post-update" goto :post_update

cd /d "%~dp0"

echo ============================================
echo Virtual Monitors Universe - GitHub upgrade
echo ============================================
echo.

rem Run Git synchronization from a temporary copy so upgrade.cmd itself can
rem be safely replaced while the upgrade is running.
set "TMP_UPDATER=%TEMP%\VMU-upgrade-%RANDOM%-%RANDOM%.cmd"
copy /Y "%~f0" "%TMP_UPDATER%" >NUL
if errorlevel 1 (
    echo ERROR: Could not create temporary updater.
    goto :fail
)

call "%TMP_UPDATER%" --worker "%~dp0"
set "ERR=%ERRORLEVEL%"
del /Q "%TMP_UPDATER%" >NUL 2>&1
exit /b %ERR%

:worker
set "REPO_ROOT=%~2"
if not defined REPO_ROOT exit /b 1
cd /d "%REPO_ROOT%"

where git >NUL 2>&1
if errorlevel 1 (
    echo ERROR: Git for Windows is not installed or git.exe is not in PATH.
    exit /b 1
)

set "BRANCH=%DEFAULT_BRANCH%"
if exist ".git" (
    for /f "usebackq delims=" %%B in (`git rev-parse --abbrev-ref HEAD 2^>NUL`) do set "CURRENT_BRANCH=%%B"
    if defined CURRENT_BRANCH if /I not "!CURRENT_BRANCH!"=="HEAD" set "BRANCH=!CURRENT_BRANCH!"
)

echo Target branch: !BRANCH!
echo.

if not exist ".git" (
    echo [1/4] Converting directory to a Git working copy...
    git init
    if errorlevel 1 exit /b 1
    git remote add origin "%REPO_URL%" >NUL 2>&1
    git remote set-url origin "%REPO_URL%"
    git fetch origin "!BRANCH!"
    if errorlevel 1 exit /b 1
    git checkout -B "!BRANCH!" "origin/!BRANCH!"
    if errorlevel 1 exit /b 1
) else (
    echo [1/4] Checking local tracked files...
    git diff --quiet
    if errorlevel 1 (
        echo ERROR: Local tracked files contain changes.
        echo Commit or revert them before running upgrade.cmd.
        exit /b 1
    )
    git diff --cached --quiet
    if errorlevel 1 (
        echo ERROR: Local staged changes exist.
        echo Commit or revert them before running upgrade.cmd.
        exit /b 1
    )

    git remote set-url origin "%REPO_URL%" >NUL 2>&1
    echo [2/4] Downloading current source from GitHub...
    git fetch origin "!BRANCH!"
    if errorlevel 1 exit /b 1

    echo [3/4] Updating local source tree...
    git checkout "!BRANCH!" >NUL 2>&1
    if errorlevel 1 git checkout -B "!BRANCH!" "origin/!BRANCH!"
    if errorlevel 1 exit /b 1
    git reset --hard "origin/!BRANCH!"
    if errorlevel 1 exit /b 1
)

echo [4/4] Restarting with the newly downloaded upgrade.cmd...
call "%REPO_ROOT%upgrade.cmd" --post-update
exit /b %ERRORLEVEL%

:post_update
cd /d "%~dp0"

echo.
echo Applying repository-local upgrade steps...
if not exist ".runtime\alpha" mkdir ".runtime\alpha" >NUL 2>&1

rem Clean only legacy paths created by earlier VMU ALPHA builds. The PowerShell
rem script refuses to delete a directory unless known VDD marker files exist.
if exist "tools\alpha\cleanup-legacy-c.ps1" (
    echo Checking C: for legacy VMU/VDD development artifacts...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\cleanup-legacy-c.ps1"
    set "CLEANERR=!ERRORLEVEL!"
    if not "!CLEANERR!"=="0" (
        echo WARNING: Legacy C: cleanup did not fully complete. Nothing unknown was deleted.
    )
)

echo.
echo ============================================
echo UPGRADE COMPLETED SUCCESSFULLY
echo ============================================
echo Repository is synchronized with GitHub.
echo Development/runtime files are stored under: %~dp0.runtime\alpha
echo Next ALPHA acceptance test: alfatest.cmd
echo.
pause
exit /b 0

:fail
echo.
echo ============================================
echo UPGRADE FAILED
echo ============================================
pause
exit /b 1
