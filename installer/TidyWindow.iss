; Inno Setup script for TidyWindow. Built by installer/build.ps1 from a self-contained publish folder.

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
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=TidyWindow-Setup-{#MyAppVersion}
SetupIconFile=..\app\Assets\Brand\tidywindow.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The app runs without administrator rights, so the default is a per-user install. Setup offers an all-users install on request.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Closes a running TidyWindow (and its helpers) so its files can be replaced during an upgrade.
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
; Remove binaries from an earlier build so files dropped in a newer version do not linger.
Type: filesandordirs; Name: "{app}\*"

[Code]
{ The app keeps its settings, logs and the copies it needs to undo changes in one folder. Uninstalling keeps it
  unless the person says otherwise, because deleting it also deletes the way back from cleanups, repairs and tweaks. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    DataDir := ExpandConstant('{localappdata}\{#MyAppDataDir}');
    if DirExists(DataDir) and (MsgBox(
      'Also delete TidyWindow''s settings, logs and saved undo data?' + #13#10 + #13#10 +
      'Without the undo data, changes TidyWindow made (cleanups, repairs, tweaks) can no longer be put back.' + #13#10 + #13#10 +
      DataDir + #13#10 + #13#10 +
      'Choose No to keep it.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(DataDir, True, True, True);
  end;
end;
