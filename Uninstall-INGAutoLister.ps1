<#
.SYNOPSIS
  Removes ING AutoLister completely, then proves it.

.DESCRIPTION
  Windows' own Add/Remove entry runs the MSI, and the MSI removes exactly what it installed:
  the Program Files folder, the Start Menu and Public Desktop shortcuts, the HKLM Run entry,
  the Photo Box firewall rule and the port-9332 reservation. That is the whole install.

  It is not the whole footprint. The app creates its data folder at first run
  (%LOCALAPPDATA%\ING AutoLister: listings database, photos, API keys, eBay tokens, saved
  browser sessions), and a seller may have made their own shortcuts. No MSI can know about
  those, so an uninstall that only runs the MSI leaves them behind — and leaving API keys
  and eBay tokens on a machine you thought was clean is the worst kind of leftover.

  So this script runs the MSI and then walks EVERY location the app has ever written,
  removes what it finds, and finally re-checks each one and prints PASS or LEFT. It exits
  non-zero if anything it was asked to remove is still there. An uninstaller that cannot
  prove itself is an uninstaller that has not happened.

.PARAMETER Audit
  Report only. Touches nothing. Safe to run any time, elevated or not.

.PARAMETER RemoveData
  Also delete the data folder (listings, photos, keys, tokens) without asking.

.PARAMETER KeepData
  Leave the data folder alone without asking. Default when neither switch is given is to ask.

.NOTES
  Written for Windows PowerShell 5.1 (what every Windows box has) as well as pwsh 7.
  Run elevated for a real uninstall; the .bat next to this file elevates for you.
#>
[CmdletBinding()]
param(
    [switch]$Audit,
    [switch]$RemoveData,
    [switch]$KeepData
)

$ErrorActionPreference = 'Continue'
$script:left = @()

# ── Every place the app puts something, with the reason it is there ───────────────────────
# Names here must match the installer (installer.wxs) and the app (Services/AppPaths.cs);
# Tests/UninstallerCoverageTests.cs fails the build if they drift.
$ProductName   = 'ING AutoLister'
$InstallDir    = Join-Path ${env:ProgramFiles} 'ING Mining\ING AutoLister'
$VendorDir     = Join-Path ${env:ProgramFiles} 'ING Mining'
$StartMenuDir  = Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs\ING Mining'
$PublicDesktop = Join-Path ${env:PUBLIC} 'Desktop'
$UserDesktop   = [Environment]::GetFolderPath('Desktop')
$RunKeyHKLM    = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$RunKeyHKCU    = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$RunValueName  = 'INGAutoLister'
$ShortcutKey   = 'HKCU:\SOFTWARE\INGMining'
$FirewallRule  = 'ING Photo Box phone camera'
$ReservedPort  = 9332
$DataDirUser   = Join-Path ${env:LOCALAPPDATA} 'ING AutoLister'
$DataDirSystem = Join-Path ${env:ProgramData} 'ING AutoLister'
$ExportsDir    = Join-Path $UserDesktop 'eBayListing'
$TempPatterns  = @('ing_enhance_*', 'rembg_in_*', 'rembg_script_*', 'portrait_in_*', 'portrait_script_*', 'pwshot_*')

