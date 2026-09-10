@echo off
:: ING AutoLister Uninstaller
:: Asks for administrator rights, then runs Uninstall-INGAutoLister.ps1 next to this file,
:: which removes the program AND every leftover it can find, then verifies each location.
::
::   Uninstall-INGAutoLister.bat              remove, asking about your data folder
::   Uninstall-INGAutoLister.bat -RemoveData  remove everything including your data, no questions
::   Uninstall-INGAutoLister.bat -KeepData    remove the program, keep your data, no questions
::   Uninstall-INGAutoLister.bat -Audit       only report what is on this machine (no admin needed)

setlocal
set "SCRIPT=%~dp0Uninstall-INGAutoLister.ps1"
if not exist "%SCRIPT%" (
    echo Uninstall-INGAutoLister.ps1 is missing. Keep it next to this file.
    pause
    exit /b 1
)

echo %* | find /i "-Audit" >nul
if %errorLevel% EQU 0 goto run

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo Requesting administrator access...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    exit /b
)

:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%errorLevel%"
echo.
pause
exit /b %RC%
