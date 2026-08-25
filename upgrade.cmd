@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "REPO_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "DEFAULT_BRANCH=devel"
set "DOTNET_REQUIRED_MAJOR=10"
set "DOTNET_REQUIRED_PACKAGE=Microsoft.DotNet.SDK.10"
set "DOTNET_LEGACY_PACKAGE=Microsoft.DotNet.SDK.8"

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

for %%L in (upgrade.log alfatest.log multivddtest.log vmu-selftest.log) do (
    if exist "%REPO_ROOT%\%%L" (
        if exist "%LOG_DIR%\%%L" del /Q "%LOG_DIR%\%%L" >NUL 2>&1
        move /Y "%REPO_ROOT%\%%L" "%LOG_DIR%\%%L" >NUL 2>&1
    )
)

rem Never use cmd.exe's TIME pseudo-variable for log timestamps here. Some
rem execution paths can be parsed as the interactive TIME command. Generate a
rem deterministic timestamp through PowerShell instead.
set "VMU_TIMESTAMP="
for /f "usebackq delims=" %%T in (`powershell.exe -NoProfile -Command "Get-Date -Format 'dd.MM.yyyy HH:mm:ss'" 2^>NUL`) do set "VMU_TIMESTAMP=%%T"
if not defined VMU_TIMESTAMP set "VMU_TIMESTAMP=%DATE%"

> "%LOG_FILE%" echo [%VMU_TIMESTAMP%] Virtual Monitors Universe - DEVEL upgrade
>> "%LOG_FILE%" echo Repository: %REPO_ROOT%
>> "%LOG_FILE%" echo Target branch: %DEFAULT_BRANCH%
>> "%LOG_FILE%" echo Required SDK: .NET %DOTNET_REQUIRED_MAJOR%
>> "%LOG_FILE%" echo.

if not exist "%LOG_FILE%" (
    echo ERROR: Could not create upgrade log: %LOG_FILE%
    pause
    exit /b 1
)

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

call "%TMP_UPDATER%" --worker "%REPO_ROOT%" >> "%LOG_FILE%" 2>&1
set "ERR=%ERRORLEVEL%"
del /Q "%TMP_UPDATER%" >NUL 2>&1

