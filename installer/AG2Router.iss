; AG2 Router - Inno Setup Script
; Per-user non-admin installation matching scripts/install.ps1 and scripts/uninstall.ps1 contracts

#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

#ifndef SourceDir
#define SourceDir "..\publish\win-x64"
#endif

#ifndef OutputDir
#define OutputDir "..\dist"
#endif

[Setup]
AppId={{D37E7404-585A-4B6A-B7F9-5360980DF628}
AppName=AG2 Router
AppVersion={#AppVersion}
AppPublisher=AG2
DefaultDirName={localappdata}\Programs\AG2Router
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=AG2Router-Setup-v{#AppVersion}-win-x64
OutputDir={#OutputDir}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\AG2Router.exe
UninstallDisplayName=AG2 Router
CloseApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\AG2 Router"; Filename: "{app}\AG2Router.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\AG2Router.exe"; Description: "{cm:LaunchProgram,AG2 Router}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Gracefully signal running instance to exit before uninstallation begins
Filename: "{app}\AG2Router.exe"; Parameters: "--exit"; Flags: runhidden skipifdoesntexist

[Code]
// Prerequisite check for Microsoft Edge WebView2 Evergreen Runtime
function InitializeSetup(): Boolean;
var
  Wv2Ver: String;
  Wv2Found: Boolean;
begin
  Result := True;
  Wv2Found := False;

  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Wv2Ver) then
  begin
    if (Wv2Ver <> '') and (Wv2Ver <> '0.0.0.0') then
      Wv2Found := True;
  end;

  if (not Wv2Found) and RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Wv2Ver) then
  begin
    if (Wv2Ver <> '') and (Wv2Ver <> '0.0.0.0') then
      Wv2Found := True;
  end;

  if not Wv2Found then
  begin
    MsgBox('Microsoft Edge WebView2 Evergreen Runtime was not detected.' + #13#10 +
           'The system tray and background routing will function, but the dashboard UI requires WebView2.' + #13#10 +
           'You can install it from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/',
           mbInformation, MB_OK);
  end;
end;

// Startup ownership verification during uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentRunVal: String;
  CleanExePath: String;
  ExpectedExe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExpectedExe := ExpandConstant('{app}\AG2Router.exe');

    // Startup Run ownership check
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AG2Router', CurrentRunVal) then
    begin
      CleanExePath := Trim(CurrentRunVal);
      // Strip leading and trailing quotes if present
      if (Length(CleanExePath) > 0) and (CleanExePath[1] = '"') then
      begin
        Delete(CleanExePath, 1, 1);
        if Pos('"', CleanExePath) > 0 then
          CleanExePath := Copy(CleanExePath, 1, Pos('"', CleanExePath) - 1);
      end
      else if Pos(' ', CleanExePath) > 0 then
      begin
        CleanExePath := Copy(CleanExePath, 1, Pos(' ', CleanExePath) - 1);
      end;

      // Only delete Run key if it points to THIS installation
      if CompareText(CleanExePath, ExpectedExe) = 0 then
      begin
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AG2Router');
      end;
    end;
  end;
end;
