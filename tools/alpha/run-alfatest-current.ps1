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

    # PowerShell 5.1 parses "$Label:" as a scoped/drive variable reference.
    $source = $source.Replace('"Using cached $Label: $Path"', '"Using cached ${Label}: $Path"')

    # Identify the virtual Windows display by topology delta instead of relying
    # on a driver/device string whose value varies between Windows/VDD builds.
    $oldBlock = @'
    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    Install-Vdd
    $Results.Preflight = 'PASS'

    $virtual = @(Get-Displays | Where-Object { $_.DeviceString -match 'Virtual|MTT|VDD' }) | Select-Object -Last 1
    if (-not $virtual) { throw 'Could not identify the virtual Windows display.' }
    $name = $virtual.DeviceName
    Write-Log "Virtual Windows display: $name / $($virtual.DeviceString)"
'@

    $newBlock = @'
    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    $baselineDisplays = @(Get-Displays)
    $baselineNames = @($baselineDisplays | ForEach-Object { $_.DeviceName })
    Write-Log ("Baseline Windows displays before VDD install: {0}" -f ($baselineNames -join ', ')) DarkGray

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
'@

    if (-not $source.Contains($oldBlock)) {
        throw 'Could not locate the expected ALPHA pre-flight block for runtime patching.'
    }

    $source = $source.Replace($oldBlock, $newBlock)
    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
