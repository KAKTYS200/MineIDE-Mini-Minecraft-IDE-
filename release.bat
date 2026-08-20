@echo off
chcp 65001 > nul
title Mine IDE - Build & Publish
cd /d "%~dp0"
echo ============================================
echo   Mine IDE - Self-Contained Build
echo ============================================
echo.
echo NOTE: close all running MineIDE.exe first (it locks the file).
echo.
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
echo.
if not "%errorlevel%"=="0" (
    echo PUBLISH FAILED
    pause
    exit /b 1
)
echo.
copy /y "%~dp0bin\Release\net8.0-windows\win-x64\publish\MineIDE.exe" "%~dp0MineIDE.exe" > nul
if not "%errorlevel%"=="0" (
    echo Copy failed - close running MineIDE.exe and try again.
    pause
    exit /b 1
)
echo Done! Your exe is right here:
echo   %~dp0MineIDE.exe
echo.
echo Double-click it to run - no .NET install required.
echo.
pause
