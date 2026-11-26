@echo off
setlocal enabledelayedexpansion

rem ================================================================
rem MDK² unified build for Space Engineers Programmable Block scripts
rem ================================================================
rem Usage: double-click build.bat or run from cmd / powershell
rem Prerequisites: .NET 8 SDK, Space Engineers installed (for game DLL refs)
rem ================================================================

set ROOT=%~dp0

set PB_PROJECTS=apps\amn apps\fcs apps\platform apps\sample apps\ship-link apps\radar services\broker services\consumer services\galnet services\producer

echo ================================================================
echo MDK² Unified Build
echo Root: %ROOT%
echo ================================================================

for %%P in (%PB_PROJECTS%) do (
    echo.
    echo --- Building %%P ---
    dotnet build "%ROOT%%%P" -c Release --nologo
    if errorlevel 1 (
        echo.
        echo ERROR: MDK² build failed for %%P
        pause
        exit /b 1
    )
)

echo.
echo ================================================================
echo All 10 Programmable Block projects built successfully.
echo MDK² has generated script.cs for each PB and (on Windows with the
echo game installed) deployed them to the Space Engineers IngameScripts
echo local folder as configured in each project's mdk.local.ini.
echo ================================================================
pause