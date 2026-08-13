@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0vmu.ps1" %*
exit /b %errorlevel%
