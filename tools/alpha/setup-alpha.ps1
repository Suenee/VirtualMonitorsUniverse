[CmdletBinding()]
param()

Write-Host 'This legacy ALPHA installer is deprecated because it used C:\VirtualDisplayDriver.' -ForegroundColor Yellow
Write-Host 'VMU development/runtime payload must stay inside the repository.' -ForegroundColor Yellow
Write-Host 'Run upgrade.cmd and then alfatest.cmd instead.' -ForegroundColor Cyan
exit 1
