@echo off
setlocal EnableExtensions
set "VMU_REPO_ROOT=%~dp0"
if "%VMU_REPO_ROOT:~-1%"=="\" set "VMU_REPO_ROOT=%VMU_REPO_ROOT:~0,-1%"
set "VMU_SERVER=%~dp0.runtime\server\VirtualMonitorsUniverse.Server.exe"

if not exist "%VMU_SERVER%" (
    echo VMU Server is not built yet.
    echo Run upgrade.cmd first.
    exit /b 1
)

echo Restarting VMU Server...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $items=@(Get-Process -Name 'VirtualMonitorsUniverse.Server' -ErrorAction SilentlyContinue); foreach($p in $items){ $closed=$false; try{$closed=$p.CloseMainWindow()}catch{}; if($closed){try{$p.WaitForExit(5000) | Out-Null}catch{}}; try{$p.Refresh()}catch{}; if(-not $p.HasExited){Stop-Process -Id $p.Id -Force -ErrorAction Stop; try{$p.WaitForExit(5000) | Out-Null}catch{}} }; $started=Start-Process -FilePath $env:VMU_SERVER -WorkingDirectory $env:VMU_REPO_ROOT -ArgumentList @('--repo-root',$env:VMU_REPO_ROOT) -PassThru; Start-Sleep -Milliseconds 700; $started.Refresh(); if($started.HasExited){throw ('VMU Server exited during startup with code ' + $started.ExitCode)}; Write-Host ('VMU Server running, PID ' + $started.Id)"
exit /b %ERRORLEVEL%
