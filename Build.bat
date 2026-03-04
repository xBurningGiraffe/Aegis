@echo off
:: =============================================================================
:: Aegis - Build Script
:: =============================================================================
:: Prerequisites: .NET 10 SDK  (included with Visual Studio 2026)
:: Run this file from the Aegis project directory.
::
:: Output: dist\Aegis.exe  (~160 KB, requires .NET 10 runtime installed by Visual Studio 2026)
:: =============================================================================

setlocal

set PROJECT=%~dp0Aegis.csproj
set OUTDIR=%~dp0dist

echo.
echo  Building Aegis...
echo  Project : %PROJECT%
echo  Output  : %OUTDIR%\Aegis.exe
echo.

dotnet publish "%PROJECT%" ^
    -c Release ^
    -o "%OUTDIR%"

if %ERRORLEVEL% neq 0 (
    echo.
    echo  BUILD FAILED. Make sure the .NET 10 SDK is installed (included with Visual Studio 2026).
    pause
    exit /b 1
)

echo.
echo  Build succeeded!
echo  Binary: %OUTDIR%\Aegis.exe
echo.
echo  To run: right-click Aegis.exe ^> Run as administrator
echo          (required for wbAdmin and diskpart backup operations)
echo.
pause
endlocal