function Say($t)  { Write-Host $t }
function Ok($t)   { Write-Host "  PASS  $t" -ForegroundColor Green }
function Left($t) { Write-Host "  LEFT  $t" -ForegroundColor Red; $script:left += $t }
function Note($t) { Write-Host "  ....  $t" -ForegroundColor DarkGray }
function Did($t)  { Write-Host "  done  $t" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Say ''
Say "  $ProductName uninstaller$(if ($Audit) { '  (AUDIT - nothing will be changed)' })"
Say '  =================================================='
if (-not $Audit -and -not $isAdmin) {
    Say ''
    Say '  This needs administrator rights (the app is installed for all users).'
    Say '  Run Uninstall-INGAutoLister.bat, which asks for them, or use -Audit to only look.'
    exit 2
}

# ── 1. Stop the app ───────────────────────────────────────────────────────────────────────
Say ''
Say '[1] The running app'
$procs = @(Get-Process -Name 'AutoListerB1' -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { Ok 'not running' }
elseif ($Audit) { Note "running ($($procs.Count) process) - would be stopped" }
else {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    if (Get-Process -Name 'AutoListerB1' -ErrorAction SilentlyContinue) { Left 'AutoListerB1.exe is still running' }
    else { Did "stopped $($procs.Count) process" }
}

# ── 2. The MSI ────────────────────────────────────────────────────────────────────────────
# Found by the Add/Remove registry entry, not Win32_Product: that WMI class walks and
# re-validates every MSI on the machine, takes minutes, and has been known to trigger
# repairs of unrelated products along the way.
Say ''
Say '[2] Windows Installer package'
$uninstallRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
$products = @(Get-ChildItem $uninstallRoots -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq $ProductName -and $_.PSChildName -match '^\{[0-9A-F-]+\}$' })
if ($products.Count -eq 0) { Ok 'no Add/Remove entry (not installed, or already removed)' }
foreach ($p in $products) {
    if ($Audit) { Note "installed: $($p.DisplayName) $($p.DisplayVersion) $($p.PSChildName) - would run msiexec /x"; continue }
    Say "  removing $($p.DisplayName) $($p.DisplayVersion) $($p.PSChildName)"
    $msi = Start-Process msiexec.exe -ArgumentList "/x $($p.PSChildName) /qn /norestart" -Wait -PassThru
    switch ($msi.ExitCode) {
        0    { Did 'msiexec finished' }
        3010 { Did 'msiexec finished (Windows wants a restart to release a file in use)' }
        1605 { Did 'msiexec: product was already gone' }
        default { Left "msiexec exited $($msi.ExitCode) for $($p.PSChildName)" }
    }
}

# ── 3. Everything the MSI does not know about ─────────────────────────────────────────────
Say ''
Say '[3] Leftovers'

function Remove-Tree($path, $what) {
    if (-not (Test-Path -LiteralPath $path)) { Ok "$what - absent"; return }
    if ($Audit) { Note "$what - present: $path"; return }
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $path) {
        # A file still open (a log, a tray icon on its way out) is the usual reason. One more try.
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $path) { Left "$what - still present: $path" } else { Did "$what removed: $path" }
}

# Program Files: the MSI removes its own files; anything else that ended up there (a crash
# log, a stray copy) would keep the folder alive. Then the vendor folder, only if empty.
Remove-Tree $InstallDir 'install folder'
if ((Test-Path -LiteralPath $VendorDir) -and -not (Get-ChildItem -LiteralPath $VendorDir -Force -ErrorAction SilentlyContinue)) {
    if ($Audit) { Note "vendor folder empty: $VendorDir" } else { Remove-Item -LiteralPath $VendorDir -Force -ErrorAction SilentlyContinue; Did "empty vendor folder removed: $VendorDir" }
}

# Shortcuts. The MSI's own two, and any the seller made by hand that point at the app —
# a shortcut to a deleted exe is a broken icon on the desktop, which is not "uninstalled".
Remove-Tree $StartMenuDir 'Start Menu folder'
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @($PublicDesktop, $UserDesktop)) {
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($lnk in Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -ErrorAction SilentlyContinue) {
        $target = ''
        try { $target = $shell.CreateShortcut($lnk.FullName).TargetPath } catch { }
        if ($target -like '*AutoListerB1.exe' -or $lnk.BaseName -eq $ProductName) {
            if ($Audit) { Note "shortcut present: $($lnk.FullName) -> $target" }
            else { Remove-Item -LiteralPath $lnk.FullName -Force -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $lnk.FullName) { Left "shortcut still present: $($lnk.FullName)" } else { Did "shortcut removed: $($lnk.FullName)" } }
        }
    }
}

