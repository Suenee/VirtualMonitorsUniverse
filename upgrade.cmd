@echo off
cls
setlocal EnableExtensions EnableDelayedExpansion

set "UPGRADE_REV=3.0-portable-bootstrap"
set "REPOSITORY_URL=https://github.com/Suenee/VirtualMonitorsUniverse.git"
set "REPOSITORY_BRANCH=devel"
set "ORIGINAL_ARGS=%*"
set "CALLER_DIR=%CD%"
set "SCRIPT_DIR=%~dp0"
if "!SCRIPT_DIR:~-1!"=="\" set "SCRIPT_DIR=!SCRIPT_DIR:~0,-1!"

if defined VMU_BOOTSTRAP_TARGET (
    set "REPO_DIR=!VMU_BOOTSTRAP_TARGET!"
) else if exist "!SCRIPT_DIR!\.git" (
    set "REPO_DIR=!SCRIPT_DIR!"
) else (
    set "REPO_DIR=!CALLER_DIR!"
)

set "DO_TEST=0"
set "DO_RUN=0"
:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="--test" (set "DO_TEST=1"&shift&goto parse_args)
if /I "%~1"=="--run" (set "DO_RUN=1"&shift&goto parse_args)
echo ERROR: Unknown upgrade option: %~1
exit /b 2
:args_done

set "TEST_LABEL=no"
set "RUN_LABEL=no"
if "!DO_TEST!"=="1" set "TEST_LABEL=yes"
if "!DO_RUN!"=="1" set "RUN_LABEL=yes"
echo Requested post actions: test=!TEST_LABEL!, run=!RUN_LABEL!
echo Bootstrap target: !REPO_DIR!

rem Process-local trust is intentionally broad because this bootstrap may move
rem between a mapped drive and its UNC representation. It never changes the
rem user's global Git configuration and applies only to this cmd.exe process.
set "GIT_CONFIG_COUNT=1"
set "GIT_CONFIG_KEY_0=safe.directory"
set "GIT_CONFIG_VALUE_0=*"

call :FindGit
if not defined GIT_EXE (
    echo Git was not found. Installing Git for Windows...
    where winget.exe >nul 2>nul
    if errorlevel 1 (
        echo ERROR: Git is missing and Windows Package Manager ^(winget^) is unavailable.
        echo Install Microsoft App Installer / winget, then run upgrade.cmd again.
        exit /b 1
    )

    rem Restrict WinGet to its community source so Microsoft Store agreements do
    rem not block a non-interactive bootstrap. Do not trust the WinGet exit code
    rem alone: an already-registered Git package can return a non-zero status.
    winget install --id Git.Git --exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements
    call :FindGit

    if not defined GIT_EXE (
        echo Git is registered but git.exe is still unavailable. Attempting forced repair...
        winget install --id Git.Git --exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements --force
        call :FindGit
    )

    if not defined GIT_EXE (
        echo ERROR: Git for Windows could not be located after installation/repair.
        exit /b 1
    )
)

for %%G in ("!GIT_EXE!") do set "PATH=%%~dpG;!PATH!"
for /f "delims=" %%G in ('"!GIT_EXE!" --version 2^>nul') do set "GIT_VERSION=%%G"
echo Git: !GIT_VERSION!

pushd "!REPO_DIR!" >nul 2>nul
if errorlevel 1 (
    echo ERROR: Target path is not accessible:
    echo   !REPO_DIR!
    exit /b 1
)

"!GIT_EXE!" rev-parse --is-inside-work-tree >nul 2>nul
if errorlevel 1 goto bootstrap_repository
goto repository_ready

:bootstrap_repository
rem Run the bootstrap from TEMP before replacing files in the target directory.
rem This makes an in-place install safe even when the only file initially present
rem is the very upgrade.cmd that is currently executing.
if not "!VMU_BOOTSTRAP_CHILD!"=="1" (
    set "HANDOFF=%TEMP%\VMU-bootstrap-%RANDOM%-%RANDOM%.cmd"
    copy /y "%~f0" "!HANDOFF!" >nul
    if errorlevel 1 (
        popd >nul 2>nul
        echo ERROR: Could not create temporary bootstrap handoff.
        exit /b 1
    )
    set "VMU_BOOTSTRAP_TARGET=!REPO_DIR!"
    set "VMU_BOOTSTRAP_CHILD=1"
    popd >nul 2>nul
    call "!HANDOFF!" !ORIGINAL_ARGS!
    set "HANDOFF_RC=!ERRORLEVEL!"
    del /q "!HANDOFF!" >nul 2>nul
    exit /b !HANDOFF_RC!
)

echo.
echo No VMU Git working tree was found. Bootstrapping DEVEL into the current directory...
set "VMU_BOOTSTRAP_TARGET=!REPO_DIR!"
powershell.exe -NoProfile -Command "$allowed=@('upgrade.cmd','logs','.cache'); $bad=@(Get-ChildItem -LiteralPath $env:VMU_BOOTSTRAP_TARGET -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $allowed }); if($bad.Count -gt 0){ Write-Host 'ERROR: Target directory contains non-VMU files:' -ForegroundColor Red; $bad | ForEach-Object { Write-Host ('  ' + $_.Name) }; exit 3 }"
if errorlevel 1 (
    popd >nul 2>nul
    exit /b 1
)

set "CLONE_TEMP=%TEMP%\VMU-clone-%RANDOM%-%RANDOM%"
"!GIT_EXE!" clone --branch "!REPOSITORY_BRANCH!" --single-branch "!REPOSITORY_URL!" "!CLONE_TEMP!"
if errorlevel 1 (
    popd >nul 2>nul
    echo ERROR: VMU repository clone failed.
    exit /b 1
)

