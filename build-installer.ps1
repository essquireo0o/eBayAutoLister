# Build installer for ING AutoLister
#
# PRIMARY:  If the WiX 4 CLI ("wix") is installed, builds a proper .msi
# FALLBACK: If WiX is not found, falls back to a ps2exe/batch self-extractor
#
# Run this with pwsh (PowerShell 7+), not legacy powershell.exe (5.1) — 5.1's native
# command argument passing mangles a trailing backslash immediately before a closing
# quote (e.g. `-d "SourceDir=C:\...\dist\"`), which breaks the wix invocation with a
# confusing "Cannot find the input file 'eBay'" error. pwsh doesn't have this bug.
#
# Prerequisites
#   1. Build the project first:
#        dotnet publish "ING eBay AutoLister\ING eBay AutoLister.csproj" `
#          -c Release -r win-x64 --self-contained true `
#          -o "ING eBay AutoLister\dist"
#      NOTE: do NOT add -p:PublishSingleFile=true — the bundled single-file exe gets
#      quarantined by endpoint AV (Bitdefender/Sophos) as a false-positive packer/dropper
#      on this build machine. The folder-of-files output below is what installer.wxs expects.
#   2. Install WiX 4 (for .msi):
#        dotnet tool install --global wix
#   3. Configure a trusted signing identity as documented in CODE_SIGNING.md.
#      Public releases fail closed when neither Artifact Signing nor a certificate-store
#      thumbprint is configured. Use -AllowUnsigned only for local packaging diagnostics.
#
# credentials.json (gitignored) must exist in the project folder.
# eBay app credentials are embedded so OAuth works out-of-the-box.
# Anthropic API key is NOT embedded — users enter their own at console.anthropic.com.