# Startup entries. HKLM is the installer's; HKCU is never written by the installer but a
# developer build registers one, and a startup entry for a program that is gone is a login
# error every morning.
foreach ($key in @($RunKeyHKLM, $RunKeyHKCU)) {
    $v = (Get-ItemProperty -Path $key -Name $RunValueName -ErrorAction SilentlyContinue).$RunValueName
    if (-not $v) { Ok "startup entry $key\$RunValueName - absent"; continue }
    if ($Audit) { Note "startup entry present: $key\$RunValueName = $v"; continue }
    Remove-ItemProperty -Path $key -Name $RunValueName -ErrorAction SilentlyContinue
    if ((Get-ItemProperty -Path $key -Name $RunValueName -ErrorAction SilentlyContinue)) { Left "startup entry still present: $key\$RunValueName" }
    else { Did "startup entry removed: $key\$RunValueName (was $v)" }
}

# The installer's per-user bookkeeping for its shortcuts.
if (Test-Path $ShortcutKey) {
    if ($Audit) { Note "registry key present: $ShortcutKey" }
    else { Remove-Item -Path $ShortcutKey -Recurse -Force -ErrorAction SilentlyContinue; if (Test-Path $ShortcutKey) { Left "registry key still present: $ShortcutKey" } else { Did "registry key removed: $ShortcutKey" } }
} else { Ok "registry key $ShortcutKey - absent" }

# Firewall rule for the phone camera. The MSI removes it; this is the check, and the
# fallback for an install that predates the declared rule (those were added by netsh).
$rules = @(Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue)
if ($rules.Count -eq 0) { Ok "firewall rule '$FirewallRule' - absent" }
elseif ($Audit) { Note "firewall rule present: '$FirewallRule' ($($rules.Count))" }
else {
    $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    if (@(Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue).Count) { Left "firewall rule still present: '$FirewallRule'" } else { Did "firewall rule removed: '$FirewallRule'" }
}

# The port-9332 reservation (installer.wxs ReservePort9332). Persistent, so it outlives the
# app unless given back.
$excl = (& netsh interface ipv4 show excludedportrange protocol=tcp store=persistent 2>$null) -join "`n"
if ($excl -notmatch "(?m)^\s*$ReservedPort\s+$ReservedPort\b") { Ok "port $ReservedPort reservation - absent" }
elseif ($Audit) { Note "port $ReservedPort reservation present" }
else {
    & netsh interface ipv4 delete excludedportrange protocol=tcp startport=$ReservedPort numberofports=1 store=persistent | Out-Null
    $excl = (& netsh interface ipv4 show excludedportrange protocol=tcp store=persistent 2>$null) -join "`n"
    if ($excl -match "(?m)^\s*$ReservedPort\s+$ReservedPort\b") { Left "port $ReservedPort reservation still present" } else { Did "port $ReservedPort reservation released" }
}

# Scratch files from photo enhancement and screenshots. Deleted after use in the normal
# case; a crash mid-way leaves one behind.
$tempDir = [IO.Path]::GetTempPath()
$scratch = @(foreach ($pat in $TempPatterns) { Get-ChildItem -Path $tempDir -Filter $pat -ErrorAction SilentlyContinue })
if ($scratch.Count -eq 0) { Ok 'temp scratch files - absent' }
elseif ($Audit) { Note "temp scratch files present: $($scratch.Count)" }
else { $scratch | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue; Did "temp scratch files removed: $($scratch.Count)" }

