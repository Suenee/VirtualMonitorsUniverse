[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Command,

    [Parameter(Mandatory = $true)]
    [string[]]$Arguments,

    [Parameter(Mandatory = $true)]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$logDirectory = Split-Path -Parent $LogPath
if (-not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $Command
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $false

foreach ($argument in $Arguments) {
    [void]$startInfo.ArgumentList.Add($argument)
}

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo

$writer = [System.IO.StreamWriter]::new($LogPath, $true, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true

try {
    $outputHandler = [System.Diagnostics.DataReceivedEventHandler]{
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) {
            [Console]::Out.WriteLine($eventArgs.Data)
            $writer.WriteLine($eventArgs.Data)
        }
    }

    $errorHandler = [System.Diagnostics.DataReceivedEventHandler]{
        param($sender, $eventArgs)
        if ($null -ne $eventArgs.Data) {
            [Console]::Error.WriteLine($eventArgs.Data)
            $writer.WriteLine($eventArgs.Data)
        }
    }

    $process.add_OutputDataReceived($outputHandler)
    $process.add_ErrorDataReceived($errorHandler)

    if (-not $process.Start()) {
        throw "Could not start process: $Command"
    }

    $process.BeginOutputReadLine()
    $process.BeginErrorReadLine()
    $process.WaitForExit()

    # Ensure asynchronous output handlers have drained before returning.
    $process.WaitForExit()
    exit $process.ExitCode
}
finally {
    $writer.Dispose()
    $process.Dispose()
}
