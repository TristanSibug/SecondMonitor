@echo off
REM ==========================================================
REM  build-installer.bat - builds SecondMonitorSetup-<version>.exe
REM
REM  Before you run this:
REM    1. Run build-exe.bat (it makes the "app" folder).
REM    2. Put your tablet app in the "installer" folder and name it SecondMonitor.apk.
REM    3. Install Inno Setup 7 (64-bit edition recommended): https://jrsoftware.org/isinfo.php
REM
REM  Result: installer\output\SecondMonitorSetup-<version>.exe
REM ==========================================================

setlocal
cd /d "%~dp0installer"

if not exist "..\app\SecondMonitor.exe" goto noexe
if not exist "SecondMonitor.apk" goto noapk

REM An explicit ISCC path supports portable or custom Inno Setup 7 installations.
if defined ISCC (
    if exist "%ISCC%" goto build
    echo The configured ISCC compiler was not found: "%ISCC%"
    goto failed
)

REM Prefer the 64-bit edition, then check the other standard install locations.
set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%ProgramFiles(x86)%\Inno Setup 7 (32-bit)\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%LocalAppData%\Programs\Inno Setup 7\ISCC.exe"
if exist "%ISCC%" goto build
set "ISCC=%LocalAppData%\Programs\Inno Setup 7 (32-bit)\ISCC.exe"
if exist "%ISCC%" goto build
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do (
    set "ISCC=%%I"
    goto build
)
goto noinno

:build
"%ISCC%" SecondMonitor.iss
if errorlevel 1 goto failed
echo.
echo Done. Your installer is in:  %~dp0installer\output
pause
exit /b 0

:noexe
echo app\SecondMonitor.exe was not found. Run build-exe.bat first.
pause
exit /b 1

:noapk
echo installer\SecondMonitor.apk was not found.
echo Copy your tablet app there and name it SecondMonitor.apk.
pause
exit /b 1

:noinno
echo Inno Setup 7 was not found. Install it from https://jrsoftware.org/isinfo.php
echo For a custom installation, set ISCC to the full path of its ISCC.exe.
pause
exit /b 1

:failed
echo.
echo BUILD FAILED. Read the error above.
pause
exit /b 1
