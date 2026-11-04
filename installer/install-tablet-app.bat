@echo off
REM ==========================================================
REM  Installs the Second Monitor app on your tablet using adb.
REM  Before you run this:
REM    1. Plug the tablet into the PC with a USB cable.
REM    2. Turn on USB debugging on the tablet (Developer options).
REM    3. When the tablet asks "Allow USB debugging?", tap Allow.
REM ==========================================================

REM adb may have just been installed, so also look where winget puts its shortcuts
set "PATH=%PATH%;%LOCALAPPDATA%\Microsoft\WinGet\Links;%ProgramFiles%\WinGet\Links"

where adb >nul 2>nul
if errorlevel 1 goto noadb

echo Installing the tablet app...
adb install -r "%~dp0SecondMonitor.apk"
if errorlevel 1 goto failed

echo.
echo Done. The app "Second Monitor" is now on your tablet.
pause
exit /b 0

:noadb
echo.
echo adb was not found. Install "Android platform-tools" first
echo (for example: winget install Google.PlatformTools), then run this again.
pause
exit /b 1

:failed
echo.
echo Installing failed. Check the cable, that USB debugging is on, and that you tapped Allow.
echo You can also copy SecondMonitor.apk to the tablet and open it there.
pause
exit /b 1
