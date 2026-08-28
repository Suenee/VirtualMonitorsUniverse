Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = $env:VMU_UPGRADE_REPO
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { throw 'VMU_UPGRADE_REPO is not set.' }
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$LogDir = Join-Path $RepoRoot 'logs'
$LogFile = Join-Path $LogDir 'upgrade.log'
$Solution = Join-Path $RepoRoot 'VirtualMonitorsUniverse.sln'
$TestProject = Join-Path $RepoRoot 'tests\Core.Tests\Core.Tests.csproj'
$CliProject = Join-Path $RepoRoot 'src\Cli\Cli.csproj'
$ServerProject = Join-Path $RepoRoot 'src\Server\Server.csproj'
$RuntimeCli = Join-Path $RepoRoot '.runtime\cli'
$RuntimeServer = Join-Path $RepoRoot '.runtime\server'
$ServerExe = Join-Path $RuntimeServer 'VirtualMonitorsUniverse.Server.exe'
$ServerProcessName = 'VirtualMonitorsUniverse.Server'
$RequiredBranch = 'devel'
$RequiredSdkMajor = 10
$RequiredSdkPackage = 'Microsoft.DotNet.SDK.10'
$LegacySdkPackage = 'Microsoft.DotNet.SDK.8'
$FinalStatus = 'FAILED'
$FinalStatusColor = 'Red'
$ExitCode = 1
$Warnings = [System.Collections.Generic.List[string]]::new()
$ServerWasRunning = $false

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
Start-Transcript -Path $LogFile -Force | Out-Null