robocopy "!CLONE_TEMP!" "!REPO_DIR!" /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
set "COPY_RC=!ERRORLEVEL!"
rmdir /s /q "!CLONE_TEMP!" >nul 2>nul
if !COPY_RC! GEQ 8 (
    popd >nul 2>nul
    echo ERROR: Repository was cloned, but copying it into the target directory failed.
    exit /b !COPY_RC!
)

if not exist "!REPO_DIR!\upgrade.cmd" (
    popd >nul 2>nul
    echo ERROR: Bootstrap completed, but upgrade.cmd is missing from the target.
    exit /b 1
)

echo Repository bootstrap complete. Continuing with the repository-owned upgrade.cmd...
popd >nul 2>nul
call "!REPO_DIR!\upgrade.cmd" !ORIGINAL_ARGS!
exit /b !ERRORLEVEL!

:repository_ready
rem From this point onward the repository exists and persistent VMU caches can be
rem anchored safely inside it. TEMP remains reserved for transient handoff files.
set "VMU_CACHE_ROOT=!REPO_DIR!\.cache"
set "DOTNET_CLI_HOME=!VMU_CACHE_ROOT!\dotnet-home"
set "NUGET_PACKAGES=!VMU_CACHE_ROOT!\nuget\packages"
set "NUGET_HTTP_CACHE_PATH=!VMU_CACHE_ROOT!\nuget\http-cache"
set "NUGET_SCRATCH=!VMU_CACHE_ROOT!\nuget\scratch"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
for %%D in ("!DOTNET_CLI_HOME!" "!NUGET_PACKAGES!" "!NUGET_HTTP_CACHE_PATH!" "!NUGET_SCRATCH!") do if not exist "%%~D" mkdir "%%~D" >nul 2>nul
if not exist "!REPO_DIR!\logs" mkdir "!REPO_DIR!\logs" >nul 2>nul
set "BOOTSTRAP_LOG=!REPO_DIR!\logs\upgrade.log"
echo VMU build cache: !VMU_CACHE_ROOT!

if defined LOCALAPPDATA if exist "%LOCALAPPDATA%\VirtualMonitorsUniverse" (
    echo Removing legacy VMU cache from LocalAppData...
    rmdir /s /q "%LOCALAPPDATA%\VirtualMonitorsUniverse" >nul 2>nul
)

"!GIT_EXE!" remote get-url origin >nul 2>nul
if errorlevel 1 (
    "!GIT_EXE!" remote add origin "!REPOSITORY_URL!"
) else (
    "!GIT_EXE!" remote set-url origin "!REPOSITORY_URL!"
)
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: Could not configure Git origin.
    popd >nul 2>nul
    exit /b 1
)

"!GIT_EXE!" fetch origin "!REPOSITORY_BRANCH!" >nul 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: git fetch origin failed before PowerShell runner bootstrap.
    >> "!BOOTSTRAP_LOG!" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    echo ERROR: git fetch origin failed before upgrade bootstrap.
    popd >nul 2>nul
    exit /b 1
)

set "RUNNER_TEMP=%TEMP%\VMU-upgrade-%RANDOM%-%RANDOM%.ps1"
"!GIT_EXE!" show origin/!REPOSITORY_BRANCH!:upgrade.ps1 > "!RUNNER_TEMP!" 2>nul
if errorlevel 1 (
    > "!BOOTSTRAP_LOG!" echo ERROR: Could not extract origin/devel:upgrade.ps1.
    >> "!BOOTSTRAP_LOG!" echo STATUS: FAILED - phase=SELF-UPDATE/BOOTSTRAP
    echo ERROR: Could not extract upgrade.ps1 from origin/devel.
    popd >nul 2>nul
    exit /b 1
)

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
            echo CLI selftest failed. --run will not be executed.
            popd >nul 2>nul
            exit /b !TEST_RC!
        )
    )

    if "!DO_RUN!"=="1" (
        echo Restarting VMU Server through run.cmd...
        call "!REPO_DIR!\run.cmd"
        set "RUN_RC=!ERRORLEVEL!"
        if not "!RUN_RC!"=="0" (
            popd >nul 2>nul
            exit /b !RUN_RC!
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

:FindGit
set "GIT_EXE="
for /f "delims=" %%G in ('where git.exe 2^>nul') do if not defined GIT_EXE set "GIT_EXE=%%G"
if not defined GIT_EXE if exist "%ProgramFiles%\Git\cmd\git.exe" set "GIT_EXE=%ProgramFiles%\Git\cmd\git.exe"
if not defined GIT_EXE if exist "%ProgramFiles%\Git\bin\git.exe" set "GIT_EXE=%ProgramFiles%\Git\bin\git.exe"
if not defined GIT_EXE if defined ProgramFiles(x86) if exist "%ProgramFiles(x86)%\Git\cmd\git.exe" set "GIT_EXE=%ProgramFiles(x86)%\Git\cmd\git.exe"
if not defined GIT_EXE if defined LocalAppData if exist "%LocalAppData%\Programs\Git\cmd\git.exe" set "GIT_EXE=%LocalAppData%\Programs\Git\cmd\git.exe"
if not defined GIT_EXE for /f "delims=" %%G in ('powershell.exe -NoProfile -Command "$roots=@('HKLM:\SOFTWARE\GitForWindows','HKLM:\SOFTWARE\WOW6432Node\GitForWindows','HKCU:\SOFTWARE\GitForWindows'); foreach($r in $roots){$p=(Get-ItemProperty -Path $r -ErrorAction SilentlyContinue).InstallPath; if($p){$g=Join-Path $p 'cmd\git.exe'; if(Test-Path -LiteralPath $g){Write-Output $g; break}}}" 2^>nul') do if not defined GIT_EXE set "GIT_EXE=%%G"
exit /b 0
