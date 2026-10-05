@echo off
setlocal EnableExtensions
title Rimatomics Fuel Pool Bill Counter - Quick Check

set "RW=%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
set "LOG=%RW%\Player.log"

echo ============================================================
echo  Rimatomics Fuel Pool Bill Counter - QUICK CHECK
echo ============================================================
echo.
echo This does not modify anything.
echo.

if not exist "%LOG%" (
    echo Player.log was not found at the normal RimWorld location.
    echo.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$hits = Select-String -LiteralPath '%LOG%' -Pattern 'Rimatomics Fuel Pool Bill Counter' -SimpleMatch -ErrorAction SilentlyContinue; " ^
  "if($hits){ $hits | Select-Object -Last 30 | ForEach-Object { $_.Line } } else { Write-Host 'No log lines from the mod were found in the current Player.log.' -ForegroundColor Yellow }"

echo.
echo Expected startup line:
echo   [Rimatomics Fuel Pool Bill Counter] Loaded...
echo.
echo If vanilla missed pooled rods, you should also see something like:
echo   MakeFuelRods: corrected product count 2 -^> 12 ...
echo.
pause
