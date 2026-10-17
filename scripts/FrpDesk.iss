#define AppName "FrpDesk"
#define AppVersion "0.1.2"
#define AppPublisher "FrpDesk"
#define AppExeName "FrpDesk.exe"

[Setup]
AppId={{7E4C9A52-3B1D-4F86-9C0E-FRPDESKWIN01}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\artifacts
OutputBaseFilename=FrpDesk-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\assets\FrpDesk.ico
CloseApplications=yes
CloseApplicationsFilter=FrpDesk.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "登录 Windows 后自动启动 FrpDesk"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "..\artifacts\FrpDesk-{#AppVersion}-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\assets\FrpDesk.ico"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"; IconFilename: "{app}\assets\FrpDesk.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\assets\FrpDesk.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FrpDesk"; ValueData: """{app}\{#AppExeName}"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"; ValueType: string; ValueName: "Publisher"; ValueData: "{#AppPublisher}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk"; ValueType: string; ValueName: "DisplayIcon"; ValueData: "{app}\assets\FrpDesk.ico"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""if (Get-Process FrpDesk -ErrorAction SilentlyContinue) {{ Stop-Process -Name FrpDesk -Force }}; Get-Service -Name 'FrpDesk-Instance-*' -ErrorAction SilentlyContinue | ForEach-Object {{ sc.exe delete $_.Name }}"""; Flags: runhidden

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;