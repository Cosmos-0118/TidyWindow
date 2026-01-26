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
; Launch without holding the installer process open; shellexec breaks any parent/child wait chain
Filename: "{app}\\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: postinstall skipifsilent runasoriginaluser unchecked nowait shellexec
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
begin
  Result := True;

  { Rely on Inno Setup's built-in upgrade handling so we don't deadlock by invoking a previous
    uninstaller while this installer already holds the setup mutex. }
  BackupUserData;
  TerminateRunningInstance;
end;

procedure CurStepChanged(CurStep: TSetupStep);
const
  WM_CLOSE = $0010;
begin
  if CurStep = ssPostInstall then
    Log('Post-install step complete.');

  { Make sure the wizard tears down cleanly once the Finish page is done }
  if CurStep = ssDone then
    PostMessage(WizardForm.Handle, WM_CLOSE, 0, 0);
end;
