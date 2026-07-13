# Build installer for ING AutoLister
#
# PRIMARY:  If the WiX 4 CLI ("wix") is installed, builds a proper .msi
# FALLBACK: If WiX is not found, falls back to a ps2exe/batch self-extractor
#
# Prerequisites
#   1. Build the project first:
#        dotnet publish "ING eBay AutoLister\ING eBay AutoLister.csproj" `
#          -c Release -r win-x64 --self-contained true `
#          -p:PublishSingleFile=true `
#          -o "ING eBay AutoLister\dist"
#      Then rename the output to AutoListerB1.exe if necessary.
#   2. Install WiX 4 (for .msi):
#        dotnet tool install --global wix
#
# credentials.json (gitignored) must exist in the project folder.
# eBay app credentials are embedded so OAuth works out-of-the-box.
# Anthropic API key is NOT embedded — users enter their own at console.anthropic.com.

param(
    [string]$Version = "B1"
)

$ErrorActionPreference = "Stop"

$projectDir  = "$PSScriptRoot\ING eBay AutoLister"
$exeSource   = "$projectDir\dist\AutoListerB1.exe"
$credsSource = "$projectDir\credentials.json"
$wxsSource   = "$PSScriptRoot\installer.wxs"
$outDir      = "$PSScriptRoot\installer-out"
$distDir     = "$outDir\dist"

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
    AnthropicApiKey             = ""   # users enter their own at console.anthropic.com
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
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item $exeSource "$distDir\AutoListerB1.exe" -Force
$credsJson | Out-File -FilePath "$distDir\credentials.json" -Encoding UTF8
Write-Host "  dist folder prepared: $distDir"

# ── PRIMARY: WiX 4 MSI ────────────────────────────────────────────────────────
$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($wix) {
    Write-Host ""
    Write-Host "Building MSI with WiX 4..." -ForegroundColor Cyan
    $msiPath = "$outDir\ING-AutoLister-Setup-$Version.msi"

    $wixArgs = @(
        "build", $wxsSource,
        "-d", "SourceDir=$distDir\",
        "-o", $msiPath
    )
    & wix @wixArgs
    if ($LASTEXITCODE -eq 0) {
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

# Add inglistingengine.com → 127.0.0.1 to the Windows hosts file
`$hostsFile = "`$env:SystemRoot\System32\drivers\etc\hosts"
`$hostsEntry = "127.0.0.1  inglistingengine.com"
`$alreadySet = (Get-Content `$hostsFile -ErrorAction SilentlyContinue) | Select-String "inglistingengine\.com" -Quiet
if (-not `$alreadySet) {
    try {
        # Try direct write (works when running as admin)
        Add-Content `$hostsFile "`n`$hostsEntry" -Encoding ASCII -ErrorAction Stop
        Write-Host "  Local DNS added: inglistingengine.com -> 127.0.0.1" -ForegroundColor Green
        Write-Host "  You can access the app at: http://inglistingengine.com:9330" -ForegroundColor Green
    } catch {
        # Not admin - re-launch the elevated helper
        Write-Host "  Adding local DNS (UAC prompt may appear)..." -ForegroundColor Yellow
        `$cmd = "Add-Content '`$hostsFile' '`$hostsEntry' -Encoding ASCII"
        Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -Command `"`$cmd`"" -Verb RunAs -Wait -ErrorAction SilentlyContinue
        Write-Host "  Local DNS added (or was already present)." -ForegroundColor Green
    }
} else {
    Write-Host "  Local DNS already configured." -ForegroundColor Green
}

Write-Host ""
Write-Host "  Launching ING AutoLister..." -ForegroundColor Cyan
Start-Process `$exePath
Start-Sleep -Seconds 3
Start-Process "http://localhost:9330"
Write-Host ""
Write-Host "  Done! The app is running at http://localhost:9330" -ForegroundColor Green
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
