@echo off
REM ==========================================================
REM  build-single-file.bat - builds ONE file: app-single\SecondMonitor.exe
REM
REM  The exe does not contain .NET (a PC needs the ".NET 10 Desktop
REM  Runtime" installed to run it).
REM
REM  NOTE: this build may need to download a few extra packages from
REM  NuGet, so it needs an internet connection. If it fails with
REM  "NU1100" or "Unable to resolve" errors, use build-exe.bat instead
REM  (it needs no downloads) and zip the app folder.
REM ==========================================================

cd /d "%~dp0pc"

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=none -o "%~dp0app-single"
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Read the error above. Try build-exe.bat instead.
    pause
    exit /b 1
)

echo.
echo Done. Your program is:  %~dp0app-single\SecondMonitor.exe
pause
