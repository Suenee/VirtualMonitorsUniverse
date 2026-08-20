[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'alfatest-v2.ps1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtimeDir = Join-Path $repoRoot '.runtime\alpha'
$runtimePath = Join-Path $runtimeDir 'alfatest.runtime.ps1'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "ALPHA test source not found: $sourcePath"
}

New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

try {
    $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8

    # Normalize line endings so runtime patching is deterministic across Git,
    # PowerShell 5.1, and Windows checkout configurations.
    $source = $source -replace "`r`n", "`n"

    # PowerShell 5.1 parses "$Label:" as a scoped/drive variable reference.
    $source = $source.Replace('"Using cached $Label: $Path"', '"Using cached ${Label}: $Path"')

    # Replace the custom EnumDisplayDevices-based Get-Displays implementation.
    # On the current test machine it returned an empty list even though Windows
    # had multiple active displays. System.Windows.Forms.Screen is sufficient
    # for the ALPHA topology-delta test and reliably exposes \\.\DISPLAYn names.
    $getDisplaysPattern = '(?s)function Get-Displays \{.*?\n\}\n\nfunction Get-Mode \{'
    $getDisplaysReplacement = @'
function Get-Displays {
    Add-Type -AssemblyName System.Windows.Forms
    $items = @()
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        $items += [pscustomobject]@{
            DeviceName = $screen.DeviceName
            DeviceString = 'Windows Forms Screen'
            Attached = $true
        }
    }
    return $items
}

function Get-Mode {
'@

    $patched = [regex]::Replace($source, $getDisplaysPattern, $getDisplaysReplacement, 1)
    if ($patched -eq $source) {
        throw 'Could not locate Get-Displays for runtime patching.'
    }
    $source = $patched

    # Replace only the pre-flight body between the known section header and the
    # TEST 1 section. Regex makes this independent of CRLF/LF and small edits.
    $preflightPattern = '(?s)(Write-Section ''PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD''\n).*?(\n    Write-Section ''TEST 1: DYNAMIC RESOLUTION'')'
    $preflightReplacement = @'
$1    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    $baselineDisplays = @(Get-Displays)
    $baselineNames = @($baselineDisplays | ForEach-Object { $_.DeviceName })
    Write-Log ("Baseline Windows displays before VDD install: {0}" -f ($baselineNames -join ', ')) DarkGray
    if ($baselineNames.Count -eq 0) {
        throw 'Windows display enumeration returned no baseline displays.'
    }

    Install-Vdd

    if (-not (Wait-Until -Description 'new Windows display created by VDD' -TimeoutMs 10000 -Condition {
        $currentNames = @(Get-Displays | ForEach-Object { $_.DeviceName })
        @($currentNames | Where-Object { $_ -notin $baselineNames }).Count -eq 1
    })) {
        $current = @(Get-Displays)
        foreach ($display in $current) {
            Write-Log ("DISPLAY DIAGNOSTIC: {0} | {1} | attached={2}" -f $display.DeviceName, $display.DeviceString, $display.Attached) Yellow
        }
        throw 'Could not identify exactly one new Windows display after VDD installation.'
    }

    $virtual = @(Get-Displays | Where-Object { $_.DeviceName -notin $baselineNames }) | Select-Object -First 1
    if (-not $virtual) { throw 'Could not identify the virtual Windows display.' }
    $name = $virtual.DeviceName
    Write-Log "Virtual Windows display: $name / $($virtual.DeviceString)"
    $Results.Preflight = 'PASS'
$2
'@

    $patched = [regex]::Replace($source, $preflightPattern, $preflightReplacement, 1)
    if ($patched -eq $source) {
        throw 'Could not locate the ALPHA pre-flight section for runtime patching.'
    }
    $source = $patched

    # Restore Windows line endings for easier local debugging.
    $source = $source -replace "(?<!`r)`n", "`r`n"
    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