param(
    [string]$Version = "B1",
    [string]$SigningThumbprint = $env:ING_CODESIGN_THUMBPRINT,
    [string]$ArtifactSigningDlib = $env:ING_ARTIFACT_SIGNING_DLIB,
    [string]$ArtifactSigningMetadata = $env:ING_ARTIFACT_SIGNING_METADATA,
    [string]$TimestampUrl = $(if ($env:ING_CODESIGN_TIMESTAMP_URL) { $env:ING_CODESIGN_TIMESTAMP_URL } else { "http://timestamp.acs.microsoft.com/" }),
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

$projectDir  = "$PSScriptRoot\ING eBay AutoLister"
$publishDir  = "$projectDir\dist"
$exeSource   = "$publishDir\AutoListerB1.exe"
$credsSource = "$projectDir\credentials.json"
$wxsSource   = "$PSScriptRoot\installer.wxs"
$outDir      = "$PSScriptRoot\installer-out"
$distDir     = "$outDir\dist"

# Windows treats every new unsigned build as an unknown publisher, and Smart App Control can block
# it outright. Signing is therefore a release requirement, not an optional finishing step. Two
# identities are supported: Microsoft's Artifact Signing dlib + metadata file, or an Authenticode
# code-signing certificate already held in the Windows certificate store. -AllowUnsigned exists
# only for local packaging diagnostics; publish-update.ps1 never uses it.
function Find-SignTool {
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe `
                -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "SignTool was not found. Install the Windows SDK signing tools." }
    return $tool.FullName
}

function Signing-Mode {
    if ($ArtifactSigningDlib -or $ArtifactSigningMetadata) {
        if (-not $ArtifactSigningDlib -or -not $ArtifactSigningMetadata) {
            throw "Artifact Signing requires both ING_ARTIFACT_SIGNING_DLIB and ING_ARTIFACT_SIGNING_METADATA."
        }
        if (-not (Test-Path -LiteralPath $ArtifactSigningDlib)) { throw "Artifact Signing dlib not found: $ArtifactSigningDlib" }
        if (-not (Test-Path -LiteralPath $ArtifactSigningMetadata)) { throw "Artifact Signing metadata not found: $ArtifactSigningMetadata" }
        return "artifact"
    }
    if ($SigningThumbprint) { return "store" }
    if ($AllowUnsigned) { return "unsigned" }
    throw @"
Release signing is not configured. An unsigned MSI displays Unknown publisher and cannot carry
publisher reputation between releases. Configure Microsoft Artifact Signing with
ING_ARTIFACT_SIGNING_DLIB + ING_ARTIFACT_SIGNING_METADATA, or set ING_CODESIGN_THUMBPRINT to a
trusted Authenticode certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My.
Use -AllowUnsigned only for a local packaging test; publish-update.ps1 intentionally fails closed.
"@
}

$signingMode = Signing-Mode
$signTool = if ($signingMode -eq "unsigned") { $null } else { Find-SignTool }

function Sign-ReleaseFile([string]$Path) {
    if ($signingMode -eq "unsigned") {
        Write-Warning "UNSIGNED local diagnostic build: $Path"
        return
    }

    $args = @("sign", "/v", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256",
              "/d", "ING Listing Engine", "/du", "https://inglisting.com")
    if ($signingMode -eq "artifact") {
        $args += @("/dlib", $ArtifactSigningDlib, "/dmdf", $ArtifactSigningMetadata)
    } else {
        $thumb = ($SigningThumbprint -replace '\s', '').ToUpperInvariant()
        $userCert = Get-ChildItem "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
        $machineCert = Get-ChildItem "Cert:\LocalMachine\My\$thumb" -ErrorAction SilentlyContinue
        $cert = if ($userCert) { $userCert } else { $machineCert }
        if (-not $cert -or -not $cert.HasPrivateKey) {
            throw "Code-signing certificate $thumb with a private key was not found in the CurrentUser or LocalMachine My store."
        }
        if ($cert.EnhancedKeyUsageList.ObjectId.Value -notcontains "1.3.6.1.5.5.7.3.3") {
            throw "Certificate $thumb is not valid for Code Signing."
        }
        if ($machineCert) { $args += "/sm" }
        $args += @("/sha1", $thumb)
    }

    Write-Host "Signing $([IO.Path]::GetFileName($Path)) ($signingMode)..." -ForegroundColor Cyan
    & $signTool @args $Path
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed for $Path (exit $LASTEXITCODE)." }

    & $signTool verify /pa /all /v $Path
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $Path (exit $LASTEXITCODE)." }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "Signature on $Path is $($signature.Status), not Valid." }
    if (-not $signature.TimeStamperCertificate) { throw "Signature on $Path has no trusted timestamp." }
    Write-Host "  Verified publisher: $($signature.SignerCertificate.Subject)" -ForegroundColor Green
}

if (-not (Test-Path $exeSource)) {
    Write-Error "AutoListerB1.exe not found at: $exeSource`nBuild the project first with dotnet publish."
    exit 1
}

# ── Prepare distribution credentials ──────────────────────────────────────────
if (-not (Test-Path $credsSource)) {
    Write-Error "credentials.json not found at $credsSource"
    exit 1
}
Write-Host "Reading credentials.json..."
$credsRaw = Get-Content $credsSource -Raw | ConvertFrom-Json

# Helper: null-coalesce compatible with Windows PowerShell 5.1
function Coalesce($a, $b) { if ($null -ne $a -and $a -ne '') { $a } else { $b } }

$distCreds = [ordered]@{
    AnthropicApiKey             = ""   # NOT embedded — each customer enters their own Claude key at console.anthropic.com
    OpenAiApiKey                = ""   # optional — only needed for DALL-E image gen
    ImageGenMode                = Coalesce $credsRaw.ImageGenMode       "disabled"
    LocalSdEndpoint             = Coalesce $credsRaw.LocalSdEndpoint    "http://127.0.0.1:7860"
    LocalSdBackend              = Coalesce $credsRaw.LocalSdBackend     "automatic1111"
    LocalSdModelName            = Coalesce $credsRaw.LocalSdModelName   ""
    ImagePromptTemplate         = Coalesce $credsRaw.ImagePromptTemplate ""
    EbayClientId                = Coalesce $credsRaw.EbayClientId       ""
    EbayDevId                   = Coalesce $credsRaw.EbayDevId          ""
    EbayClientSecret            = Coalesce $credsRaw.EbayClientSecret   ""
    EbayRuName                  = Coalesce $credsRaw.EbayRuName         ""
    EbaySandbox                 = $false
    EbayFulfillmentPolicyId     = ""
    EbayPaymentPolicyId         = ""
    EbayReturnPolicyId          = ""
    EbayUserToken               = ""
    EbayRefreshToken            = ""
    EbayTokenExpiresAt          = $null
    EbayRefreshTokenExpiresAt   = $null
    EbayTokenType               = ""
    DefaultPostalCode           = Coalesce $credsRaw.DefaultPostalCode  ""
    DefaultCountry              = Coalesce $credsRaw.DefaultCountry     "US"
    DefaultPackageType          = Coalesce $credsRaw.DefaultPackageType "PACKAGE_THICK_ENVELOPE"
    DefaultHandlingTimeDays     = if ($credsRaw.DefaultHandlingTimeDays) { $credsRaw.DefaultHandlingTimeDays } else { 1 }
    DefaultWeightLbs            = if ($null -ne $credsRaw.DefaultWeightLbs) { $credsRaw.DefaultWeightLbs } else { 0 }
    DefaultWeightOz             = if ($null -ne $credsRaw.DefaultWeightOz)  { $credsRaw.DefaultWeightOz  } else { 0 }
    DefaultLengthIn             = if ($null -ne $credsRaw.DefaultLengthIn)  { $credsRaw.DefaultLengthIn  } else { 0 }
    DefaultWidthIn              = if ($null -ne $credsRaw.DefaultWidthIn)   { $credsRaw.DefaultWidthIn   } else { 0 }
    DefaultHeightIn             = if ($null -ne $credsRaw.DefaultHeightIn)  { $credsRaw.DefaultHeightIn  } else { 0 }
    DefaultFulfillmentPolicyId  = ""
    DefaultBestOffer            = $false
    LicenseKey                  = "ING-BETA-2025"
    InstallDate                 = $null
    StripePublishableKey        = Coalesce $credsRaw.StripePublishableKey ""
    AdminKey                    = ""
    StripeSecretKey             = ""
    StripeWebhookSecret         = ""
}
$credsJson = $distCreds | ConvertTo-Json -Depth 3
Write-Host "  Claude API: (not embedded - users enter their own)"
Write-Host "  eBay App:   $(if ($credsRaw.EbayClientId)    { 'configured' } else { 'MISSING - add EbayClientId to credentials.json' })"

# ── Prepare dist folder (used by both WiX and the PS1 installer) ──────────────
# Copy the whole publish output (390-ish framework/dependency files, not just the exe) since
# installer.wxs harvests everything in SourceDir now that PublishSingleFile is off.
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item "$publishDir\*" $distDir -Recurse -Force
$credsJson | Out-File -FilePath "$distDir\credentials.json" -Encoding UTF8 -Force
Write-Host "  dist folder prepared: $distDir ($((Get-ChildItem $distDir -Recurse -File).Count) files)"

# Sign the two ING-owned PE files before WiX hashes and packages them. Framework and third-party
# files retain their vendors' signatures; modifying those would invalidate their provenance.
Sign-ReleaseFile "$distDir\AutoListerB1.exe"
Sign-ReleaseFile "$distDir\AutoListerB1.dll"

# ── PRIMARY: WiX 4 MSI ────────────────────────────────────────────────────────
$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($wix) {
    Write-Host ""
    Write-Host "Building MSI with WiX 4..." -ForegroundColor Cyan
    $msiPath = "$outDir\ING-AutoLister-Setup-$Version.msi"
    if (Test-Path -LiteralPath $msiPath) { Remove-Item -LiteralPath $msiPath -Force }

    # Note: `& wix @wixArgs` (array splatting) mis-tokenizes paths containing spaces on this
    # toolchain — "ING eBay AutoLister\installer.wxs" gets split at the space, and wix reports
    # "Cannot find the input file 'eBay'". Calling wix with each argument written as its own
    # literal quoted token (not built from an array or a re-parsed string) avoids the bug.
    #
    # -arch x64 is required even though installer.wxs references ProgramFiles64Folder: without
    # it, wix emits a 32-bit package, and Windows Installer silently redirects ProgramFiles64Folder
    # to "Program Files (x86)" and HKLM\...\Run to HKLM\...\WOW6432Node\...\Run instead of failing
    # loudly, which is how a x64 self-contained build ended up installed as if it were x86.
    # The trailing backslash before the closing quote (`$distDir\"`) is read by the native
    # command-line parser as an escaped quote, which swallows the closing quote and lets the
    # space in "ING eBay AutoLister" split the argument (wix then reports "Cannot find the
    # input file 'eBay'"). Doubling it (`\\"`) escapes to a single literal backslash and closes
    # the quote correctly, so WiX still gets a SourceDir/RepoDir ending in one backslash.
    & wix build "$wxsSource" -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Firewall.wixext -d "SourceDir=$distDir\\" -d "RepoDir=$PSScriptRoot\\" -o "$msiPath"
    if ($LASTEXITCODE -eq 0) {
        Sign-ReleaseFile $msiPath
        Write-Host ""
        Write-Host "MSI created: $msiPath" -ForegroundColor Green

        Write-Host "To install:  msiexec /i `"$msiPath`"" -ForegroundColor Green

        # ── Copy to Desktop\eBay Autolister MSI folder ───────────────────────
        $desktopMsiDir = "$env:USERPROFILE\Desktop\eBay Autolister MSI"
        New-Item -ItemType Directory -Force -Path $desktopMsiDir | Out-Null
        $destMsi = "$desktopMsiDir\ING-AutoLister-Setup-$Version.msi"
        Copy-Item $msiPath $destMsi -Force
        Write-Host "Copied to Desktop: $destMsi" -ForegroundColor Green

        # ── Copy uninstaller to same folder ──────────────────────────────────
        $uninstallSrc = "$PSScriptRoot\Uninstall-INGAutoLister.bat"
        if (Test-Path $uninstallSrc) {
            Copy-Item $uninstallSrc "$desktopMsiDir\Uninstall-INGAutoLister.bat" -Force
            Write-Host "Uninstaller: $desktopMsiDir\Uninstall-INGAutoLister.bat" -ForegroundColor Green
        }

        exit 0
    } else {
        Write-Warning "WiX build failed (exit $LASTEXITCODE) - falling back to exe/bat installer."
        Write-Warning "Fix the error above, or install WiX with: dotnet tool install --global wix"
    }
} else {
    Write-Host ""
    Write-Host "WiX 4 not found. Install it for .msi support:" -ForegroundColor Yellow
    Write-Host "  dotnet tool install --global wix" -ForegroundColor Yellow
    Write-Host "Falling back to exe/bat installer..." -ForegroundColor Yellow
}

# ── FALLBACK: PowerShell self-extractor (ps2exe / bat) ───────────────────────
Write-Host ""
Write-Host "Building PowerShell self-extractor..." -ForegroundColor Cyan

$exeBytes = [System.IO.File]::ReadAllBytes($exeSource)
$b64      = [Convert]::ToBase64String($exeBytes)
$credsB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($credsJson))

$script = @"
`$ErrorActionPreference = 'Stop'
`$installDir = "`$env:LOCALAPPDATA\ING AutoLister"
`$exePath    = "`$installDir\AutoListerB1.exe"
`$credsPath  = "`$installDir\credentials.json"

Write-Host ""
Write-Host "  ING Listing Engine(tm) Setup" -ForegroundColor Cyan
Write-Host "  by ING Mining LLC" -ForegroundColor Cyan
Write-Host ""

# Extract exe
New-Item -ItemType Directory -Force -Path `$installDir | Out-Null
Write-Host "  Installing to `$installDir ..."
`$b64 = '$b64'
[System.IO.File]::WriteAllBytes(`$exePath, [Convert]::FromBase64String(`$b64))

# Drop pre-configured credentials only if the user doesn't already have their own
if (-not (Test-Path `$credsPath)) {
    Write-Host "  Writing pre-configured API credentials..."
    `$credsB64 = '$credsB64'
    `$credsJson = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(`$credsB64))
    [System.IO.File]::WriteAllText(`$credsPath, `$credsJson)
}

`$ws = New-Object -ComObject WScript.Shell

# Desktop shortcut
`$lnk = `$ws.CreateShortcut("`$env:USERPROFILE\Desktop\ING AutoLister.lnk")
`$lnk.TargetPath       = `$exePath
`$lnk.WorkingDirectory = `$installDir
`$lnk.Description      = "ING Listing Engine by ING Mining LLC"
`$lnk.Save()

# Start Menu shortcut
`$smDir = "`$env:APPDATA\Microsoft\Windows\Start Menu\Programs\ING Mining"
New-Item -ItemType Directory -Force -Path `$smDir | Out-Null
`$lnk2 = `$ws.CreateShortcut("`$smDir\ING AutoLister.lnk")
`$lnk2.TargetPath       = `$exePath
`$lnk2.WorkingDirectory = `$installDir
`$lnk2.Description      = "ING Listing Engine by ING Mining LLC"
`$lnk2.Save()

# Startup folder shortcut (auto-start on login)
`$startupDir = "`$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
`$lnk3 = `$ws.CreateShortcut("`$startupDir\ING AutoLister.lnk")
`$lnk3.TargetPath       = `$exePath
`$lnk3.WorkingDirectory = `$installDir
`$lnk3.Description      = "ING Listing Engine by ING Mining LLC"
`$lnk3.Save()

Write-Host "  Shortcuts created (Desktop, Start Menu, Startup)." -ForegroundColor Green

# No hosts-file / local-DNS write: modifying the Windows hosts file is a classic
# malware pattern (hosts hijacking) that antivirus/EDR flags, and it forced a UAC
# prompt for a cosmetic hostname. The app is reached at http://localhost:9332.

Write-Host ""
Write-Host "  Launching ING AutoLister..." -ForegroundColor Cyan
Start-Process `$exePath
Start-Sleep -Seconds 3
Start-Process "http://localhost:9332"
Write-Host ""
Write-Host "  Done! The app is running at http://localhost:9332" -ForegroundColor Green
Write-Host "  It lives in the system tray - right-click the tray icon to open or quit." -ForegroundColor Green
Write-Host ""
"@

$ps1Path = "$outDir\setup-temp.ps1"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$script | Out-File -FilePath $ps1Path -Encoding UTF8

$installer = "$outDir\ING-AutoLister-Setup-$Version.exe"
$ps2exe = Get-Command ps2exe -ErrorAction SilentlyContinue
if ($ps2exe) {
    Write-Host "Compiling with ps2exe..."
    ps2exe -inputFile $ps1Path -outputFile $installer `
           -title "ING AutoLister Setup" -version "1.0.0.$Version" -noConsole:$false
    Remove-Item $ps1Path -Force
    Sign-ReleaseFile $installer
    Write-Host "Installer created: $installer" -ForegroundColor Green
} else {
    $batPath = "$outDir\ING-AutoLister-Setup-$Version.bat"
    @"
@echo off
echo.
echo  ING Listing Engine Setup
echo  Please wait...
echo.
powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0setup-temp.ps1"
pause
"@ | Out-File -FilePath $batPath -Encoding ASCII
    Write-Host ""
    Write-Host "ps2exe not found - created batch installer: $batPath" -ForegroundColor Yellow
    Write-Host "For a proper .exe: Install-Module ps2exe -Scope CurrentUser" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Output folder: $outDir" -ForegroundColor Green
}
