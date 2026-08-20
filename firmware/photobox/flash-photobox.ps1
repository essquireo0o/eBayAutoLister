# Flash the Photo Box firmware onto the Freenove ESP32-S3 CAM board.
#
#   pwsh -File flash-photobox.ps1            # auto-detects the port
#   pwsh -File flash-photobox.ps1 COM7       # or name it
#
# If the board doesn't show up as a COM port at all: use the USB-C socket on the
# board labelled UART/COM (not the one labelled USB), on a DATA cable — a
# charge-only cable enumerates as "Unknown USB Device". If it still refuses,
# hold the BOOT button while plugging it in.
param([string]$Port = "")

$ErrorActionPreference = "Stop"
$build = Join-Path $PSScriptRoot "build"
$app   = Join-Path $build "photobox.ino.bin"
$boot  = Join-Path $build "photobox.ino.bootloader.bin"
$parts = Join-Path $build "photobox.ino.partitions.bin"
if (-not (Test-Path $app)) { throw "No compiled firmware in $build - run the arduino-cli compile first (see the repo's Photo Box notes)." }

if (-not $Port) {
    $ports = [System.IO.Ports.SerialPort]::GetPortNames() | Where-Object { $_ -notin @('COM3','COM4') }
    if (-not $ports) { throw "No serial port found. Plug the board's UART/COM socket in with a data cable." }
    $Port = $ports | Select-Object -First 1
    Write-Host "Using $Port"
}

# boot_app0 ships with the esp32 core; find it wherever the core landed.
$app0 = Get-ChildItem "$env:LOCALAPPDATA\Arduino15\packages\esp32\hardware\esp32\*\tools\partitions\boot_app0.bin" |
        Select-Object -First 1
if (-not $app0) { throw "boot_app0.bin not found - is the esp32 arduino core installed?" }

python -m esptool --chip esp32s3 --port $Port --baud 460800 write-flash `
    0x0 $boot 0x8000 $parts 0xe000 $app0.FullName 0x10000 $app
if ($LASTEXITCODE -ne 0) { throw "esptool failed. Hold BOOT while plugging the board in, then retry." }
Write-Host "Flashed. Unplug/replug the board, then use the app's Photo Box Camera screen to send it your WiFi." -ForegroundColor Green
