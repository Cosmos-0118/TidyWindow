; Inno Setup script for TidyWindow

#define MyAppName "TidyWindow"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "Cosmos-0118"
#define MyAppExeName "TidyWindow.exe"
#ifndef BuildOutput
  #define BuildOutput "..\\src\\TidyWindow.App\\bin\\Release\\net8.0-windows\\win-x64\\publish"
#endif

[Setup]
AppId={{6F4045F0-2C7A-4D37-9A4B-9EFEAD0D8F8D}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf64}\\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=TidyWindow-Setup-{#MyAppVersion}
SetupIconFile=..\\resources\\applogo.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\\{#MyAppExeName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
LicenseFile=TERMS_AND_CONDITIONS.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "runatstartup"; Description: "&Run TidyWindow at Windows startup"; GroupDescription: "Optional features:"; Flags: unchecked

[Files]
Source: "{#BuildOutput}\\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: desktopicon; WorkingDir: "{app}"

[Run]
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; If the user selects the "runatstartup" task, create a Run registry value so the app starts with Windows
Root: HKCU; Subkey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\\{#MyAppExeName}"""; Check: IsTaskSelected('runatstartup')

[Code]
const
  PrevUninstallKey = 'SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{6F4045F0-2C7A-4D37-9A4B-9EFEAD0D8F8D}';

function BuildTimestamp(): string;
begin
  Result := GetDateTimeString('yyyymmddhhnnss', False);
end;

function IsPreviousInstalled(var UninstallString: string): Boolean;
var
  s: string;
begin
  Result := False;
  UninstallString := '';
  { Check HKLM 64-bit }
  if RegQueryStringValue(HKLM64, PrevUninstallKey, 'UninstallString', s) then
  begin
    UninstallString := s;
    Result := True;
    exit;
  end;
  { Check HKCU }
  if RegQueryStringValue(HKCU, PrevUninstallKey, 'UninstallString', s) then
  begin
    UninstallString := s;
    Result := True;
    exit;
  end;
end;

function RunUninstallerSilently(UninstallString: string): Boolean;
var
  CmdLine: string;
  rc: Integer;
begin
  Result := False;
  if UninstallString = '' then Exit;

  { Many uninstallers are quoted; ensure we pass the proper arguments for silent uninstall }
  CmdLine := UninstallString + ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART';
  Log('Running previous uninstaller: ' + CmdLine);
  if Exec(ExpandConstant('{cmd}'), '/C ' + CmdLine, '', SW_HIDE, ewWaitUntilTerminated, rc) then
  begin
    Result := (rc = 0);
  end;
end;

procedure BackupUserData();
var
  Src, Dest, TimeStamp, Cmd: string;
  rc: Integer;
begin
  Src := ExpandConstant('{userappdata}\\{#MyAppName}');
  if not DirExists(Src) then
  begin
    Log('No user data to backup from: ' + Src);
    exit;
  end;

  TimeStamp := BuildTimestamp();
  Dest := ExpandConstant('{tmp}\\{#MyAppName}_Backup_' + TimeStamp);
  if not DirExists(Dest) then
    ForceDirectories(Dest);

  { Use robocopy when available (more reliable), otherwise fall back to xcopy }
  if Exec('robocopy', '"' + Src + '" "' + Dest + '" /MIR /FFT /Z /NFL /NDL', '', SW_HIDE, ewWaitUntilTerminated, rc) then
  begin
    Log('robocopy exit code: ' + IntToStr(rc));
  end
  else
  begin
    Cmd := '/C xcopy "' + Src + '" "' + Dest + '" /E /I /Y';
    Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, rc);
    Log('xcopy exit code: ' + IntToStr(rc));
  end;
  Log('User data backed up to: ' + Dest);
end;

function InitializeSetup(): Boolean;
var
  UninstallStr: string;
  MsgResult: Integer;
begin
  Result := True; { allow setup to continue by default }

  { Check for a previous installed version and offer to uninstall it silently }
  if IsPreviousInstalled(UninstallStr) then
  begin
    MsgResult := MsgBox('A previous installation of {#MyAppName} was detected. Do you want to automatically uninstall the previous version before continuing?'#13#10#13#10'If you choose Yes, the previous version will be removed silently.', mbConfirmation, MB_YESNO);
    if MsgResult = IDYES then
    begin
      { Attempt to stop the running application first }
      Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, MsgResult);
      if not RunUninstallerSilently(UninstallStr) then
      begin
        MsgBox('Automatic uninstallation of the previous version failed. You can cancel setup and manually uninstall the previous version, or continue to install side-by-side.', mbError, MB_OK);
        { Allow user to continue or cancel }
        if MsgBox('Do you want to cancel the installation so you can uninstall the previous version manually?', mbConfirmation, MB_YESNO) = IDYES then
        begin
          Result := False;
          exit;
        end;
      end
      else
      begin
        Log('Previous installation uninstalled successfully.');
      end;
    end;
  end;

  { Backup user data if present }
  BackupUserData();

  { Ensure running application is closed to avoid locked files }
  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, MsgResult);

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { After installation finishes, if the user selected startup run, we leave the registry entry created by [Registry] }
  if CurStep = ssPostInstall then
  begin
    { Ensure desktop/start menu shortcuts exist (Inno will create them). No-op here; placeholder for future post-install actions }
    Log('Post-install step completed.');
  end;
end;
