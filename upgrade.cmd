@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "REPO_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "DEFAULT_BRANCH=devel"

if /I "%~1"=="--worker" goto :worker
if /I "%~1"=="--post-update" goto :post_update

cls
cd /d "%~dp0"
set "REPO_ROOT=%CD%"
set "LOG_DIR=%REPO_ROOT%\logs"
set "LOG_FILE=%LOG_DIR%\upgrade.log"
set "TMP_UPDATER=%TEMP%\VMU-upgrade-%RANDOM%-%RANDOM%.cmd"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%" >NUL 2>&1
if not exist "%LOG_DIR%" (
    echo ERROR: Could not create log directory: %LOG_DIR%
    pause
    exit /b 1
)

rem Migrate any legacy root-level logs before opening the current upgrade log.
for %%L in (upgrade.log alfatest.log multivddtest.log vmu-selftest.log) do (
    if exist "%REPO_ROOT%\%%L" (
        if exist "%LOG_DIR%\%%L" del /Q "%LOG_DIR%\%%L" >NUL 2>&1
        move /Y "%REPO_ROOT%\%%L" "%LOG_DIR%\%%L" >NUL 2>&1
    )
)

rem Create the current log before any Git, cleanup, dependency, or build step.
rem This file is repository-local and ignored by Git.
type NUL > "%LOG_FILE%"
if not exist "%LOG_FILE%" (
    echo ERROR: Could not create upgrade log: %LOG_FILE%
    pause
    exit /b 1
)

>> "%LOG_FILE%" echo [%DATE% %TIME%] Virtual Monitors Universe - DEVEL upgrade
>> "%LOG_FILE%" echo Repository: %REPO_ROOT%
>> "%LOG_FILE%" echo Target branch: %DEFAULT_BRANCH%
>> "%LOG_FILE%" echo.

echo ============================================
echo Virtual Monitors Universe - DEVEL upgrade
echo ============================================
echo.

copy /Y "%~f0" "%TMP_UPDATER%" >NUL
if errorlevel 1 (
    echo ERROR: Could not create temporary updater.
    >> "%LOG_FILE%" echo ERROR: Could not create temporary updater.
    goto :fail
)

rem Capture the complete worker/post-update output in the single central log.
call "%TMP_UPDATER%" --worker "%REPO_ROOT%" >> "%LOG_FILE%" 2>&1
set "ERR=%ERRORLEVEL%"
del /Q "%TMP_UPDATER%" >NUL 2>&1

rem The log must survive every success/failure path.
if not exist "%LOG_FILE%" (
    type NUL > "%LOG_FILE%"
    >> "%LOG_FILE%" echo [%DATE% %TIME%] ERROR: upgrade.log unexpectedly disappeared during execution.
    >> "%LOG_FILE%" echo Worker exit code: %ERR%
)

type "%LOG_FILE%"
echo.
echo Upgrade log: %LOG_FILE%
if not "%ERR%"=="0" goto :fail_with_code
pause
exit /b 0

:worker
set "REPO_ROOT=%~2"
if not defined REPO_ROOT exit /b 1
cd /d "%REPO_ROOT%"

where git >NUL 2>&1
if errorlevel 1 (
    echo ERROR: Git for Windows is not installed or git.exe is not in PATH.
    exit /b 1
)

if not exist ".git" (
    echo ERROR: This DEVEL bootstrap expects an existing Git working copy.
    exit /b 1
)

for /f "usebackq delims=" %%B in (`git rev-parse --abbrev-ref HEAD 2^>NUL`) do set "CURRENT_BRANCH=%%B"
if /I not "!CURRENT_BRANCH!"=="%DEFAULT_BRANCH%" (
    echo ERROR: Current branch is !CURRENT_BRANCH!, expected %DEFAULT_BRANCH%.
    echo Switch once with: git fetch origin ^&^& git switch devel
    exit /b 1
)

git diff --quiet
if errorlevel 1 (
    echo ERROR: Local tracked files contain changes. Commit or revert them first.
    exit /b 1
)
git diff --cached --quiet
if errorlevel 1 (
    echo ERROR: Local staged changes exist. Commit or revert them first.
    exit /b 1
)

git remote set-url origin "%REPO_URL%" >NUL 2>&1

echo [1/6] Downloading DEVEL source...
git fetch origin "%DEFAULT_BRANCH%"
if errorlevel 1 exit /b 1

echo [2/6] Synchronizing local DEVEL branch...
git reset --hard "origin/%DEFAULT_BRANCH%"
if errorlevel 1 exit /b 1

