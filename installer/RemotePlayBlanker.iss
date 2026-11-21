; Inno Setup script for Remote Play Blanker.
; Build: dotnet publish -c Release -o publish   then   ISCC installer\RemotePlayBlanker.iss
; (or just run build-installer.ps1 in the project root)

#define AppName "Remote Play Blanker"
#define AppExe "RemotePlayBlanker.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "..\publish\" + AppExe)

[Setup]
AppId={{6E0F4C1B-8B7A-4E5B-9C55-2B7F5E8D9A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Remote Play Blanker contributors
AppPublisherURL=https://github.com/EVFUBS/remote-play-blanker
AppSupportURL=https://github.com/EVFUBS/remote-play-blanker/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\RemotePlayBlanker\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=RemotePlayBlanker-Setup-{#AppVersion}
CloseApplications=no

[Tasks]
Name: "driver"; Description: "Install the virtual display (recommended: lets every monitor switch off fully)"
Name: "autostart"; Description: "Start {#AppName} when Windows starts"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "driver\MttVDD.inf"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "driver\MttVDD.dll"; DestDir: "{app}\driver"; Flags: ignoreversion
Source: "driver\mttvdd.cat"; DestDir: "{app}\driver"; Flags: ignoreversion
; The driver reads its modes from here. Don't overwrite a config the user (or another app) already has.
Source: "driver\vdd_settings.xml"; DestDir: "{sd}\VirtualDisplayDriver"; Tasks: driver; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RemotePlayBlanker"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  StateKey = 'Software\RemotePlayBlanker';

procedure StopRunningApp(const Exe: String);
var
  Code: Integer;
begin
  { Ask the running tray app to quit cleanly so it restores the monitors first. }
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('driver') then
  begin
    WizardForm.StatusLabel.Caption := 'Installing the virtual display driver...';
    if Exec(ExpandConstant('{app}\{#AppExe}'),
            ExpandConstant('--install-driver "{app}\driver\MttVDD.inf"'),
            '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
      { We created the device, so uninstall should remove it again. }
      RegWriteDWordValue(HKLM, StateKey, 'InstalledDriver', 1)
    else if Code <> 2 then  { 2 = it was already installed (e.g. by another app) }
      MsgBox('The virtual display driver could not be installed (code ' + IntToStr(Code) + ').' + #13#10 +
             'Remote Play Blanker will fall back to DDC/CI monitor standby, which not every monitor supports.',
             mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Cardinal;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
    if RegQueryDWordValue(HKLM, StateKey, 'InstalledDriver', Code) and (Code = 1) then
      Exec(ExpandConstant('{app}\{#AppExe}'), '--uninstall-driver', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteKeyIncludingSubkeys(HKLM, StateKey);
  end;
end;
