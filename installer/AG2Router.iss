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
PrivilegesRequiredOverridesAllowed=commandline
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
; Gracefully signal running instance to exit and wait boundedly for primary termination before uninstallation begins
Filename: "{app}\AG2Router.exe"; Parameters: "--exit"; Flags: runhidden skipifdoesntexist

[Code]
// Prerequisite check for Microsoft Edge WebView2 Evergreen Runtime & per-user validation
function InitializeSetup(): Boolean;
var
  Wv2Ver: String;
  Wv2Found: Boolean;
begin
  Result := True;

  // Enforce strictly per-user non-admin installation invariant
  if IsAdminInstallMode then
  begin
    MsgBox('AG2 Router is a per-user application and cannot be installed with administrative privileges.' + #13#10 +
           'Please run the installer as a standard user without elevation.',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;

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

// Helper function to strip trailing backslashes for exact path comparisons
function StripTrailingBackslash(const S: String): String;
begin
  Result := S;
  while (Length(Result) > 0) and (Result[Length(Result)] = '\') do
    Delete(Result, Length(Result), 1);
end;

// Safely prune legacy script-based uninstall registration (from scripts/install.ps1)
// to guarantee a single entry in Windows Installed Apps post-upgrade.
procedure CleanLegacyScriptUninstallRegistration();
var
  LegacyKey: String;
  DisplayNameVal: String;
  PublisherVal: String;
  InstallLocationVal: String;
  UninstallStringVal: String;
  ExpectedAppDir: String;
  LowerUninstall: String;
begin
  LegacyKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router';

  // Guard 1: Legacy registry key must exist in HKCU
  if not RegKeyExists(HKCU, LegacyKey) then
    Exit;

  // Guard 2: DisplayName must match 'AG2 Router'
  if not RegQueryStringValue(HKCU, LegacyKey, 'DisplayName', DisplayNameVal) or
     (CompareText(Trim(DisplayNameVal), 'AG2 Router') <> 0) then
    Exit;

  // Guard 3: Publisher must match 'AG2'
  if not RegQueryStringValue(HKCU, LegacyKey, 'Publisher', PublisherVal) or
     (CompareText(Trim(PublisherVal), 'AG2') <> 0) then
    Exit;

  // Guard 4: InstallLocation must match the active {app} target directory
  if not RegQueryStringValue(HKCU, LegacyKey, 'InstallLocation', InstallLocationVal) then
    Exit;

  ExpectedAppDir := StripTrailingBackslash(ExpandConstant('{app}'));
  if CompareText(StripTrailingBackslash(Trim(InstallLocationVal)), ExpectedAppDir) <> 0 then
    Exit;

  // Guard 5: UninstallString must reference 'uninstall.ps1' or 'AG2Router'
  if not RegQueryStringValue(HKCU, LegacyKey, 'UninstallString', UninstallStringVal) then
    Exit;

  LowerUninstall := Lowercase(UninstallStringVal);
  if (Pos('uninstall.ps1', LowerUninstall) = 0) and (Pos('ag2router', LowerUninstall) = 0) then
    Exit;

  // All 5 strict ownership guards passed: safe to delete legacy uninstall key
  RegDeleteKeyIncludingSubkeys(HKCU, LegacyKey);
end;

// Graceful shutdown before installing or upgrading over existing files
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledExe: String;
  ResultCode: Integer;
begin
  Result := '';
  InstalledExe := ExpandConstant('{app}\AG2Router.exe');
  if FileExists(InstalledExe) then
  begin
    // Request graceful shutdown and wait boundedly for primary instance termination via AG2Router.exe --exit
    if Exec(InstalledExe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if ResultCode <> 0 then
      begin
        Result := 'AG2 Router is currently running and could not be gracefully closed.' + #13#10 +
                  'Please exit AG2 Router from the system tray before proceeding with installation.';
      end
      else
      begin
        // Allow brief pause for OS process handle closure and file unlocks before extraction
        Sleep(250);
      end;
    end
    else
    begin
      Result := 'Failed to execute AG2 Router shutdown command (--exit).';
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    CleanLegacyScriptUninstallRegistration();
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
      else
      begin
        if Pos('.exe', Lowercase(CleanExePath)) > 0 then
          CleanExePath := Copy(CleanExePath, 1, Pos('.exe', Lowercase(CleanExePath)) + 3)
        else if Pos(' ', CleanExePath) > 0 then
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
