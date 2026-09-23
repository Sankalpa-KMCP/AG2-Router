; AG2 Router - Inno Setup Script
; Per-user non-admin installation matching scripts/install.ps1 and scripts/uninstall.ps1 contracts

#ifndef AppVersion
#define AppVersion "0.2.0"
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

[Code]
const
  SYNCHRONIZE = $00100000;
  ERROR_FILE_NOT_FOUND = 2;
  SessionMutexName = 'Local\AG2Router_Session_Mutex';

var
  HasBackup: Boolean;
  InstallCompleted: Boolean;
  StagedAppDir: String;

function OpenMutexW(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
external 'OpenMutexW@kernel32.dll stdcall';

function CloseHandle(hObject: THandle): BOOL;
external 'CloseHandle@kernel32.dll stdcall';

function MoveFileW(lpExistingFileName, lpNewFileName: String): BOOL;
external 'MoveFileW@kernel32.dll stdcall';

// 0 = proven absent, 1 = held, -1 = query failed (unknown).
function SessionMutexState(): Integer;
var
  hMutex: THandle;
begin
  hMutex := OpenMutexW(SYNCHRONIZE, False, SessionMutexName);
  if hMutex <> 0 then
  begin
    Result := 1;
    CloseHandle(hMutex);
  end;
  if hMutex = 0 then
  begin
    if DLLGetLastError = ERROR_FILE_NOT_FOUND then
      Result := 0
    else
      Result := -1;
  end;
end;

function WaitForSessionMutexRelease(TimeoutMs: Integer): Boolean;
var
  Elapsed: Integer;
begin
  Result := False;
  Elapsed := 0;
  while Elapsed < TimeoutMs do
  begin
    if SessionMutexState() = 0 then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
    Elapsed := Elapsed + 250;
  end;
  Result := SessionMutexState() = 0;
end;

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

// A failed exit request must stop uninstall before files or registrations are removed.
function InitializeUninstall(): Boolean;
var
  InstalledExe: String;
  ResultCode: Integer;
begin
  Result := True;
  if SessionMutexState() <> 0 then
  begin
    if SessionMutexState() < 0 then
    begin
      MsgBox('AG2 Router stop state could not be verified. Uninstall was cancelled.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    InstalledExe := ExpandConstant('{app}\AG2Router.exe');
    if not FileExists(InstalledExe) then
    begin
      MsgBox('AG2 Router is currently running but its shutdown executable is missing.' + #13#10 +
             'Please exit AG2 Router before proceeding with uninstallation.',
             mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not Exec(InstalledExe, '--exit', '', SW_HIDE, ewNoWait, ResultCode) then
    begin
      MsgBox('AG2 Router shutdown command could not be started.' + #13#10 +
             'Please exit AG2 Router from the system tray before uninstalling.',
             mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not WaitForSessionMutexRelease(5000) then
    begin
      MsgBox('AG2 Router is currently running and could not be gracefully closed within 5 seconds.' + #13#10 +
             'Please exit AG2 Router from the system tray before uninstalling.',
             mbError, MB_OK);
      Result := False;
      Exit;
    end;

    Sleep(250);
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
  InstalledExe, AppDir, BackupDir: String;
  ResultCode: Integer;
begin
  Result := '';
  AppDir := ExpandConstant('{app}');
  BackupDir := AppDir + '.bak';
  if StagedAppDir <> '' then
  begin
    if CompareText(StagedAppDir, AppDir) <> 0 then
      Result := 'Install target changed after the prior installation was staged.';
    Exit;
  end;

  if SessionMutexState() < 0 then
  begin
    Result := 'AG2 Router stop state could not be verified. Installation was cancelled.';
    Exit;
  end;
  if SessionMutexState() = 1 then
  begin
    InstalledExe := ExpandConstant('{app}\AG2Router.exe');
    if not FileExists(InstalledExe) then
    begin
      Result := 'AG2 Router is currently running but its shutdown executable is missing.' + #13#10 +
                'Please exit AG2 Router before proceeding with installation.';
      Exit;
    end;

    // Request graceful shutdown and wait boundedly for primary instance termination via AG2Router.exe --exit
    if not Exec(InstalledExe, '--exit', '', SW_HIDE, ewNoWait, ResultCode) then
    begin
      Result := 'Failed to execute AG2 Router shutdown command (--exit).' + #13#10 +
                'Please exit AG2 Router from the system tray before proceeding with installation.';
      Exit;
    end;

    // Poll mutex up to 20 times (5000 ms)
    if not WaitForSessionMutexRelease(5000) then
    begin
      Result := 'AG2 Router is currently running and could not be gracefully closed within 5 seconds.' + #13#10 +
                'Please exit AG2 Router from the system tray before proceeding with installation.';
      Exit;
    end;

    // Allow brief pause for OS process handle closure and file unlocks before extraction
    Sleep(250);
  end;

  if DirExists(BackupDir) then
  begin
    Result := 'A prior installation backup already exists. Resolve that backup before upgrading.';
    Exit;
  end;
  if DirExists(AppDir) then
  begin
    if not MoveFileW(AppDir, BackupDir) then
    begin
      Result := 'Could not stage the prior installation. No application files were overwritten.';
      Exit;
    end;
    HasBackup := True;
    StagedAppDir := AppDir;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, BackupDir: String;
begin
  if CurStep = ssInstall then
  begin
    InstallCompleted := False;
  end
  else if CurStep = ssDone then
  begin
    AppDir := ExpandConstant('{app}');
    BackupDir := AppDir + '.bak';

    if FileExists(AppDir + '\AG2Router.exe') then
    begin
      InstallCompleted := True;
      if HasBackup and DirExists(BackupDir) and
         (not DelTree(BackupDir, True, True, True)) then
        MsgBox('The new version installed, but the prior installation backup could not be removed. Resolve the sibling .bak directory before the next upgrade.', mbError, MB_OK);
      CleanLegacyScriptUninstallRegistration();
    end
    else
    begin
      MsgBox('Installed application validation failed. The prior installation will be restored if possible.', mbError, MB_OK);
    end;
  end;
end;

procedure DeinitializeSetup();
var
  AppDir, BackupDir: String;
begin
  AppDir := ExpandConstant('{app}');
  BackupDir := AppDir + '.bak';

  if HasBackup and (not InstallCompleted) then
  begin
    if DirExists(AppDir) and (not DelTree(AppDir, True, True, True)) then
    begin
      MsgBox('Incomplete replacement could not be removed. The complete prior installation remains in the sibling .bak directory for manual recovery.', mbError, MB_OK);
      Exit;
    end;

    if DirExists(BackupDir) and (not MoveFileW(BackupDir, AppDir)) then
      MsgBox('The complete prior installation could not be restored automatically. It remains in the sibling .bak directory for manual recovery.', mbError, MB_OK);
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
