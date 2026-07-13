@echo off
:: ING AutoLister Uninstaller
:: Self-elevates to admin, then removes all installed versions by product name.

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo Requesting administrator access...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo.
echo  ING AutoLister Uninstaller
echo  ==========================
echo.

powershell -ExecutionPolicy Bypass -NonInteractive -Command ^
  "$products = Get-WmiObject Win32_Product | Where-Object { $_.Name -eq 'ING AutoLister' };" ^
  "if (-not $products) { Write-Host '  ING AutoLister is not installed.' -ForegroundColor Yellow; Read-Host '  Press Enter to close'; exit }" ^
  "Write-Host \"  Found $($products.Count) installation(s). Uninstalling...\" -ForegroundColor Cyan;" ^
  "foreach ($p in $products) {" ^
  "  Write-Host \"  Removing: $($p.Name) $($p.Version) [$($p.IdentifyingNumber)]\";" ^
  "  Start-Process msiexec.exe -ArgumentList \"/x $($p.IdentifyingNumber) /qb\" -Wait" ^
  "};" ^
  "Write-Host '';" ^
  "Write-Host '  Uninstall complete.' -ForegroundColor Green;" ^
  "Read-Host '  Press Enter to close'"
