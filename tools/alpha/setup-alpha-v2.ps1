[CmdletBinding()]
param()

Write-Host 'This setup wrapper is deprecated.' -ForegroundColor Yellow
Write-Host 'Run upgrade.cmd and then alfatest.cmd. All VMU development/runtime files now stay inside the repository.' -ForegroundColor Cyan
exit 1