echo [3/6] Cleaning known obsolete and generated artifacts...
call "%REPO_ROOT%\upgrade.cmd" --post-update
exit /b %ERRORLEVEL%

:post_update
cd /d "%~dp0"

rem ---------------------------------------------------------------------------
rem Workspace hygiene
rem ---------------------------------------------------------------------------
rem Never use a blanket "git clean" here. Only paths that VMU owns and can
rem safely regenerate, or paths that are explicitly obsolete from the ALPHA
rem prototype, are removed. The logs directory is never removed by cleanup.

echo Cleaning repository-owned generated files...

rem Runtime/build outputs are always reproducible.
if exist ".runtime" rmdir /S /Q ".runtime"
for /D /R "src" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"
for /D /R "tests" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"

rem Known ALPHA/legacy paths are intentionally absent from DEVEL.
for %%D in (tools client companion server shared) do (
    if exist "%%D" (
        echo Removing obsolete path: %%D
        rmdir /S /Q "%%D"
    )
)
for %%F in (alfatest.cmd alfatest.log multivddtest.log vmu-selftest.log upgrade.log) do (
    if exist "%%F" (
        echo Removing obsolete root file: %%F
        del /Q "%%F"
    )
)

rem Recreate only the directories owned by the current DEVEL toolchain.
if not exist "logs" mkdir "logs" >NUL 2>&1
if not exist ".runtime\cli" mkdir ".runtime\cli" >NUL 2>&1

rem Sanity check: no known ALPHA paths may survive cleanup.
set "HYGIENE_ERROR=0"
for %%D in (tools client companion server shared) do if exist "%%D" (
    echo ERROR: Obsolete path still exists after cleanup: %%D
    set "HYGIENE_ERROR=1"
)
for %%F in (alfatest.cmd alfatest.log multivddtest.log vmu-selftest.log upgrade.log) do if exist "%%F" (
    echo ERROR: Obsolete root file still exists after cleanup: %%F
    set "HYGIENE_ERROR=1"
)
if "!HYGIENE_ERROR!"=="1" exit /b 1

echo Workspace hygiene: OK

where dotnet >NUL 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK is not installed or dotnet.exe is not in PATH.
    exit /b 1
)

dotnet --list-sdks | findstr /R /B "10\." >NUL
if errorlevel 1 (
    echo ERROR: .NET 10 SDK is required.
    echo Installed SDKs:
    dotnet --list-sdks
    exit /b 1
)

echo .NET 10 SDK: OK

echo [4/6] Restoring, building and testing...
dotnet restore "VirtualMonitorsUniverse.sln"
if errorlevel 1 exit /b 1

dotnet build "VirtualMonitorsUniverse.sln" -c Debug --no-restore
if errorlevel 1 exit /b 1

dotnet test "tests\Core.Tests\Core.Tests.csproj" -c Debug --no-build --no-restore
if errorlevel 1 exit /b 1

echo [5/6] Publishing VMU CLI...
if exist ".runtime\cli" rmdir /S /Q ".runtime\cli"
mkdir ".runtime\cli" >NUL 2>&1
dotnet publish "src\Cli\Cli.csproj" -c Debug --no-restore -o ".runtime\cli"
if errorlevel 1 exit /b 1

echo [6/6] Verifying final workspace hygiene...
set "HYGIENE_ERROR=0"
for %%D in (tools client companion server shared) do if exist "%%D" set "HYGIENE_ERROR=1"
for %%F in (alfatest.cmd alfatest.log multivddtest.log vmu-selftest.log upgrade.log) do if exist "%%F" set "HYGIENE_ERROR=1"
if "!HYGIENE_ERROR!"=="1" (
    echo ERROR: Repository hygiene check failed after build.
    exit /b 1
)
echo Final workspace hygiene: OK

echo.
echo ============================================
echo UPGRADE COMPLETED SUCCESSFULLY
echo ============================================
echo Branch: devel
echo Runtime CLI: %~dp0.runtime\cli
echo Logs: %~dp0logs
echo Next check: vmu selftest
echo.
exit /b 0

:fail_with_code
echo.
echo ============================================
echo UPGRADE FAILED
echo ============================================
echo See logs\upgrade.log for details.
pause
exit /b %ERR%

:fail
echo.
echo ============================================
echo UPGRADE FAILED
echo ============================================
echo See logs\upgrade.log for details.
pause
exit /b 1
