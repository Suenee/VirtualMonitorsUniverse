[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo_root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logs_dir = Join-Path $repo_root 'logs'
$log_path = Join-Path $logs_dir 'vmu-selftest.log'
$acceptance_runner = Join-Path $repo_root 'tools\alpha\multivdd-test.ps1'
$required_files = @(
    $acceptance_runner,
    (Join-Path $repo_root 'tools\alpha\multivdd-reflow-v12.ps1'),
    (Join-Path $repo_root 'tools\alpha\multivdd-reflow-v10.ps1'),
    (Join-Path $repo_root 'tools\alpha\displayconfig-topology.ps1')
)

New-Item -ItemType Directory -Path $logs_dir -Force | Out-Null
Remove-Item -LiteralPath $log_path -Force -ErrorAction SilentlyContinue

function Write-SelfTestLog {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
    Add-Content -LiteralPath $log_path -Value $line -Encoding UTF8
    Write-Host $Message -ForegroundColor $Color
}

Write-SelfTestLog 'Virtual Monitors Universe - Core self-test' Cyan
Write-SelfTestLog 'Self-test version: core-selftest-v3-dependency-preflight' Cyan
Write-SelfTestLog 'This command is a permanent development regression gate and must exercise the same display behavior used by VMU Core.' DarkGray
Write-SelfTestLog "Acceptance runner: $acceptance_runner" DarkGray

$missing = @($required_files | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missing.Count -gt 0) {
    Write-SelfTestLog 'PREFLIGHT: FAIL' Red
    foreach ($path in $missing) {
        Write-SelfTestLog "Missing dependency: $path" Red
    }
    Write-SelfTestLog 'RESULT: FAIL' Red
    exit 1
}

Write-SelfTestLog 'PREFLIGHT: PASS - all self-test dependencies are present.' Green

try {
    & $acceptance_runner
    $exit_code = $LASTEXITCODE

    $acceptance_log = Join-Path $logs_dir 'multivddtest.log'
    if (Test-Path -LiteralPath $acceptance_log) {
        Add-Content -LiteralPath $log_path -Value '' -Encoding UTF8
        Add-Content -LiteralPath $log_path -Value '================ ACCEPTANCE DETAIL ================' -Encoding UTF8
        Get-Content -LiteralPath $acceptance_log | Add-Content -LiteralPath $log_path -Encoding UTF8
    }

    if ($exit_code -ne 0) {
        Write-SelfTestLog "RESULT: FAIL - acceptance test exited with code $exit_code." Red
        exit $exit_code
    }

    Write-Host ''
    Write-Host '============================================' -ForegroundColor Green
    Write-Host 'VMU CORE SELFTEST: PASS' -ForegroundColor Green
    Write-Host '============================================' -ForegroundColor Green
    Write-SelfTestLog 'RESULT: PASS' Green
    Write-SelfTestLog "Log: $log_path" DarkGray
    exit 0
}
catch {
    Write-SelfTestLog "RESULT: FAIL - $($_.Exception.Message)" Red
    exit 1
}
