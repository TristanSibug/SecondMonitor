; ============================================================================
; SecondMonitor.iss  --  the script for the Inno Setup installer
;
; Result: installer\output\SecondMonitorSetup-<version>.exe
;
; What the installer does:
;   - installs SecondMonitor.exe (and its small helper files) into Program Files
;   - puts "Second Monitor" in the Start menu (and optionally on the desktop)
;   - installs the tablet app file (SecondMonitor.apk) next to it, with a Start menu
;     shortcut "Install tablet app (USB)" that sends it to the tablet using adb
;   - checks for the programs Second Monitor needs and, if you agree, installs the missing
;     ones with winget:  .NET 10 Desktop Runtime, ffmpeg, adb (platform-tools),
;     Virtual Display Driver
;   - adds an uninstaller (this does not remove the programs above, other things may use them)
;
; To build it you need:
;   1. Inno Setup 7 (64-bit edition recommended): https://jrsoftware.org/isinfo.php
;   2. ..\app\SecondMonitor.exe   (run build-exe.bat first)
;   3. installer\SecondMonitor.apk (your tablet app, renamed to SecondMonitor.apk)
;   Then run build-installer.bat (or open this file in Inno Setup and press Compile).
; ============================================================================

#if Ver < EncodeVer(7, 0, 0)
  #error This installer requires Inno Setup 7 or newer.
#endif

#define MyAppName "Second Monitor"
#define MyAppVersion "1.4.0"
#define MyAppExe "SecondMonitor.exe"

[Setup]
; AppId = the installer's "name tag" for this program. Keep it the same in every version,
; so a new installer upgrades the old one instead of installing a second copy.
AppId={{6339C09A-FFE5-482E-A88B-221A0261D3FA}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; Program Files and the driver need administrator rights
PrivilegesRequired=admin
; Inno Setup 7 can build a native x64 setup executable for our win-x64 app.
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=output
OutputBaseFilename=SecondMonitorSetup-{#MyAppVersion}
SetupIconFile=..\pc\icon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; If Second Monitor is running, setup asks you to close it first (it uses this flag name)
AppMutex=SecondMonitor_SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "installdeps"; Description: "Install missing required programs with winget (.NET 10 Desktop Runtime, ffmpeg, adb, Virtual Display Driver)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; Everything build-exe.bat made (except debug files)
Source: "..\app\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "SecondMonitor.apk"; DestDir: "{app}"; Flags: ignoreversion
Source: "install-tablet-app.bat"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autoprograms}\Install tablet app (USB)"; Filename: "{app}\install-tablet-app.bat"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; If you ticked "Run when Windows starts" inside the program, remove that entry when uninstalling
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SecondMonitor"; Flags: uninsdeletevalue

[Run]
; Optional on the final page: the tablet must already allow USB debugging.
; Keep this synchronous so the helper finishes before the PC app is launched.
Filename: "{cmd}"; Parameters: "/D /C """"{app}\install-tablet-app.bat"""""; Description: "Install tablet app over USB (connect and authorize the tablet first)"; Flags: postinstall skipifsilent runasoriginaluser unchecked
Filename: "{app}\{#MyAppExe}"; Description: "Start {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

; ============================================================================
; [Code] = small programs inside the installer, written in Pascal.
; ============================================================================
[Code]

var
  FailedList: string;   // names of programs that could not be installed

// Is the ".NET 10 Desktop Runtime" installed? Windows keeps a list of installed versions
// in the registry; we look for one that starts with "10."
function IsDotNet10DesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// Is a program (like ffmpeg.exe) somewhere we can find it? We look in PATH and in the
// folders where winget puts its shortcuts.
function ToolExists(const FileName: String): Boolean;
var
  Folders: String;
begin
  Folders := GetEnv('PATH')
    + ';' + ExpandConstant('{localappdata}') + '\Microsoft\WinGet\Links'
    + ';' + ExpandConstant('{commonpf64}') + '\WinGet\Links';
  Result := FileSearch(FileName, Folders) <> '';
end;

// The Virtual Display Driver keeps its settings in C:\VirtualDisplayDriver, so that folder
// is a good sign that it is installed.
function IsVirtualDisplayDriverInstalled(): Boolean;
begin
  Result := DirExists(ExpandConstant('{sd}') + '\VirtualDisplayDriver');
end;

// Is winget (Windows' package installer) available?
function WingetAvailable(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c winget --version', '', SW_HIDE,
                 ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

// Installs one program with winget. If it fails, its name is added to FailedList.
// 'Quiet' = no windows (the driver installer is NOT quiet, so Windows can ask its questions).
procedure InstallPackage(const Title, PackageId: String; Quiet: Boolean);
var
  Args: String;
  ResultCode: Integer;
  ShowMode: Integer;
  Ok: Boolean;
begin
  WizardForm.StatusLabel.Caption := 'Installing ' + Title + ' ... (this can take a few minutes)';
  WizardForm.FileNameLabel.Caption := '';

  Args := '/c winget install --id ' + PackageId
        + ' -e --accept-package-agreements --accept-source-agreements';
  ShowMode := SW_SHOW;
  if Quiet then
  begin
    Args := Args + ' --silent';
    ShowMode := SW_HIDE;
  end;

  Ok := Exec(ExpandConstant('{cmd}'), Args, '', ShowMode, ewWaitUntilTerminated, ResultCode)
        and (ResultCode = 0);
  if not Ok then
    FailedList := FailedList + #13#10 + '  - ' + Title;
end;

// Runs at each step of the installation. After the files are copied, install what is missing.
procedure CurStepChanged(CurStep: TSetupStep);
var
  NeedDotNet, NeedFfmpeg, NeedAdb, NeedDriver: Boolean;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('installdeps') then
  begin
    FailedList := '';

    NeedDotNet := not IsDotNet10DesktopInstalled();
    NeedFfmpeg := not ToolExists('ffmpeg.exe');
    NeedAdb := not ToolExists('adb.exe');
    NeedDriver := not IsVirtualDisplayDriverInstalled();

    if NeedDotNet or NeedFfmpeg or NeedAdb or NeedDriver then
    begin
      if not WingetAvailable() then
      begin
        // No winget: just report what is missing
        if NeedDotNet then FailedList := FailedList + #13#10 + '  - .NET 10 Desktop Runtime';
        if NeedFfmpeg then FailedList := FailedList + #13#10 + '  - ffmpeg';
        if NeedAdb then FailedList := FailedList + #13#10 + '  - adb (Android platform-tools)';
        if NeedDriver then FailedList := FailedList + #13#10 + '  - Virtual Display Driver';
      end
      else
      begin
        if NeedDotNet then InstallPackage('.NET 10 Desktop Runtime', 'Microsoft.DotNet.DesktopRuntime.10', True);
        if NeedFfmpeg then InstallPackage('ffmpeg', 'Gyan.FFmpeg', True);
        if NeedAdb then InstallPackage('adb (Android platform-tools)', 'Google.PlatformTools', True);
        if NeedDriver then InstallPackage('Virtual Display Driver', 'VirtualDrivers.Virtual-Display-Driver', False);
      end;
    end;

    if FailedList <> '' then
    begin
      MsgBox('Second Monitor is installed, but these programs could not be installed automatically:'
        + #13#10 + FailedList + #13#10 + #13#10
        + 'Please install them by hand (see the README on GitHub), then start Second Monitor.',
        mbInformation, MB_OK);
    end;
  end;
end;