function Write-Section { param([Parameter(Mandatory)][string]$Text); Write-Host ''; Write-Host '============================================'; Write-Host $Text; Write-Host '============================================' }
function Invoke-Native {
    param([Parameter(Mandatory)][string]$FilePath,[Parameter()][string[]]$ArgumentList=@(),[Parameter()][string]$FailureMessage='')
    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { if ([string]::IsNullOrWhiteSpace($FailureMessage)) { $FailureMessage="$FilePath failed with exit code $exitCode." }; throw $FailureMessage }
}
function Get-InstalledSdks { $output=& dotnet --list-sdks 2>$null; if ($LASTEXITCODE -ne 0) { return @() }; return @($output) }
function Test-SdkMajorInstalled { param([Parameter(Mandatory)][int]$Major); $matches=@(Get-InstalledSdks | Where-Object { $_ -match "^$Major\." }); return $matches.Count -gt 0 }
function Wait-WindowsInstallerIdle {
    param([int]$TimeoutSeconds=180)
    $deadline=(Get-Date).AddSeconds($TimeoutSeconds)
    do { $busy=@(Get-Process msiexec -ErrorAction SilentlyContinue); if ($busy.Count -eq 0) { Write-Host 'Windows Installer: idle'; return $true }; Start-Sleep -Seconds 2 } while ((Get-Date) -lt $deadline)
    Write-Warning "Windows Installer still appears busy after $TimeoutSeconds seconds."; return $false
}
function Stop-IdleDotNetBuildServers {
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) { return }
    Write-Host 'Releasing idle .NET build servers...'
    & dotnet build-server shutdown
    if ($LASTEXITCODE -eq 0) { Write-Host '.NET build servers: shutdown requested successfully' }
    else { $warning=".NET build-server shutdown returned exit code $LASTEXITCODE. Other .NET work may still be using build infrastructure; no process was force-killed."; $Warnings.Add($warning); Write-Warning $warning }
}
function Stop-RunningVmuServer {
    $processes=@(Get-Process -Name $ServerProcessName -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) { Write-Host 'VMU Server before upgrade: not running'; return $false }
    Write-Host ("VMU Server before upgrade: running ({0} process(es)); stopping before build..." -f $processes.Count)
    foreach ($process in $processes) {
        Write-Host ("Stopping VMU Server PID {0}..." -f $process.Id)
        $closed=$false
        try { $closed=$process.CloseMainWindow() } catch { Write-Warning ("Could not request graceful shutdown for PID {0}: {1}" -f $process.Id,$_.Exception.Message) }
        if ($closed) { try { $process.WaitForExit(5000) | Out-Null } catch { } }
        try { $process.Refresh() } catch { }
        if (-not $process.HasExited) {
            Write-Warning ("VMU Server PID {0} did not exit gracefully; terminating it." -f $process.Id)
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            try { $process.WaitForExit(5000) | Out-Null } catch { }
        }
        Write-Host ("VMU Server PID {0}: stopped" -f $process.Id)
    }
    return $true
}
function Start-VmuServerAfterUpgrade {
    if (-not (Test-Path -LiteralPath $ServerExe)) { throw "Cannot restart VMU Server because the published executable is missing: $ServerExe" }
    Write-Host 'VMU Server was running before upgrade; starting the newly published server...'
    try {
        $process=Start-Process -FilePath $ServerExe -WorkingDirectory $RepoRoot -ArgumentList @('--repo-root',$RepoRoot) -PassThru
        Write-Host ("VMU Server restart requested; PID {0}." -f $process.Id)
        Start-Sleep -Seconds 2
        $process.Refresh()
        if ($process.HasExited) { throw "VMU Server exited during startup with exit code $($process.ExitCode)." }
        Write-Host ("VMU Server restart: OK (PID {0})" -f $process.Id)
        return $true
    }
    catch {
        $warning="Upgrade succeeded, but VMU Server restart failed: $($_.Exception.Message)"
        $Warnings.Add($warning)
        Write-Warning $warning
        return $false
    }
}
function Remove-KnownGeneratedArtifacts {
    Write-Host 'Cleaning repository-owned generated files...'
    $runtime=Join-Path $RepoRoot '.runtime'; if (Test-Path $runtime) { Remove-Item -LiteralPath $runtime -Recurse -Force }
    foreach ($base in @('src','tests')) { $basePath=Join-Path $RepoRoot $base; if (-not (Test-Path $basePath)) { continue }; Get-ChildItem -LiteralPath $basePath -Directory -Recurse -Force | Where-Object { $_.Name -in @('bin','obj') } | Sort-Object FullName -Descending | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force } }
    foreach ($obsolete in @('tools','client','companion','server','shared')) { $path=Join-Path $RepoRoot $obsolete; if (Test-Path $path) { Write-Host "Removing obsolete path: $obsolete"; Remove-Item -LiteralPath $path -Recurse -Force } }
    foreach ($obsoleteFile in @('alfatest.cmd','alfatest.log','multivddtest.log','vmu-selftest.log','upgrade.log')) { $path=Join-Path $RepoRoot $obsoleteFile; if (Test-Path $path) { Write-Host "Removing obsolete root file: $obsoleteFile"; Remove-Item -LiteralPath $path -Force } }
}
function Assert-WorkspaceHygiene {
    foreach ($obsolete in @('tools','client','companion','server','shared')) { if (Test-Path (Join-Path $RepoRoot $obsolete)) { throw "Workspace hygiene check failed: obsolete path remains: $obsolete" } }
    foreach ($obsoleteFile in @('alfatest.cmd','alfatest.log','multivddtest.log','vmu-selftest.log','upgrade.log')) { if (Test-Path (Join-Path $RepoRoot $obsoleteFile)) { throw "Workspace hygiene check failed: obsolete root file remains: $obsoleteFile" } }
    Write-Host 'Workspace hygiene: OK'
}

