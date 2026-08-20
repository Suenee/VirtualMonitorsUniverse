@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo Virtual Monitors Universe - ALPHA acceptance test
echo.

rem A stale C:\VirtualDisplayDriver directory may still contain files held by
rem Windows after an older VDD instance is removed. Recursive deletion can
rem block. Detach the directory first so the ALPHA test starts from a clean,
rem deterministic path without waiting for those handles to be released.
if exist "C:\VirtualDisplayDriver" (
  set "STALE=C:\VirtualDisplayDriver.vmu-stale-%RANDOM%"
  echo Detaching stale VDD configuration directory...
  move /Y "C:\VirtualDisplayDriver" "%STALE%" >NUL 2>&1
  if errorlevel 1 (
    echo WARNING: Could not rename C:\VirtualDisplayDriver.
    echo Close applications that may still hold VDD files and try again.
    pause
    exit /b 1
  )
  echo Stale directory moved to: %STALE%
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\alpha\run-alfatest.ps1"
set "ERR=%ERRORLEVEL%"

echo.
echo Test finished with exit code %ERR%.
echo Log: %~dp0alfatest.log
pause
exit /b %ERR%
