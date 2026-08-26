@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "REPO_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "DEFAULT_BRANCH=devel"
set "DOTNET_REQUIRED_MAJOR=10"
set "DOTNET_REQUIRED_PACKAGE=Microsoft.DotNet.SDK.10"
set "DOTNET_LEGACY_PACKAGE=Microsoft.DotNet.SDK.8"

rem IMPORTANT BOOTSTRAP CONTRACT:
rem The normal entry point must stay dependency-free except for Git/CMD.
rem It synchronizes DEVEL first, then transfers control through a temporary
rem launcher created BEFORE the running upgrade.cmd is replaced by Git.
if /I "%~1"=="--current" goto :current
if /I "%~1"=="--post-update" goto :post_update

cd /d "%~dp0"
set "REPO_ROOT=%CD%"
set "BOOTSTRAP_LAUNCHER=%TEMP%\VMU-upgrade-handoff-%RANDOM%-%RANDOM%.cmd"

where git >NUL 2>&1
if errorlevel 1 (
    echo ERROR: Git for Windows is not installed or git.exe is not in PATH.
    pause
    exit /b 1
)
if not exist ".git" (
    echo ERROR: upgrade.cmd must be run from an existing VMU Git working copy.
    pause
    exit /b 1
)

for /f "delims=" %%B in ('git rev-parse --abbrev-ref HEAD 2^>NUL') do set "CURRENT_BRANCH=%%B"
if /I not "!CURRENT_BRANCH!"=="%DEFAULT_BRANCH%" (
    echo ERROR: Current branch is !CURRENT_BRANCH!, expected %DEFAULT_BRANCH%.
    echo Switch once with: git fetch origin ^&^& git switch devel
    pause
    exit /b 1
)

git diff --quiet
if errorlevel 1 (
    echo ERROR: Local tracked files contain changes. Commit or revert them first.
    pause
    exit /b 1
)
git diff --cached --quiet
if errorlevel 1 (
    echo ERROR: Local staged changes exist. Commit or revert them first.
    pause
    exit /b 1
)

rem Build the external handoff before Git can replace this currently executing
rem batch file. The temporary launcher is a separate batch context and therefore
rem cannot inherit stale labels/arguments from the old updater implementation.
> "%BOOTSTRAP_LAUNCHER%" echo @echo off
>> "%BOOTSTRAP_LAUNCHER%" echo call "%REPO_ROOT%\upgrade.cmd" --current
>> "%BOOTSTRAP_LAUNCHER%" echo set "VMU_HANDOFF_RESULT=%%ERRORLEVEL%%"
>> "%BOOTSTRAP_LAUNCHER%" echo del /Q "%%~f0" ^>NUL 2^>^&1
>> "%BOOTSTRAP_LAUNCHER%" echo exit /b %%VMU_HANDOFF_RESULT%%
if not exist "%BOOTSTRAP_LAUNCHER%" (
    echo ERROR: Could not create temporary upgrade handoff launcher.
    pause
    exit /b 1
)

git remote set-url origin "%REPO_URL%" >NUL 2>&1
echo [BOOTSTRAP] Downloading current DEVEL source...
git fetch origin "%DEFAULT_BRANCH%"
if errorlevel 1 (
    del /Q "%BOOTSTRAP_LAUNCHER%" >NUL 2>&1
    echo ERROR: Could not download DEVEL from GitHub.
    pause
    exit /b 1
)

echo [BOOTSTRAP] Synchronizing local DEVEL branch...
git reset --hard "origin/%DEFAULT_BRANCH%"
if errorlevel 1 (
    del /Q "%BOOTSTRAP_LAUNCHER%" >NUL 2>&1
    echo ERROR: Could not synchronize the local DEVEL branch.
    pause
    exit /b 1
)

set "SYNCED_BRANCH="
for /f "delims=" %%B in ('git rev-parse --abbrev-ref HEAD 2^>NUL') do set "SYNCED_BRANCH=%%B"
echo [BOOTSTRAP] Active branch after synchronization: !SYNCED_BRANCH!
if /I not "!SYNCED_BRANCH!"=="%DEFAULT_BRANCH%" (
    del /Q "%BOOTSTRAP_LAUNCHER%" >NUL 2>&1
    echo ERROR: Repository is not on %DEFAULT_BRANCH% after synchronization.
    pause
    exit /b 1
)

rem Deliberately invoke another batch file WITHOUT CALL. CMD transfers control
rem to the pre-created launcher and never continues parsing this replaced file.
"%BOOTSTRAP_LAUNCHER%"