try {
    Set-Location -LiteralPath $RepoRoot
    Write-Section 'Virtual Monitors Universe - DEVEL upgrade'
    Write-Host ("[{0}] Virtual Monitors Universe - DEVEL upgrade" -f (Get-Date -Format 'dd.MM.yyyy HH:mm:ss'))
    Write-Host "Repository: $RepoRoot"; Write-Host "Target branch: $RequiredBranch"; Write-Host "Required SDK: .NET $RequiredSdkMajor"; Write-Host ''
    $ServerWasRunning=Stop-RunningVmuServer
    if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) { throw 'Git for Windows is not installed or git.exe is not in PATH.' }
    $inside=& git rev-parse --is-inside-work-tree 2>$null; if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') { throw 'This folder is not a Git working tree.' }
    $branch=(& git rev-parse --abbrev-ref HEAD 2>$null).Trim(); if ($LASTEXITCODE -ne 0 -or $branch -ne $RequiredBranch) { throw "Current branch is '$branch', expected '$RequiredBranch'." }
    & git diff --quiet; if ($LASTEXITCODE -ne 0) { throw 'Local tracked files contain changes. Commit or revert them first.' }
    & git diff --cached --quiet; if ($LASTEXITCODE -ne 0) { throw 'Local staged changes exist. Commit or revert them first.' }
    Write-Host '[1/5] Synchronizing current DEVEL source...'
    Invoke-Native git @('remote','set-url','origin','https://github.com/Suenee/VirtualMonitorsUniverse.git') 'Could not set the origin URL.'
    Invoke-Native git @('reset','--hard','origin/devel') 'Could not synchronize the local DEVEL branch.'
    $branchAfterReset=(& git rev-parse --abbrev-ref HEAD 2>$null).Trim(); if ($branchAfterReset -ne $RequiredBranch) { throw "Repository is not on '$RequiredBranch' after synchronization." }; Write-Host "Active branch after synchronization: $branchAfterReset"
    Write-Host '[2/5] Cleaning known obsolete and generated artifacts...'; Remove-KnownGeneratedArtifacts; Assert-WorkspaceHygiene
    Write-Host '[3/5] Ensuring .NET 10 SDK and retiring .NET 8 SDK...'
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) { throw 'Windows Package Manager (winget) is required to bootstrap the .NET SDK.' }
    if (-not (Test-SdkMajorInstalled -Major 10)) { Write-Host '.NET 10 SDK is not installed. Starting the official WinGet installation...'; Write-Host 'A Windows/UAC or installer confirmation may appear. Approve it to continue.'; Invoke-Native winget @('install','--id',$RequiredSdkPackage,'--exact','--source','winget','--interactive','--accept-source-agreements','--accept-package-agreements') '.NET 10 SDK installation failed or was cancelled. .NET 8 SDK has NOT been removed.'; Wait-WindowsInstallerIdle -TimeoutSeconds 180 | Out-Null }
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) { throw 'dotnet.exe is unavailable after .NET 10 installation.' }
    if (-not (Test-SdkMajorInstalled -Major 10)) { throw '.NET 10 SDK could not be verified after installation. .NET 8 SDK has NOT been removed.' }
    Write-Host '.NET 10 SDK: VERIFIED'; Write-Host 'Installed SDKs before VMU validation:'; & dotnet --list-sdks
    Write-Host 'Validating VMU on .NET 10 before removing the old SDK...'; Invoke-Native dotnet @('restore',$Solution) 'Restore failed on .NET 10. .NET 8 SDK has NOT been removed.'; Invoke-Native dotnet @('build',$Solution,'-c','Debug','--no-restore') 'Build failed on .NET 10. .NET 8 SDK has NOT been removed.'; Invoke-Native dotnet @('test',$TestProject,'-c','Debug','--no-build','--no-restore') 'Tests failed on .NET 10. .NET 8 SDK has NOT been removed.'; Write-Host 'VMU validation on .NET 10: PASS'
    if (Test-SdkMajorInstalled -Major 8) {
        Write-Host '.NET 8 SDK is still installed.'; Write-Host 'Waiting for Windows Installer to become idle before uninstall...'; Wait-WindowsInstallerIdle -TimeoutSeconds 180 | Out-Null
        $uninstalled=$false
        for ($attempt=1; $attempt -le 2 -and -not $uninstalled; $attempt++) { Write-Host "Starting .NET 8 SDK uninstall attempt $attempt of 2..."; Write-Host 'A Windows/UAC or uninstaller confirmation may appear. Approve it to continue.'; & winget uninstall --id $LegacySdkPackage --exact --source winget --interactive; if ($LASTEXITCODE -eq 0) { $uninstalled=$true } elseif ($attempt -eq 1) { Write-Warning 'Uninstall attempt 1 did not complete. Waiting before retrying...'; Start-Sleep -Seconds 10; Wait-WindowsInstallerIdle -TimeoutSeconds 180 | Out-Null } }
        if ($uninstalled) { Write-Host '.NET 8 SDK uninstall command completed.' } else { $warning='.NET 8 SDK uninstall did not complete after two attempts. No .NET runtime was removed. VMU will continue because .NET 10 validation passed.'; $Warnings.Add($warning); Write-Warning $warning }
    }
    Write-Host 'Installed SDKs after SDK maintenance:'; & dotnet --list-sdks
    Write-Host '[4/5] Restoring, building, testing and publishing with .NET 10...'; Remove-KnownGeneratedArtifacts; Invoke-Native dotnet @('restore',$Solution) 'Final restore failed.'; Invoke-Native dotnet @('build',$Solution,'-c','Debug','--no-restore') 'Final build failed.'; Invoke-Native dotnet @('test',$TestProject,'-c','Debug','--no-build','--no-restore') 'Final tests failed.'; New-Item -ItemType Directory -Path $RuntimeCli -Force | Out-Null; Invoke-Native dotnet @('publish',$CliProject,'-c','Debug','--no-restore','-o',$RuntimeCli) 'CLI publish failed.'; New-Item -ItemType Directory -Path $RuntimeServer -Force | Out-Null; Invoke-Native dotnet @('publish',$ServerProject,'-c','Debug','--no-restore','-o',$RuntimeServer) 'Server publish failed.'
    Write-Host '[5/5] Verifying final workspace and SDK state...'; Assert-WorkspaceHygiene; if (-not (Test-SdkMajorInstalled -Major 10)) { throw 'Final .NET 10 SDK verification failed.' }; if (-not (Test-Path $ServerExe)) { throw 'Published VMU Server executable was not found.' }
    Write-Host 'Final workspace hygiene: OK'; Write-Host '.NET 10 SDK: OK'; Stop-IdleDotNetBuildServers
    if ($ServerWasRunning) { Start-VmuServerAfterUpgrade | Out-Null } else { Write-Host 'VMU Server restart: skipped because it was not running before upgrade.' }
    Write-Section 'UPGRADE COMPLETED SUCCESSFULLY'; Write-Host 'Branch: devel'; Write-Host "Runtime CLI: $RuntimeCli"; Write-Host "Runtime Server: $RuntimeServer"; Write-Host "Upgrade log: $LogFile"; Write-Host 'Next check: vmu selftest'; Write-Host 'Tray server: vmu-server.cmd'
    if ($Warnings.Count -gt 0) { $FinalStatus='WARNING'; $FinalStatusColor='Yellow' } else { $FinalStatus='OK'; $FinalStatusColor='Green' }
    $ExitCode=0
}
catch {
    Write-Host ''; Write-Host '============================================' -ForegroundColor Red; Write-Host 'UPGRADE FAILED' -ForegroundColor Red; Write-Host '============================================' -ForegroundColor Red; Write-Host $_.Exception.Message -ForegroundColor Red; Write-Host "See $LogFile for details."; $FinalStatus='FAILED'; $FinalStatusColor='Red'; $ExitCode=1
}
finally {
    try { Stop-Transcript | Out-Null } catch { }
    Write-Host ("STATUS: {0}" -f $FinalStatus) -ForegroundColor $FinalStatusColor
}
exit $ExitCode