if not exist "%LOG_FILE%" (
    > "%LOG_FILE%" echo ERROR: upgrade.log unexpectedly disappeared during execution.
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

echo [1/7] Downloading DEVEL source...
git fetch origin "%DEFAULT_BRANCH%"
if errorlevel 1 exit /b 1

echo [2/7] Synchronizing local DEVEL branch...
git reset --hard "origin/%DEFAULT_BRANCH%"
if errorlevel 1 exit /b 1

echo [3/7] Running post-update bootstrap...
call "%REPO_ROOT%\upgrade.cmd" --post-update
exit /b %ERRORLEVEL%

:post_update
cd /d "%~dp0"

echo [4/7] Cleaning known obsolete and generated artifacts...
echo Cleaning repository-owned generated files...
if exist ".runtime" rmdir /S /Q ".runtime"
for /D /R "src" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"
for /D /R "tests" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"
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
if not exist "logs" mkdir "logs" >NUL 2>&1

set "HYGIENE_ERROR=0"
for %%D in (tools client companion server shared) do if exist "%%D" set "HYGIENE_ERROR=1"
for %%F in (alfatest.cmd alfatest.log multivddtest.log vmu-selftest.log upgrade.log) do if exist "%%F" set "HYGIENE_ERROR=1"
if "!HYGIENE_ERROR!"=="1" (
    echo ERROR: Workspace hygiene check failed.
    exit /b 1
)
echo Workspace hygiene: OK

echo [5/7] Ensuring .NET %DOTNET_REQUIRED_MAJOR% SDK and retiring .NET 8 SDK...
where winget >NUL 2>&1
if errorlevel 1 (
    echo ERROR: Windows Package Manager ^(winget^) is required to bootstrap the .NET SDK.
    echo Install or repair App Installer, then run upgrade.cmd again.
    exit /b 1
)

set "HAS_DOTNET10=0"
where dotnet >NUL 2>&1
if not errorlevel 1 (
    dotnet --list-sdks 2^>NUL | findstr /R /B "10\." >NUL && set "HAS_DOTNET10=1"
)

if "!HAS_DOTNET10!"=="0" (
    echo .NET 10 SDK is not installed. Starting the official WinGet installation...
    echo A Windows/UAC or installer confirmation may appear. Approve it to continue.
    winget install --id "%DOTNET_REQUIRED_PACKAGE%" --exact --source winget --interactive --accept-source-agreements --accept-package-agreements
    if errorlevel 1 (
        echo ERROR: .NET 10 SDK installation failed or was cancelled.
        echo .NET 8 SDK has NOT been removed.
        exit /b 1
    )
)

where dotnet >NUL 2>&1
if errorlevel 1 (
    echo ERROR: dotnet.exe is still unavailable after .NET 10 installation.
    echo Close this terminal, open a new one, and run upgrade.cmd again.
    exit /b 1
)

dotnet --list-sdks | findstr /R /B "10\." >NUL
if errorlevel 1 (
    echo ERROR: .NET 10 SDK could not be verified after installation.
    echo .NET 8 SDK has NOT been removed.
    exit /b 1
)

echo .NET 10 SDK: VERIFIED
echo Installed SDKs before VMU validation:
dotnet --list-sdks

echo Validating VMU on .NET 10 before removing the old SDK...
dotnet restore "VirtualMonitorsUniverse.sln"
if errorlevel 1 (
    echo ERROR: Restore failed on .NET 10. .NET 8 SDK has NOT been removed.
    exit /b 1
)
dotnet build "VirtualMonitorsUniverse.sln" -c Debug --no-restore
if errorlevel 1 (
    echo ERROR: Build failed on .NET 10. .NET 8 SDK has NOT been removed.
    exit /b 1
)
dotnet test "tests\Core.Tests\Core.Tests.csproj" -c Debug --no-build --no-restore
if errorlevel 1 (
    echo ERROR: Tests failed on .NET 10. .NET 8 SDK has NOT been removed.
    exit /b 1
)
echo VMU validation on .NET 10: PASS

set "HAS_DOTNET8=0"
dotnet --list-sdks 2^>NUL | findstr /R /B "8\." >NUL && set "HAS_DOTNET8=1"
if "!HAS_DOTNET8!"=="1" (
    echo .NET 8 SDK is still installed. Starting its standard Windows uninstall...
    echo A Windows/UAC or uninstaller confirmation may appear. Approve it to continue.
    winget uninstall --id "%DOTNET_LEGACY_PACKAGE%" --exact --source winget --interactive
    if errorlevel 1 (
        echo WARNING: Automatic .NET 8 SDK uninstall did not complete.
        echo No runtime is being removed. You can finish SDK removal from Windows Installed Apps.
        echo Continuing because .NET 10 is installed and VMU validation passed.
    ) else (
        echo .NET 8 SDK uninstall command completed.
    )
)

echo Installed SDKs after SDK maintenance:
dotnet --list-sdks
if exist ".runtime" rmdir /S /Q ".runtime"
for /D /R "src" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"
for /D /R "tests" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"

echo [6/7] Restoring, building, testing and publishing with .NET 10...
dotnet restore "VirtualMonitorsUniverse.sln"
if errorlevel 1 exit /b 1
dotnet build "VirtualMonitorsUniverse.sln" -c Debug --no-restore
if errorlevel 1 exit /b 1
dotnet test "tests\Core.Tests\Core.Tests.csproj" -c Debug --no-build --no-restore
if errorlevel 1 exit /b 1
mkdir ".runtime\cli" >NUL 2>&1
dotnet publish "src\Cli\Cli.csproj" -c Debug --no-restore -o ".runtime\cli"
if errorlevel 1 exit /b 1

echo [7/7] Verifying final workspace and SDK state...
set "HYGIENE_ERROR=0"
for %%D in (tools client companion server shared) do if exist "%%D" set "HYGIENE_ERROR=1"
for %%F in (alfatest.cmd alfatest.log multivddtest.log vmu-selftest.log upgrade.log) do if exist "%%F" set "HYGIENE_ERROR=1"
if "!HYGIENE_ERROR!"=="1" (
    echo ERROR: Repository hygiene check failed after build.
    exit /b 1
)
dotnet --list-sdks | findstr /R /B "10\." >NUL
if errorlevel 1 (
    echo ERROR: Final .NET 10 SDK verification failed.
    exit /b 1
)
echo Final workspace hygiene: OK
echo .NET 10 SDK: OK

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