:current
cls
cd /d "%~dp0"
set "REPO_ROOT=%CD%"
set "LOG_DIR=%REPO_ROOT%\logs"
set "LOG_FILE=%LOG_DIR%\upgrade.log"
set "TEE_HELPER=%REPO_ROOT%\scripts\Invoke-TeeProcess.ps1"

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
echo [%VMU_TIMESTAMP%] Virtual Monitors Universe - DEVEL upgrade
echo Repository: %REPO_ROOT%
echo Target branch: %DEFAULT_BRANCH%
echo Required SDK: .NET %DOTNET_REQUIRED_MAJOR%
echo.

if not exist "%TEE_HELPER%" (
    echo ERROR: Missing upgrade stream helper: %TEE_HELPER%
    >> "%LOG_FILE%" echo ERROR: Missing upgrade stream helper: %TEE_HELPER%
    goto :fail
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TEE_HELPER%" ^
    -Command "%REPO_ROOT%\upgrade.cmd" ^
    -Arguments "--post-update" ^
    -LogPath "%LOG_FILE%"
set "ERR=%ERRORLEVEL%"

if not exist "%LOG_FILE%" (
    > "%LOG_FILE%" echo ERROR: upgrade.log unexpectedly disappeared during execution.
    >> "%LOG_FILE%" echo Worker exit code: %ERR%
)

echo.
echo Upgrade log: %LOG_FILE%
if not "%ERR%"=="0" goto :fail_with_code
pause
exit /b 0

:post_update
cd /d "%~dp0"

echo [1/4] Cleaning known obsolete and generated artifacts...
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

echo [2/4] Ensuring .NET %DOTNET_REQUIRED_MAJOR% SDK and retiring .NET 8 SDK...
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
    call :wait_installer_idle 180
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
    echo .NET 8 SDK is still installed.
    echo Waiting for Windows Installer to become idle before uninstall...
    call :wait_installer_idle 180

    set "UNINSTALL_OK=0"
    for /L %%A in (1,1,2) do (
        if "!UNINSTALL_OK!"=="0" (
            echo Starting .NET 8 SDK uninstall attempt %%A of 2...
            echo A Windows/UAC or uninstaller confirmation may appear. Approve it to continue.
            winget uninstall --id "%DOTNET_LEGACY_PACKAGE%" --exact --source winget --interactive
            if not errorlevel 1 (
                set "UNINSTALL_OK=1"
            ) else if "%%A"=="1" (
                echo Uninstall attempt 1 did not complete. Waiting for installer activity to finish before retrying...
                timeout /T 10 /NOBREAK >NUL
                call :wait_installer_idle 180
            )
        )
    )

    if "!UNINSTALL_OK!"=="1" (
        echo .NET 8 SDK uninstall command completed.
    ) else (
        echo WARNING: .NET 8 SDK uninstall did not complete after two attempts.
        echo No .NET runtime was removed. VMU will continue because .NET 10 validation passed.
        echo If Windows Installer reports 0x80070652, finish any other installer and run upgrade.cmd again.
    )
)

echo Installed SDKs after SDK maintenance:
dotnet --list-sdks
if exist ".runtime" rmdir /S /Q ".runtime"
for /D /R "src" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"
for /D /R "tests" %%D in (bin obj) do if exist "%%D" rmdir /S /Q "%%D"

echo [3/4] Restoring, building, testing and publishing with .NET 10...
dotnet restore "VirtualMonitorsUniverse.sln"
if errorlevel 1 exit /b 1
dotnet build "VirtualMonitorsUniverse.sln" -c Debug --no-restore
if errorlevel 1 exit /b 1
dotnet test "tests\Core.Tests\Core.Tests.csproj" -c Debug --no-build --no-restore
if errorlevel 1 exit /b 1
mkdir ".runtime\cli" >NUL 2>&1
dotnet publish "src\Cli\Cli.csproj" -c Debug --no-restore -o ".runtime\cli"
if errorlevel 1 exit /b 1

echo [4/4] Verifying final workspace and SDK state...
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

:wait_installer_idle
set "WAIT_SECONDS=%~1"
if not defined WAIT_SECONDS set "WAIT_SECONDS=180"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$deadline=(Get-Date).AddSeconds(%WAIT_SECONDS%); do { $busy=@(Get-Process msiexec -ErrorAction SilentlyContinue); if($busy.Count -eq 0){ exit 0 }; Start-Sleep -Seconds 2 } while((Get-Date) -lt $deadline); exit 1"
if errorlevel 1 (
    echo WARNING: Windows Installer still appears busy after %WAIT_SECONDS% seconds.
    echo The next installer operation may ask you to close or finish another installation.
) else (
    echo Windows Installer: idle
)
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
