; Inno Setup script for TidyWindow

#define MyAppName "TidyWindow"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#define MyAppPublisher "Cosmos-0118"
#define MyAppExeName "TidyWindow.exe"
#define MyAppAumid "TidyWindow"
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
Name: "{autoprograms}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; WorkingDir: "{app}"; AppUserModelID: "{#MyAppAumid}"
Name: "{autodesktop}\\{#MyAppName}"; Filename: "{app}\\{#MyAppExeName}"; Tasks: desktopicon; WorkingDir: "{app}"; AppUserModelID: "{#MyAppAumid}"

[Run]
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall skipifsilent runasoriginaluser unchecked

[InstallDelete]
; Remove any stale binaries from earlier builds so removed files do not linger after upgrades
Type: filesandordirs; Name: "{app}\\*"

[Registry]
Root: HKCU; Subkey: "Software\\Microsoft\\Windows\\CurrentVersion\\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\\{#MyAppExeName}"""; Check: IsTaskSelected('runatstartup')

[Code]
type
  TSystemTime = record
    Year: Word;
    Month: Word;
    DayOfWeek: Word;
    Day: Word;
    Hour: Word;
    Minute: Word;
    Second: Word;
    Millisecond: Word;
  end;

const
  PrevUninstallKey = 'SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{6F4045F0-2C7A-4D37-9A4B-9EFEAD0D8F8D}';

procedure GetLocalTime(var lpSystemTime: TSystemTime);
  external 'GetLocalTime@kernel32.dll stdcall';

function PadTwoDigits(const Value: Integer): string;
begin
  if Value < 10 then
    Result := '0' + IntToStr(Value)
  else
    Result := IntToStr(Value);
end;

function BuildTimestamp: string;
var
  ST: TSystemTime;
begin
  GetLocalTime(ST);
  Result := IntToStr(ST.Year)
    + PadTwoDigits(ST.Month)
    + PadTwoDigits(ST.Day)
    + PadTwoDigits(ST.Hour)
    + PadTwoDigits(ST.Minute)
    + PadTwoDigits(ST.Second);
end;

function TryGetPreviousUninstallString(var UninstallString: string): Boolean;
begin
  UninstallString := '';

  if RegQueryStringValue(HKLM64, PrevUninstallKey, 'UninstallString', UninstallString) then
    Result := True
  else if RegQueryStringValue(HKCU, PrevUninstallKey, 'UninstallString', UninstallString) then
    Result := True
  else
    Result := False;
end;

function RunUninstallerSilently(const UninstallString: string): Boolean;
var
  CmdLine: string;
  ExitCode: Integer;
begin
  Result := False;
  if UninstallString = '' then
    Exit;

  CmdLine := UninstallString + ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART';
  Log('Running previous uninstaller: ' + CmdLine);

  if Exec(ExpandConstant('{cmd}'), '/C ' + CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := (ExitCode = 0)
  else
    Log('Failed to start previous uninstaller.');
end;

procedure BackupUserData;
var
  SourceDir: string;
  TargetDir: string;
  Timestamp: string;
  ExitCode: Integer;
  Cmd: string;
begin
  SourceDir := ExpandConstant('{userappdata}\\{#MyAppName}');
  if not DirExists(SourceDir) then
  begin
    Log('No user data directory found at: ' + SourceDir);
    Exit;
  end;

  Timestamp := BuildTimestamp();
  TargetDir := ExpandConstant('{tmp}\\{#MyAppName}_Backup_' + Timestamp);
  if not ForceDirectories(TargetDir) then
  begin
    Log('Failed to create backup target directory: ' + TargetDir);
    Exit;
  end;

  if Exec('robocopy', '"' + SourceDir + '" "' + TargetDir + '" /MIR /FFT /Z /NFL /NDL', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Log('robocopy exit code: ' + IntToStr(ExitCode));
  end
  else
  begin
    Cmd := '/C xcopy "' + SourceDir + '" "' + TargetDir + '" /E /I /Y /Q';
    if Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Log('xcopy exit code: ' + IntToStr(ExitCode))
    else
      Log('Failed to run xcopy for user data backup.');
  end;

  Log('User data backup saved to: ' + TargetDir);
end;

function TerminateRunningInstance: Boolean;
var
  ExitCode: Integer;
begin
  Result := Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  if Result then
    Log('taskkill exit code: ' + IntToStr(ExitCode))
  else
    Log('taskkill command could not be executed.');
end;

function InitializeSetup: Boolean;
var
  UninstallString: string;
  Response: Integer;
begin
  Result := True;

  if TryGetPreviousUninstallString(UninstallString) then
  begin
    Response := MsgBox('A previous installation of {#MyAppName} was detected. Do you want to remove it before continuing?',
      mbConfirmation, MB_YESNO);

    if Response = IDYES then
    begin
      TerminateRunningInstance;

      if not RunUninstallerSilently(UninstallString) then
      begin
        MsgBox('Automatic removal of the previous version failed. You may cancel setup to uninstall it manually or continue with a side-by-side installation.',
          mbError, MB_OK);

        if MsgBox('Do you want to cancel the installation now?', mbConfirmation, MB_YESNO) = IDYES then
        begin
          Result := False;
          Exit;
        end;
      end;
    end;
  end;

  BackupUserData;
  TerminateRunningInstance;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Log('Post-install step complete.');
end;
