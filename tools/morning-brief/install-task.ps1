# Registers (or refreshes) the daily "ING Morning Brief" task for the current user: 06:30 every day,
# windowless python, output in %LOCALAPPDATA%\ING AutoLister\morning-brief\. Re-run after moving the repo.
$ErrorActionPreference = 'Stop'
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$python = 'C:\Users\nsquires\source\repos\ING Desktop Bridge\.venv\Scripts\pythonw.exe'
if (-not (Test-Path $python)) { $python = (Get-Command pythonw.exe).Source }
$script = Join-Path $here 'brief.py'
$action  = New-ScheduledTaskAction -Execute $python -Argument ('"' + $script + '"') -WorkingDirectory $here
$trigger = New-ScheduledTaskTrigger -Daily -At 06:30
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'ING Morning Brief' -Action $action -Trigger $trigger -Settings $settings `
    -Description 'Builds the ordered morning to-do list (eBay/Amazon/mail/returns/packages) and sends it by e-mail + Telegram.' -Force | Out-Null
Get-ScheduledTask -TaskName 'ING Morning Brief' | Select-Object TaskName, State | Format-Table -AutoSize
(Get-ScheduledTask -TaskName 'ING Morning Brief').Triggers | Select-Object StartBoundary | Format-Table -AutoSize
