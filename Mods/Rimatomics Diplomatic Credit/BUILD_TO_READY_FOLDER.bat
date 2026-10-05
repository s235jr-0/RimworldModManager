@echo off
setlocal EnableExtensions DisableDelayedExpansion
title Build Rimatomics Diplomatic Credit v0.9.0

set "GAME="

rem Try common install locations without storing any user-specific path.
if exist "C:\GOG Games\RimWorld\RimWorldWin64.exe" set "GAME=C:\GOG Games\RimWorld"
if not defined GAME if exist "C:\Program Files (x86)\GOG Galaxy\Games\RimWorld\RimWorldWin64.exe" set "GAME=C:\Program Files (x86)\GOG Galaxy\Games\RimWorld"
if not defined GAME if exist "C:\Program Files\RimWorld\RimWorldWin64.exe" set "GAME=C:\Program Files\RimWorld"

if not defined GAME (
    echo RimWorld was not found in a common location.
    echo.
    set /p "GAME=Enter your RimWorld game folder: "
)

set "ROOT=%~dp0"
set "BUILD=%ROOT%BuildFiles"
set "MANAGED=%GAME%\RimWorldWin64_Data\Managed"
set "NETSTANDARD=%MANAGED%\netstandard.dll"
set "SRC=%BUILD%\Source\RimatomicsDiplomaticCredit.cs"
set "FINDER=%BUILD%\Find_Harmony2.ps1"
set "TEMPLATE=%BUILD%\Template"
set "READYROOT=%ROOT%READY_TO_DROP"
set "OUTMOD=%READYROOT%\Rimatomics Diplomatic Credit"
set "OUTASM=%OUTMOD%\1.6\Assemblies"
set "OUTDLL=%OUTASM%\RimatomicsDiplomaticCredit.dll"

echo.
echo ============================================================
echo  Rimatomics Diplomatic Credit v0.9.0 - BUILD TO FOLDER
echo ============================================================
echo.
echo Nothing will be installed into RimWorld\Mods.
echo Finished mod will appear at:
echo   %OUTMOD%
echo.

if not exist "%MANAGED%\Assembly-CSharp.dll" (
    echo ERROR: RimWorld 1.6 was not found at:
    echo   %GAME%
    echo.
    echo Check the folder entered above and run the builder again.
    pause
    exit /b 1
)

if not exist "%NETSTANDARD%" (
    echo ERROR: RimWorld's netstandard.dll was not found:
    echo   %NETSTANDARD%
    echo.
    echo RimWorld 1.6 / Unity requires this reference for the Mod Settings UI.
    pause
    exit /b 1
)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo ERROR: Windows .NET Framework C# compiler was not found.
    pause
    exit /b 1
)

echo Locating Harmony 2.x...
echo.

set "HARMONY="
for /f "usebackq delims=" %%F in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%FINDER%" -ModsRoot "%GAME%\Mods"`) do (
    if not defined HARMONY set "HARMONY=%%F"
)

if not defined HARMONY (
    echo ERROR: Could not find a Harmony 2.x 0Harmony.dll.
    echo.
    echo v0.3 deliberately rejects Harmony 1.x copies bundled in old mod folders.
    echo Make sure the actual Harmony mod [Workshop 2009463077] is installed.
    pause
    exit /b 1
)

if not exist "%HARMONY%" (
    echo ERROR: Harmony finder returned a nonexistent path:
    echo   %HARMONY%
    pause
    exit /b 1
)

echo Using Harmony 2.x:
echo   %HARMONY%
echo.

if exist "%OUTMOD%" rmdir /s /q "%OUTMOD%"
mkdir "%OUTASM%" >nul 2>&1

echo Preparing READY_TO_DROP folder...
robocopy "%TEMPLATE%" "%OUTMOD%" /E /NFL /NDL /NJH /NJS /NP >nul
set "RBC=%ERRORLEVEL%"
if %RBC% GEQ 8 (
    echo ERROR: Failed to prepare output folder. Robocopy code %RBC%.
    pause
    exit /b %RBC%
)

echo Compiling DLL...
echo.

"%CSC%" /nologo /target:library /optimize+ ^
 /out:"%OUTDLL%" ^
 /reference:System.dll ^
 /reference:"%NETSTANDARD%" ^
 /reference:"%MANAGED%\Assembly-CSharp.dll" ^
 /reference:"%MANAGED%\UnityEngine.CoreModule.dll" ^
 /reference:"%HARMONY%" ^
 "%SRC%"

if errorlevel 1 (
    echo.
    echo ============================================================
    echo BUILD FAILED
    echo ============================================================
    echo.
    if exist "%OUTMOD%" rmdir /s /q "%OUTMOD%"
    echo The incomplete READY_TO_DROP folder was removed.
    echo.
    echo Fix the compiler errors shown above, then run this again.
    pause
    exit /b 1
)

if not exist "%OUTDLL%" (
    echo ERROR: Compiler returned success but DLL was not created.
    if exist "%OUTMOD%" rmdir /s /q "%OUTMOD%"
    pause
    exit /b 1
)

echo.
echo ============================================================
echo BUILD SUCCESS
echo ============================================================
echo.
echo Ready-to-drop mod:
echo   %OUTMOD%
echo.
echo Manually copy that ONE folder into:
echo   %GAME%\Mods
echo.
echo Nothing was installed automatically.
echo.

explorer.exe /select,"%OUTMOD%"
pause
