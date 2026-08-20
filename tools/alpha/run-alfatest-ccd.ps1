[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RunnerVersion = 'ccd-setdisplayconfig-v2'
$sourcePath = Join-Path $PSScriptRoot 'alfatest-v2.ps1'
$helperPath = Join-Path $PSScriptRoot 'displayconfig-topology.ps1'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$runtimeDir = Join-Path $repoRoot '.runtime\alpha'
$runtimePath = Join-Path $runtimeDir 'alfatest.runtime.ps1'

foreach ($required in @($sourcePath, $helperPath)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required ALPHA file not found: $required" }
}

New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null

try {
    $source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8
    $source = $source -replace "`r`n", "`n"
    $source = $source.Replace('"Using cached $Label: $Path"', '"Using cached ${Label}: $Path"')

    $helperLiteral = $helperPath.Replace("'", "''")
    $anchor = '$ErrorActionPreference = ''Stop'''
    if (-not $source.Contains($anchor)) { throw 'Could not locate ALPHA initialization anchor.' }
    $source = $source.Replace($anchor, ($anchor + "`n. '$helperLiteral'`n`$script:VmuRunnerVersion = '$RunnerVersion'`n`$script:FinalSummaryWritten = `$false"))

    $headerAnchor = "Write-Log 'Virtual Monitors Universe - ALPHA acceptance test'"
    if (-not $source.Contains($headerAnchor)) { throw 'Could not locate ALPHA log header.' }
    $source = $source.Replace($headerAnchor, ($headerAnchor + "`nWrite-Log \"ALPHA runner: `$script:VmuRunnerVersion\" Cyan"))

    $preflightPattern = '(?s)(Write-Section ''PRE-FLIGHT: CLEAN BASELINE AND ONE FRESH VDD''\n).*?(\n    Write-Section ''TEST 1: DYNAMIC RESOLUTION'')'
    $preflightReplacement = @'
$1    $existing = @(Get-VddDevices)
    Write-Log "Existing VDD device nodes: $($existing.Count)"
    if ($existing.Count -gt 0) {
        Write-Log 'Previous ALPHA test VDD remnants detected; removing them before the new test.' Yellow
        if (-not (Remove-VddInstallation)) { throw 'Could not establish clean baseline.' }
    }

    Install-Vdd
    $vddDevices = @(Get-VddDevices)
    if ($vddDevices.Count -ne 1) { throw "Expected one VDD device for identity mapping, found $($vddDevices.Count)." }
    $script:VddIdentity = Resolve-VmuVddIdentity -Device $vddDevices[0]
    $name = $script:VddIdentity.GdiName
    Write-Log ("VDD IDENTITY: instance={0};gdi={1};friendly={2};source={3}/{4};target={5}/{6};monitorPath={7};adapterPath={8}" -f $script:VddIdentity.InstanceId,$script:VddIdentity.GdiName,$script:VddIdentity.FriendlyName,$script:VddIdentity.SourceLuid,$script:VddIdentity.SourceId,$script:VddIdentity.TargetLuid,$script:VddIdentity.TargetId,$script:VddIdentity.MonitorPath,$script:VddIdentity.AdapterPath) Green
    Write-Log 'PASS: exact PnP VDD instance mapped to exactly one active CCD/GDI display.' Green
    $Results.Preflight = 'PASS'
$2
'@
    $patched = [regex]::Replace($source, $preflightPattern, $preflightReplacement, 1)
    if ($patched -eq $source) { throw 'Could not locate ALPHA pre-flight section.' }
    $source = $patched

    $test2Pattern = '(?s)    Write-Section ''TEST 2: DISCONNECT / RECONNECT WITHOUT UNINSTALL''\n.*?(?=    Write-Section ''TEST 3: ONE-SHOT UNINSTALL'')'
    $test2Replacement = @'
    Write-Section 'TEST 2: DISCONNECT / RECONNECT WITHOUT UNINSTALL'
    Write-Log 'Disconnect method: SetDisplayConfig, exact CCD path only. No 0x0 mode is used.' Cyan

    if (-not (Test-VmuVddActive -InstanceId $script:VddIdentity.InstanceId -Expected $true)) {
        throw 'Safety check failed: exact VDD CCD path is not uniquely active before disconnect.'
    }

    $otherVddBefore = @((Get-VddDevices) | Where-Object { $_.InstanceId -ne $script:VddIdentity.InstanceId }).Count
    Write-Log ("Safety check: other VDD instances before disconnect = {0}" -f $otherVddBefore) DarkGray

    Invoke-VmuDisconnectExact -Identity $script:VddIdentity
    if (-not (Wait-Until -Description "$name exact CCD path inactive; PnP device retained" -TimeoutMs 5000 -Condition {
        (Test-VmuVddActive -InstanceId $script:VddIdentity.InstanceId -Expected $false) -and
        (@(Get-VddDevices | Where-Object { $_.InstanceId -eq $script:VddIdentity.InstanceId }).Count -eq 1)
    })) {
        throw 'SetDisplayConfig returned success but the exact VDD CCD path did not become inactive.'
    }
    Write-Log 'PASS: exact VDD path is inactive while its PnP device remains installed.' Green
    Open-DisplaySettings
    $disconnectOk = Ask-User 'Is this virtual monitor now disconnected from the desktop while still known by Windows?'

    Invoke-VmuReconnectSaved
    if (-not (Wait-Until -Description "$name exact CCD path active again" -TimeoutMs 5000 -Condition {
        Test-VmuVddActive -InstanceId $script:VddIdentity.InstanceId -Expected $true
    })) {
        throw 'The exact VDD CCD path did not become active after reconnect.'
    }
    Write-Log 'PASS: the same exact VDD CCD path is active again.' Green
    Open-DisplaySettings
    $reconnectOk = Ask-User 'Was the same virtual monitor reconnected without reinstalling the driver?'

    if ($disconnectOk -and $reconnectOk) { $Results.DisconnectReconnect = 'PASS' } else { $Results.DisconnectReconnect = 'FAIL' }

'@
    $patched = [regex]::Replace($source, $test2Pattern, $test2Replacement, 1)
    if ($patched -eq $source) { throw 'Could not locate ALPHA TEST 2 section.' }
    $source = $patched

    # Guard against duplicate FINAL RESULT emission if nested PowerShell/elevation flow ever
    # reaches the generated finally block more than once.
    $finalPattern = '(?s)finally \{\n    Write-Section ''FINAL RESULT''\n(.*?)\n\}'
    $finalReplacement = @'
finally {
    if (-not $script:FinalSummaryWritten) {
        $script:FinalSummaryWritten = $true
        Write-Section 'FINAL RESULT'
$1
    }
}
'@
    $patched = [regex]::Replace($source, $finalPattern, $finalReplacement, 1)
    if ($patched -eq $source) { throw 'Could not locate FINAL RESULT block for de-duplication guard.' }
    $source = $patched

    $source = $source -replace "(?<!`r)`n", "`r`n"
    Set-Content -LiteralPath $runtimePath -Value $source -Encoding UTF8
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runtimePath
    exit $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
}
