@echo off
setlocal
cd /d "%~dp0"
title Build RimWorld Mod Manager v3

echo ===============================================================
echo  RimWorld Mod Manager v3 - build for Windows
echo ===============================================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: the .NET SDK was not found.
    echo Install it with:  winget install Microsoft.DotNet.SDK.10
    pause
    exit /b 1
)

echo Running tests...
dotnet test
if errorlevel 1 (
    echo.
    echo TESTS FAILED - nothing was built.
    pause
    exit /b 1
)

echo.
echo Building a single RimModManager.exe (includes .NET, no install needed)...
dotnet publish "src\RimModManager.App" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none ^
  -o "publish\win-x64"
if errorlevel 1 (
    echo.
    echo BUILD FAILED.
    pause
    exit /b 1
)

rem Debug symbols bundled by the graphics library; not needed to run.
del /q "publish\win-x64\*.pdb" >nul 2>&1

echo.
echo ===============================================================
echo  DONE:  publish\win-x64\RimModManager.exe
echo ===============================================================
echo.
explorer.exe /select,"%~dp0publish\win-x64\RimModManager.exe"
pause
