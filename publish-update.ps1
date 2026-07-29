# Ship an update: build it, package it, put it on the website, prove it landed.
#
# WHY THIS EXISTS
#   Shipping used to be four manual steps in three tools, so it drifted. On 2026-07-28 the
#   installer on inglisting.com was four days and 58 commits behind the code: everyone who
#   downloaded it got an app without any of that work in it, and nothing anywhere said so.
#   A release that takes one command does not drift.
#
# WHAT IT DOES, IN ORDER
#   1. Refuses to ship a red build            (dotnet test)
#   2. Publishes self-contained win-x64        (dotnet publish)
#   3. Builds the installer                    (build-installer.ps1 -> .msi)
#   4. Backs up the LIVE .msi on the server    (.bak-YYYY-MM-DD, never overwritten blind)
#   5. Uploads the new one over SFTP
#   6. VERIFIES the public URL now serves it   (size + Last-Modified must change)
#
# The verification is the point. Every earlier failure here was silent: the upload "worked"
# and the download button still served the old file. A ship that cannot prove itself is a
# ship that has not happened.
#
# USAGE
#   pwsh -File publish-update.ps1              # full release
#   pwsh -File publish-update.ps1 -SkipTests   # only when you already ran them
#   pwsh -File publish-update.ps1 -WhatIf      # build + package, upload nothing
#
# Run with pwsh (7+), not powershell.exe 5.1 - build-installer.ps1 documents why.

param(
    [switch]$SkipTests,
    [switch]$WhatIf,
    [string]$Version = "B1"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }
function Ok($text)        { Write-Host "    OK  $text" -ForegroundColor Green }
function Warn($text)      { Write-Host "    !   $text" -ForegroundColor Yellow }
function Die($text)       { Write-Host "`nFAILED: $text" -ForegroundColor Red; exit 1 }

$credPath = "$env:USERPROFILE\.claude\projects\C--Users-nsquires-source-repos-ING-eBay-AutoLister\web-credentials.json"
if (-not (Test-Path $credPath)) { Die "web-credentials.json not found at $credPath" }

# ---------------------------------------------------------------- 1. tests
if ($SkipTests) {
    Warn "Tests skipped by request."
} else {
    Step 1 "Running tests"
    $t = & dotnet test "$root\ING eBay AutoLister.Tests\ING eBay AutoLister.Tests.csproj" --nologo -v q 2>&1
    $line = $t | Select-String -Pattern 'Passed!|Failed!' | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($t | Select-Object -Last 12)
        Die "Tests are red. Shipping a red build is how a bad release reaches every downloader at once."
    }
    Ok $line
}

# ---------------------------------------------------------------- 2. publish
Step 2 "Publishing self-contained win-x64"
$dist = "$root\ING eBay AutoLister\dist"
# NOT PublishSingleFile: the bundled exe gets quarantined by endpoint AV as a false-positive
# packer. installer.wxs expects the folder-of-files layout. See build-installer.ps1.
& dotnet publish "$root\ING eBay AutoLister\ING eBay AutoLister.csproj" `
    -c Release -r win-x64 --self-contained true -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { Die "dotnet publish failed." }
Ok "published to dist"

# ---------------------------------------------------------------- 3. installer
Step 3 "Building the installer"
& pwsh -File "$root\build-installer.ps1" -Version $Version
if ($LASTEXITCODE -ne 0) { Die "build-installer.ps1 failed." }

$msi = Get-ChildItem "$root\installer-out" -Filter "*.msi" -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { Die "No .msi produced in installer-out." }
$msiMb = [math]::Round($msi.Length / 1MB, 2)
Ok "$($msi.Name)  $msiMb MB  built $($msi.LastWriteTime.ToString('HH:mm:ss'))"

# What the public URL serves right now, so we can prove it changed afterwards.
$publicUrl = "https://inglisting.com/ING-AutoLister-Setup.msi"
$ua = @{ "User-Agent" = "Mozilla/5.0" }
$beforeLen = ""; $beforeMod = ""
try {
    $h = Invoke-WebRequest -Uri $publicUrl -Method Head -TimeoutSec 60 -UseBasicParsing -Headers $ua
    $beforeLen = (@($h.Headers['Content-Length'])[0])
    $beforeMod = (@($h.Headers['Last-Modified'])[0])
    Ok "live now: $([math]::Round([int64]$beforeLen/1MB,2)) MB, $beforeMod"
} catch { Warn "No installer live yet (first release?)" }

if ($WhatIf) {
    Write-Host "`n-WhatIf: built and packaged, uploaded nothing." -ForegroundColor Yellow
    Write-Host "  would upload: $($msi.FullName)"
    exit 0
}

