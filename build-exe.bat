@echo off
REM ==========================================================
REM  build-exe.bat - builds the program into the "app" folder
REM
REM  Result: app\SecondMonitor.exe  (plus a few small files next to it
REM  that it needs: keep them together in the same folder).
REM  It does not contain .NET. A PC needs the ".NET 10 Desktop Runtime"
REM  installed to run it (the .NET 10 SDK includes it).
REM
REM  Want ONE single exe file instead? Use build-single-file.bat.
REM ==========================================================

cd /d "%~dp0pc"

dotnet publish -c Release -o "%~dp0app"
if errorlevel 1 (
    echo.
    echo BUILD FAILED. Read the error above.
    pause
    exit /b 1
)

echo.
echo Done. Your program is:  %~dp0app\SecondMonitor.exe
pause