# ── 4. Your data ──────────────────────────────────────────────────────────────────────────
# Listings database, photos, generated photos, the Anthropic key, eBay tokens, saved
# Facebook/Terapeak browser sessions. Deleting it is the difference between "the program is
# gone" and "the machine holds nothing of mine", and only the seller can say which they want.
Say ''
Say '[4] Your data'
$dataDirs = @($DataDirUser, $DataDirSystem) | Where-Object { Test-Path -LiteralPath $_ }
if ($dataDirs.Count -eq 0) { Ok 'data folder - absent' }
else {
    foreach ($d in $dataDirs) {
        $m = Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum
        Note ("{0}  ({1} files, {2:n0} MB)" -f $d, $m.Count, ($m.Sum / 1MB))
    }
    Note 'holds: listings database, photos, API keys, eBay tokens, saved browser sessions'
    $wipe = $false
    if ($Audit)           { Note 'audit: not touching it' }
    elseif ($RemoveData)  { $wipe = $true }
    elseif ($KeepData)    { Note 'kept (-KeepData)' }
    else {
        $ans = Read-Host '  Delete it too? This cannot be undone. [y/N]'
        $wipe = ($ans -match '^[Yy]')
        if (-not $wipe) { Note 'kept' }
    }
    if ($wipe) { foreach ($d in $dataDirs) { Remove-Tree $d 'data folder' } }
}
if (Test-Path -LiteralPath $ExportsDir) {
    Note "exports you saved to the Desktop are yours and are never touched: $ExportsDir"
}

# ── 5. Prove it ───────────────────────────────────────────────────────────────────────────
Say ''
Say '[5] Verification'
$checks = @(
    @{ what = 'install folder';          gone = -not (Test-Path -LiteralPath $InstallDir) },
    @{ what = 'Start Menu folder';       gone = -not (Test-Path -LiteralPath $StartMenuDir) },
    @{ what = 'Add/Remove entry';        gone = (@(Get-ChildItem $uninstallRoots -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } | Where-Object { $_.DisplayName -eq $ProductName }).Count -eq 0) },
    @{ what = 'HKLM startup entry';      gone = -not (Get-ItemProperty -Path $RunKeyHKLM -Name $RunValueName -ErrorAction SilentlyContinue) },
    @{ what = 'HKCU startup entry';      gone = -not (Get-ItemProperty -Path $RunKeyHKCU -Name $RunValueName -ErrorAction SilentlyContinue) },
    @{ what = 'HKCU\SOFTWARE\INGMining'; gone = -not (Test-Path $ShortcutKey) },
    @{ what = 'firewall rule';           gone = (@(Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue).Count -eq 0) },
    @{ what = "port $ReservedPort reservation"; gone = (((& netsh interface ipv4 show excludedportrange protocol=tcp store=persistent 2>$null) -join "`n") -notmatch "(?m)^\s*$ReservedPort\s+$ReservedPort\b") },
    @{ what = 'running process';         gone = -not (Get-Process -Name 'AutoListerB1' -ErrorAction SilentlyContinue) }
)
$shortcutsLeft = @(foreach ($dir in @($PublicDesktop, $UserDesktop)) {
    if (Test-Path -LiteralPath $dir) {
        foreach ($lnk in Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -ErrorAction SilentlyContinue) {
            $t = ''; try { $t = $shell.CreateShortcut($lnk.FullName).TargetPath } catch { }
            if ($t -like '*AutoListerB1.exe' -or $lnk.BaseName -eq $ProductName) { $lnk.FullName }
        }
    }
})
$checks += @{ what = 'desktop shortcuts'; gone = ($shortcutsLeft.Count -eq 0) }
$dataGone = -not ((Test-Path -LiteralPath $DataDirUser) -or (Test-Path -LiteralPath $DataDirSystem))
$checks += @{ what = 'data folder'; gone = $dataGone; optional = (-not $wipe) }

$failed = 0
foreach ($c in $checks) {
    if ($c.gone) { Ok $c.what }
    elseif ($c.optional) { Note "$($c.what) - present, kept on purpose" }
    elseif ($Audit) { Note "$($c.what) - present" }
    else { Left $c.what; $failed++ }
}

Say ''
if ($Audit) {
    Say '  Audit only. Nothing was changed. Run without -Audit (elevated) to remove.'
    exit 0
}
if ($failed -eq 0 -and $script:left.Count -eq 0) {
    Say "  $ProductName is gone. Every location above was checked after removal."
    exit 0
}
Say "  NOT clean: $($script:left.Count) item(s) marked LEFT above. A restart usually frees what was held open; run this again after."
exit 1
