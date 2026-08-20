[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UpgradeCmd,
    [Parameter(Mandatory = $true)][string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$logPath = Join-Path $RepoRoot 'upgrade.log'
$tempCmd = Join-Path $env:TEMP ("VMU-upgrade-{0}-{1}.cmd" -f $PID, [Guid]::NewGuid().ToString('N'))

function Write-UpgradeLog {
    param([AllowEmptyString()][string]$Message)

    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $Message
}

Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
Write-UpgradeLog 'Virtual Monitors Universe - upgrade'
Write-UpgradeLog "Repository: $RepoRoot"
Write-UpgradeLog "Launcher: $UpgradeCmd"
Write-UpgradeLog ''

try {
    Copy-Item -LiteralPath $UpgradeCmd -Destination $tempCmd -Force

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec
    $psi.Arguments = ('/d /c ""{0}" --worker "{1}""' -f $tempCmd, $RepoRoot)
    $psi.WorkingDirectory = $RepoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $false

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    $stdoutHandler = [System.Diagnostics.DataReceivedEventHandler]{
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) { Write-UpgradeLog $eventArgs.Data }
    }
    $stderrHandler = [System.Diagnostics.DataReceivedEventHandler]{
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) { Write-UpgradeLog ("STDERR: {0}" -f $eventArgs.Data) }
    }

    $process.add_OutputDataReceived($stdoutHandler)
    $process.add_ErrorDataReceived($stderrHandler)

    if (-not $process.Start()) {
        throw 'Could not start upgrade worker.'
    }

    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $process.WaitForExit()
    $exitCode = $process.ExitCode

    Write-UpgradeLog ''
    Write-UpgradeLog ("Upgrade worker exit code: {0}" -f $exitCode)
    Write-UpgradeLog ("Log file: {0}" -f $logPath)
    exit $exitCode
}
catch {
    Write-UpgradeLog ("UPGRADE RUNNER ERROR: {0}" -f $_.Exception.Message)
    Write-UpgradeLog ("Log file: {0}" -f $logPath)
    exit 1
}
finally {
    Remove-Item -LiteralPath $tempCmd -Force -ErrorAction SilentlyContinue
}
