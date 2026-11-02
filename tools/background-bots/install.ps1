$ErrorActionPreference='Stop'
$root=Join-Path $env:LOCALAPPDATA 'ING\BackgroundBots'
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$backup=Join-Path $root "backups\$stamp"
New-Item -ItemType Directory -Path $root,$backup,(Join-Path $root 'configs'),(Join-Path $root 'logs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'runner.py'),(Join-Path $PSScriptRoot 'sitecustomize.py') -Destination $root
$pythonw='C:\Users\nsquires\AppData\Local\Programs\Python\Python313\pythonw.exe'
if(-not(Test-Path -LiteralPath $pythonw)){throw 'No verified windowless Python launcher'}
$runner=Join-Path $root 'runner.py'
$changes=@()
foreach($task in Get-ScheduledTask | Where-Object { $_.TaskName -match '^ING[- ]' }) {
 $actions=@($task.Actions)
 if($actions.Count -ne 1){continue}
 $old=$actions[0]
 if($old.Arguments -like '*ING\BackgroundBots\runner.py*'){continue}
 if($old.Execute -notmatch '(?i)(pythonw?|powershell|pwsh)(3\.\d+)?\.exe"?$'){continue}
 $safe=$task.TaskName -replace '[^a-zA-Z0-9_-]','_'
 Export-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Set-Content -LiteralPath (Join-Path $backup "$safe.xml") -Encoding utf8
 $config=Join-Path $root "configs\$safe.json"
 @{exe=$old.Execute.Trim('"');args=$old.Arguments;cwd=$old.WorkingDirectory;log=(Join-Path $root "logs\$safe.log")} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
 $new=New-ScheduledTaskAction -Execute $pythonw -Argument ('"'+$runner+'" "'+$config+'"') -WorkingDirectory $root
 $beforeState=[string]$task.State
 try { Set-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Action $new -ErrorAction Stop | Out-Null }
 catch { Write-Warning ("Unchanged protected task: "+$task.TaskName); continue }
 if($beforeState -eq 'Disabled'){Disable-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath | Out-Null}
 $changes+=@{name=$task.TaskName;stateBefore=$beforeState;config=$config}
}
$startup=Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
foreach($name in @('BitData-PostgreSQL','OwNinja-Collector')) {
 $cmd=Join-Path $startup "$name.cmd"
 if(-not(Test-Path -LiteralPath $cmd)){continue}
 Copy-Item -LiteralPath $cmd -Destination $backup
 $config=Join-Path $root "configs\startup-$name.json"
 if($name -eq 'OwNinja-Collector') {
  $exe='C:\Users\nsquires\AppData\Local\Microsoft\WindowsApps\python.exe'
  $arguments='"C:\Users\nsquires\source\repos\BitData\owninja_forever.py"'
  $cwd='C:\Users\nsquires\source\repos\BitData'
 } else {
  $exe='C:\Users\nsquires\pgsql-portable\pgsql\bin\pg_ctl.exe'
  $arguments='-D "C:\Users\nsquires\pgsql-data" -l "C:\Users\nsquires\pgsql-data\server.log" -o "-p 5432" start'
  $cwd='C:\Users\nsquires\pgsql-portable\pgsql\bin'
 }
 @{exe=$exe;args=$arguments;cwd=$cwd;log=(Join-Path $root "logs\startup-$name.log")} | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
 $shell=New-Object -ComObject WScript.Shell
 $shortcut=$shell.CreateShortcut((Join-Path $startup "$name.lnk"))
 $shortcut.TargetPath=$pythonw
 $shortcut.Arguments='"'+$runner+'" "'+$config+'"'
 $shortcut.WorkingDirectory=$root
 $shortcut.Save()
 Move-Item -LiteralPath $cmd -Destination ($cmd+'.disabled-'+$stamp)
}
$changes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'installation.json') -Encoding utf8
Write-Output ("Hidden scheduled launchers updated: "+$changes.Count)
Write-Output ("Backups and logs: "+$root)