# ---------------------------------------------------------------- 4+5. upload
Step 4 "Backing up the live installer, then uploading"

$py = @'
import json, os, sys, datetime, paramiko

cred_path, local_msi = sys.argv[1], sys.argv[2]
c = json.load(open(cred_path))["ionos_sftp"]
docroot = c["docroots"]["inglisting.com"].rstrip("/")
remote = docroot + "/ING-AutoLister-Setup.msi"

t = paramiko.Transport((c["host"], int(c.get("port", 22))))
t.connect(username=c["user"], password=c["password"])
sftp = paramiko.SFTPClient.from_transport(t)

# Back up whatever is live before replacing it. An installer is the one artefact where
# "put the old one back" has to stay possible without a rebuild.
try:
    sftp.stat(remote)
    bak = remote + ".bak-" + datetime.date.today().isoformat()
    try: sftp.remove(bak)
    except IOError: pass
    sftp.rename(remote, bak)
    print("    OK  backed up live installer -> " + os.path.basename(bak))
except IOError:
    print("    !   nothing live to back up")

size = os.path.getsize(local_msi)
def progress(done, total):
    pct = int(done * 100 / total) if total else 0
    if pct % 20 == 0:
        sys.stdout.write("\r    uploading %d%%" % pct); sys.stdout.flush()

sftp.put(local_msi, remote, callback=progress)
print("\r    OK  uploaded %.2f MB" % (size / 1048576.0))

st = sftp.stat(remote)
print("    OK  server reports %.2f MB" % (st.st_size / 1048576.0))
if st.st_size != size:
    print("    FAIL size mismatch on server"); sys.exit(1)

sftp.close(); t.close()
'@

$pyFile = Join-Path $env:TEMP "ing-upload-msi.py"
Set-Content -Path $pyFile -Value $py -Encoding UTF8
& python $pyFile $credPath $msi.FullName
$uploadCode = $LASTEXITCODE
Remove-Item $pyFile -ErrorAction SilentlyContinue
if ($uploadCode -ne 0) { Die "Upload failed." }

# ---------------------------------------------------------------- 6. verify
Step 6 "Verifying the public download actually changed"
Start-Sleep -Seconds 3
try {
    $h2 = Invoke-WebRequest -Uri $publicUrl -Method Head -TimeoutSec 90 -UseBasicParsing -Headers $ua
    $afterLen = (@($h2.Headers['Content-Length'])[0])
    $afterMod = (@($h2.Headers['Last-Modified'])[0])
    Ok "HTTP $($h2.StatusCode)  $([math]::Round([int64]$afterLen/1MB,2)) MB  $afterMod"

    if ([int64]$afterLen -ne $msi.Length) {
        Die "The URL serves $afterLen bytes but the installer is $($msi.Length). Upload landed somewhere else, or a cache is in front of it."
    }
    if ($afterMod -eq $beforeMod -and $beforeMod -ne "") {
        Die "Last-Modified did not move. The download button is still serving the old file."
    }
    Ok "The download button now serves this build."
} catch {
    Die "Could not verify the public URL: $($_.Exception.Message)"
}

Write-Host "`nShipped." -ForegroundColor Green
Write-Host "  $publicUrl"
Write-Host "  $($msi.Name)  $msiMb MB"
Write-Host "`nNot done for you, on purpose:" -ForegroundColor DarkGray
Write-Host "  git push        - review the diff first" -ForegroundColor DarkGray
Write-Host "  release notes   - the What's New page is written by hand" -ForegroundColor DarkGray
