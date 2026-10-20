#ifndef AppVersion
  #define AppVersion "0.1.4"
#endif
#define AppName "FrpDesk"
#define AppPublisher "FrpDesk"
#define AppExeName "FrpDesk.exe"

[Setup]
AppId={{7E4C9A52-3B1D-4F86-9C0E-FRPDESKWIN01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL=https://github.com/YM040923/FrpDesk
AppUpdatesURL=https://github.com/YM040923/FrpDesk/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes
OutputDir=..\artifacts
OutputBaseFilename=FrpDesk-{#AppVersion}-win-x64-setup
SetupIconFile=..\assets\FrpDesk.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\assets\FrpDesk.ico
AppMutex=Local\FrpDesk-FrpDesk
CloseApplications=yes
CloseApplicationsFilter=FrpDesk.exe
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "登录 Windows 后自动启动 FrpDesk"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "..\artifacts\FrpDesk-{#AppVersion}-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\assets\FrpDesk.ico"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"; IconFilename: "{app}\assets\FrpDesk.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\assets\FrpDesk.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall-Services.ps1"" -InstallRoot ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "FrpDeskServices"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if RegGetSubkeyNames(HKLM, 'SYSTEM\CurrentControlSet\Services', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('FrpDesk-Instance-', Names[I]) = 1 then
      begin
        Result := '检测到仍在注册的 FrpDesk 后台服务。请先在 FrpDesk 中关闭所有实例的后台服务，再重新安装。';
        Exit;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  LegacyRoot: String;
  RunValue: String;
  OldRunValue: String;
begin
  if CurStep = ssPostInstall then
  begin
    if (not Exec('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Verify-Release.ps1') +
      '" -PackageRoot "' + ExpandConstant('{app}') + '"',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
    begin
      MsgBox('安装文件校验失败，安装已中止。请重新下载安装包。', mbError, MB_OK);
      Abort;
    end;

    LegacyRoot := ExpandConstant('{localappdata}\Programs\FrpDesk');
    if (not SameText(LegacyRoot, ExpandConstant('{app}'))) and DirExists(LegacyRoot) then
      DelTree(LegacyRoot, True, True, False);

    DeleteFile(ExpandConstant('{userprograms}\FrpDesk.lnk'));
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\FrpDesk');
    RunValue := '"' + ExpandConstant('{app}\{#AppExeName}') + '" --minimized';
    if WizardIsTaskSelected('autostart') or
      RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FrpDesk', OldRunValue) then
      RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'FrpDesk', RunValue);
  end;
end;
