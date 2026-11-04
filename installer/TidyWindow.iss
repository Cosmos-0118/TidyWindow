; Inno Setup script for TidyWindow. Built by installer/build.ps1 from a self-contained publish folder.
; Written for Inno Setup 6.3 or later. CI installs 6.7.1 from Chocolatey; keep that pin (in .github/workflows) in step with this script.

#define MyAppName "TidyWindow"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "Cosmos-0118"
#define MyAppExeName "TidyWindow.Next.exe"
#define MyAppDataDir "TidyWindow.Next"
#ifndef BuildOutput
  #define BuildOutput "..\artifacts\publish"
#endif

[Setup]
; A different id from the v4.x installer, so the two never treat each other as an upgrade.
AppId={{B7C1D5E2-3F4A-4B6D-9E8C-2A1F0D7B5C93}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Cosmos-0118/TidyWindow
AppSupportURL=https://github.com/Cosmos-0118/TidyWindow/issues
AppUpdatesURL=https://github.com/Cosmos-0118/TidyWindow/releases
; The version resource of Setup itself, so a downloaded file says what it is under Properties > Details. build.ps1 makes sure the
; version has three parts; the fourth is always 0.
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoTextVersion={#MyAppVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
VersionInfoCopyright=MIT License
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=TidyWindow-Setup-{#MyAppVersion}
SetupIconFile=..\src\TidyWindow.Desktop\Assets\Brand\tidywindow.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The app itself runs without administrator rights, but its administrator helper (CleanHost\TidyWindow.CleanHost.exe) starts from the
; install folder every time the person approves a Windows prompt. Under Program Files only administrators can change that folder. A
; per-user install puts it under the profile, where any program running as that account could replace the helper before the next
; approved prompt. So the default is an all-users install. The dialog at the start still offers the per-user choice (as does
; /CURRENTUSER on the command line), and the README explains the trade-off.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
; x64 Windows, plus Arm64 Windows 11, where x64 programs run through emulation. Arm64 Windows 10 has no x64 emulation, so the app would
; not start there; x64os would turn those PCs away at setup time, but every Arm64 Windows 11 PC with them.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Closes a running TidyWindow (and its helpers) so its files can be replaced during an upgrade. The app refuses to close while a package
; operation is running, so Restart Manager may report it as still in use; Setup then lets the person retry or go on once the app has
; been closed by hand. AppMutex would let Setup say so before it copies anything, but the app announces its instance through the
; Windows App SDK (AppInstance), not a named mutex, so there is nothing for AppMutex to name.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; WorkingDir: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall skipifsilent nowait runasoriginaluser

[InstallDelete]
; Remove the files of an earlier build so files dropped in a newer version do not linger, but only when the chosen folder is one a
; previous TidyWindow Setup registered. Without the check, a folder picked by mistake (a shared tools folder, say) would be emptied.
Type: filesandordirs; Name: "{app}\*"; Check: AppFolderIsOurs

[Code]
// The folder a previous Setup of this app registered in the given hive, without a trailing backslash, or '' when there is none.
function RegisteredInstallFolder(const Root: Integer): string;
var
  Value: string;
begin
  Result := '';
  if RegQueryStringValue(Root, ExpandConstant('Software\Microsoft\Windows\CurrentVersion\Uninstall\{#emit SetupSetting("AppId")}_is1'), 'InstallLocation', Value) then
    Result := RemoveBackslashUnlessRoot(Value);
end;

// True when the chosen folder holds this app and is the folder a previous Setup registered, for all users or for this account.
function AppFolderIsOurs: Boolean;
var
  Folder: string;
begin
  Folder := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  Result := FileExists(AddBackslash(Folder) + '{#MyAppExeName}') and
    ((CompareText(RegisteredInstallFolder(HKLM), Folder) = 0) or (CompareText(RegisteredInstallFolder(HKCU), Folder) = 0));
end;

// The app keeps its settings, logs and the copies it needs to undo changes in one folder per account, and the administrator helper
// keeps the copies it trusts when it puts something back under ProgramData. Uninstalling keeps both unless the person says otherwise,
// because deleting them also deletes the way back from cleanups, repairs and tweaks.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserData, SharedData, Folders, Note: string;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;
  // An all-users uninstall runs with administrator rights, so the local application data folder is then the one of the account that
  // approved the prompt, which need not be the account that used the app. The full path is shown so the person can see whose it is.
  UserData := ExpandConstant('{localappdata}\{#MyAppDataDir}');
  SharedData := ExpandConstant('{commonappdata}\{#MyAppDataDir}');
  Folders := '';
  Note := '';
  if DirExists(UserData) then
    Folders := Folders + UserData + #13#10;
  if IsAdminInstallMode then
  begin
    if DirExists(SharedData) then
      Folders := Folders + SharedData + #13#10;
    Note := 'Only these folders are deleted; other accounts on this PC keep their own TidyWindow folders. ';
  end;
  if Folders = '' then
    Exit;
  if MsgBox('Also delete TidyWindow''s settings, logs and saved undo data?' + #13#10 + #13#10 +
      'This includes the files Cleanup is holding for you (old installers and unfinished downloads, kept for 14 days), ' +
      'the backups Repair made, and the saved originals that let Startup, Quiet, Tune and Debloat put things back. ' +
      'Without them, changes TidyWindow made can no longer be undone.' + #13#10 + #13#10 +
      Folders + #13#10 +
      Note + 'Choose No to keep everything.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    Exit;
  if DirExists(UserData) then
    DelTree(UserData, True, True, True);
  if IsAdminInstallMode and DirExists(SharedData) then
    DelTree(SharedData, True, True, True);
end;
