[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo_root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logs_dir = Join-Path $repo_root 'logs'
$log_path = Join-Path $logs_dir 'vmu-selftest.log'
$acceptance_runner = Join-Path $repo_root 'tools\alpha\multivdd-test.ps1'

if (-not (Test-Path -LiteralPath $logs_dir)) {
    New-Item -ItemType Directory -Path $logs_dir -Force | Out-Null
}

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
Write-SelfTestLog 'Self-test version: core-selftest-v2-centralized-logs' Cyan
Write-SelfTestLog 'This command is a permanent development regression gate and must exercise the same display behavior used by VMU Core.' DarkGray
Write-SelfTestLog "Acceptance runner: $acceptance_runner" DarkGray

if (-not (Test-Path -LiteralPath $acceptance_runner)) {
    Write-SelfTestLog 'RESULT: FAIL - multi-VDD acceptance runner is missing.' Red
    exit 1
}

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
