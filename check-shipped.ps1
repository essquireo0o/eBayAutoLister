# Is what we committed actually the thing people can download? Read-only. Answers in ~15 seconds.
#
# WHY THIS EXISTS
#   publish-update.ps1 proves a release landed, but it only runs when someone has already decided
#   to ship. Nothing answered the question that goes unasked: "the version in git is 2.6.3 - what
#   is the world serving?" On 2026-08-27 the answer was 2.6.2 on the site, 2.6.1 for nine days
#   before that, and in both cases every session believed it had shipped. The failure was never a
#   bad upload. It was that being out of date is silent, and staying silent is free.
#
#   So this makes it cheap to ask. It changes nothing, needs no credentials, and is safe to run
#   any time - before saying "shipped", after a version bump, or when the owner says the download
#   is old and you want the answer before you start rebuilding things.
#
# WHAT IT CHECKS
#   1. csproj <Version> and installer.wxs Version agree      (bumping one is a documented trap)
#   2. what inglisting.com actually serves                   (ProductVersion + sha256 of real bytes)
#   3. what GitHub releases/latest offers                    (the in-app updater reads this)
#   4. that 2 and 3 are the SAME BINARY                      (identical byte counts have hidden
#                                                             two different builds before)
#
# EXIT CODE
#   0 = HEAD is live everywhere. 1 = something is behind or inconsistent; the text says which.
#   Non-zero is not "broken" - work in progress is legitimately unshipped. It means: do not claim
#   this is live without saying what is.
#
#   pwsh -File check-shipped.ps1

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ua = @{ "User-Agent" = "Mozilla/5.0" }
$publicUrl = "https://inglisting.com/ING-AutoLister-Setup.msi"
$problems = @()

function Ok($t)   { Write-Host "  OK    $t" -ForegroundColor Green }
function Bad($t)  { Write-Host "  BEHIND $t" -ForegroundColor Yellow; $script:problems += $t }
function Info($t) { Write-Host "  ...   $t" -ForegroundColor DarkGray }

# Reading ProductVersion out of an .msi needs the Windows Installer COM object; there is no header
# or filename that can be trusted for it. Two builds of one commit differ in bytes but not version,
# which is why every caller here wants the sha too.
function Get-MsiProductVersion([string]$Path) {
    $wi = New-Object -ComObject WindowsInstaller.Installer
    $db = $wi.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $wi, @($Path, 0))
    $view = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db,
             @("SELECT Value FROM Property WHERE Property='ProductVersion'"))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
    $rec = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
    if (-not $rec) { return $null }
    $rec.GetType().InvokeMember("StringData", "GetProperty", $null, $rec, 1)
}

# 2.6.3 and 2.6.3.0 are the same release said two ways; compare on the first three parts only.
function Normalize([string]$v) {
    if (-not $v) { return "" }
    $p = ($v.Trim() -split '\.')
    (@($p[0], $p[1], $p[2]) | ForEach-Object { if ($_) { $_ } else { "0" } }) -join '.'
}

Write-Host "`nWhat is in git" -ForegroundColor Cyan

$csprojPath = Join-Path $root "ING eBay AutoLister\ING eBay AutoLister.csproj"
$wxsPath    = Join-Path $root "installer.wxs"
$csprojVer  = ([regex]::Match((Get-Content $csprojPath -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
$wxsVer     = ([regex]::Match((Get-Content $wxsPath -Raw), 'Version="(\d+\.\d+\.\d+\.\d+)"')).Groups[1].Value

# The version lives in two files and bumping only one produces a release that lies about itself:
# the app reports the csproj number, Windows upgrade logic uses the wxs number.
if ((Normalize $csprojVer) -ne (Normalize $wxsVer)) {
    Bad "csproj says $csprojVer but installer.wxs says $wxsVer - bump BOTH in one commit."
} else {
    Ok "csproj and installer.wxs agree: $csprojVer"
}
$headVer = Normalize $csprojVer

Write-Host "`nWhat the website serves" -ForegroundColor Cyan
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("shipped-" + [guid]::NewGuid().ToString("N") + ".msi")
$siteVer = $null; $siteSha = $null
try {
    Info "downloading $publicUrl"
    # Downloaded in full rather than HEADed on purpose: this host has answered 200 with an HTML
    # error body, and dl.php omits Content-Length, so headers cannot settle either question.
    Invoke-WebRequest -Uri $publicUrl -OutFile $tmp -UseBasicParsing -Headers $ua -TimeoutSec 600
    $siteVer = Normalize (Get-MsiProductVersion $tmp)
    $siteSha = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($siteVer -eq $headVer) { Ok "$publicUrl serves $siteVer  (sha $($siteSha.Substring(0,12))...)" }
    else { Bad "site serves $siteVer, git is at $headVer - the download is behind." }
} catch {
    Bad "could not read the public download: $($_.Exception.Message)"
} finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}

Write-Host "`nWhat the in-app updater sees" -ForegroundColor Cyan
$relSha = $null
try {
    $rel = & gh release view --json tagName,assets 2>$null | ConvertFrom-Json
    if (-not $rel) { throw "gh returned nothing (not installed, not authenticated, or no releases)" }
    $relVer = Normalize ($rel.tagName -replace '^v', '')
    $asset  = $rel.assets | Where-Object { $_.name -eq "ING-AutoLister-Setup.msi" } | Select-Object -First 1
    if ($asset.digest) { $relSha = ($asset.digest -replace '^sha256:', '').ToUpperInvariant() }

    if (-not $asset) {
        Bad "releases/latest ($($rel.tagName)) has no asset named ING-AutoLister-Setup.msi - the download URL matches the FILENAME, so it is broken."
    } elseif ($relVer -eq $headVer) {
        Ok "releases/latest is $($rel.tagName)"
    } else {
        # This is the one that tells an installed user "you are up to date" while the site has newer.
        Bad "releases/latest is $($rel.tagName), git is at $headVer - installed apps will not be told an update exists."
    }
} catch {
    Bad "could not read the GitHub release: $($_.Exception.Message)"
}

Write-Host "`nAre the website and the release the same binary" -ForegroundColor Cyan
if ($siteSha -and $relSha) {
    if ($siteSha -eq $relSha) {
        Ok "identical bytes (sha $($siteSha.Substring(0,12))...)"
    } else {
        # Seen for real on 2026-08-27: both "2.6.2", both 54,776,252 bytes, different builds. MSI
        # packaging is not reproducible, so building twice from one commit yields two files.
        # Build once, upload that same file to both places.
        Bad "site and release are DIFFERENT BUILDS. site $($siteSha.Substring(0,12))... vs release $($relSha.Substring(0,12))... Build the MSI once and push that same file to both."
    }
} else {
    Info "skipped - need both a site download and a release asset digest"
}

Write-Host ""
if ($problems.Count -eq 0) {
    Write-Host "HEAD ($csprojVer) is what people can download." -ForegroundColor Green
    exit 0
}
Write-Host "$($problems.Count) thing(s) not live:" -ForegroundColor Yellow
$problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
Write-Host "`nTo ship:  pwsh -File publish-update.ps1        (add -AllowUnsigned while signing is blocked)" -ForegroundColor DarkGray
exit 1
