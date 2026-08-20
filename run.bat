@echo off
chcp 65001 > nul
title Mine IDE
cd /d "%~dp0"
echo ============================================
echo   Mine IDE Launcher
echo ============================================
echo.
echo Starting... (using 'dotnet run' for max compatibility)
dotnet run --project MineIDE.csproj -c Release
echo.
if errorlevel 1 (
    echo.
    echo Build/run failed. Error code: %errorlevel%
    echo Looking for crash log...
    if exist "%LOCALAPPDATA%\MineIDE\mine_ide_crash.log" (
        echo --- crash log follows ---
        type "%LOCALAPPDATA%\MineIDE\mine_ide_crash.log"
    )
    pause
)
