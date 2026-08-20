# Register the three background bots as logon-triggered scheduled tasks so they survive a reboot.
# Must run elevated. None of them is started here — all three are already running; the tasks fire
# at the NEXT logon. Each bot has its own single-instance guard where it matters.
$log = "$PSScriptRoot\register-bots.log"
Start-Transcript -Path $log -Force
try {
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $storePy = "C:\Users\nsquires\AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0\python.exe"
    $py313   = "C:\Users\nsquires\AppData\Local\Programs\Python\Python313\python.exe"
    $repo    = "C:\Users\nsquires\source\repos\ING eBay AutoLister"
    $bitdata = "C:\Users\nsquires\source\repos\BitData"
    $logs    = "C:\Users\nsquires\source\repos"

    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -StartWhenAvailable
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

    # 1. Comps runner: spends the monthly OpenWebNinja allowance, syncs hosted. Store-python via cmd.
    $a1 = New-ScheduledTaskAction -Execute "cmd.exe" -WorkingDirectory $bitdata `
        -Argument "/c `"`"$storePy`" owninja_forever.py >> `"$logs\owninja-forever.out.log`" 2>&1`""
    Register-ScheduledTask -TaskName "ING-CompsRunner" -Action $a1 -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "registered ING-CompsRunner"

    # 2. Staging deployer: WSL loop that redeploys the test site on every commit.
    $a2 = New-ScheduledTaskAction -Execute "wsl.exe" -WorkingDirectory $repo `
        -Argument "-d Ubuntu-24.04 -- bash -lc `"cd '/mnt/c/Users/nsquires/source/repos/ING eBay AutoLister' && exec bash deploy-staging-loop.sh`""
    Register-ScheduledTask -TaskName "ING-StagingDeploy" -Action $a2 -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "registered ING-StagingDeploy"

    # 3. Claude queue supervisor: caged to explicit tasks only, night hours only (see queue_forever.py).
    $a3 = New-ScheduledTaskAction -Execute "cmd.exe" -WorkingDirectory $repo `
        -Argument "/c `"`"$py313`" queue_forever.py >> `"$logs\queue-bot.log`" 2>&1`""
    Register-ScheduledTask -TaskName "ING-UpgradesBot" -Action $a3 -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    "registered ING-UpgradesBot"

    Get-ScheduledTask -TaskName "ING-CompsRunner","ING-StagingDeploy","ING-UpgradesBot" | Select-Object TaskName, State | Format-Table -AutoSize | Out-String
    "RESULT: OK"
} catch {
    "RESULT: FAILED - $_"
} finally {
    Stop-Transcript
}
