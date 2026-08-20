[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $PSCommandPath)) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args -Wait -PassThru
    exit $process.ExitCode
}

$root = 'C:\'
$candidates = New-Object System.Collections.Generic.List[string]

$legacy = 'C:\VirtualDisplayDriver'
if (Test-Path -LiteralPath $legacy) {
    $candidates.Add($legacy)
}

Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'VirtualDisplayDriver.vmu-stale-*' } |
    ForEach-Object { $candidates.Add($_.FullName) }

if ($candidates.Count -eq 0) {
    Write-Host 'Legacy C: VDD development artifacts: none found.' -ForegroundColor Green
    exit 0
}

$removed = 0
$skipped = 0
foreach ($path in @($candidates | Sort-Object -Unique)) {
    $markers = @(
        (Join-Path $path 'MttVDD.inf'),
        (Join-Path $path 'mttvdd.cat'),
        (Join-Path $path 'vdd_settings.xml')
    )

    $knownMarkerFound = $false
    foreach ($marker in $markers) {
        if (Test-Path -LiteralPath $marker) {
            $knownMarkerFound = $true
            break
        }
    }

    if (-not $knownMarkerFound) {
        Write-Warning "REFUSED to remove unknown directory: $path"
        Write-Warning 'No known Virtual Display Driver marker file was found.'
        $skipped++
        continue
    }

    Write-Host "Removing verified legacy VMU/VDD development directory: $path" -ForegroundColor Yellow
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
    $removed++
}

Write-Host "Legacy cleanup complete. Removed: $removed; skipped for safety: $skipped" -ForegroundColor Cyan
if ($skipped -gt 0) {
    exit 2
}
exit 0
