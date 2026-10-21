#ifndef AppVersion
  #define AppVersion "0.1.8"
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
Source: "Stop-Foreground.ps1"; DestDir: "{tmp}"; Flags: dontcopy

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
  ResultCode: Integer;
  StopScript: String;
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

  // 旧版被关闭后，它启动的前台 frpc 会成为孤儿进程并锁住 assets\frpc.exe。
  // 只结束安装目录内的 FrpDesk.exe 和 frpc.exe，避免升级时 DeleteFile 报拒绝访问。
  ExtractTemporaryFile('Stop-Foreground.ps1');
  StopScript := ExpandConstant('{tmp}\Stop-Foreground.ps1');
  if (not Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -File "' + StopScript + '" -InstallRoot "' + ExpandConstant('{app}') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode)) or (ResultCode <> 0) then
  begin
    Result := '无法关闭仍在运行的 FrpDesk 或 frpc。请从系统托盘退出 FrpDesk，并停止正在运行的实例，然后重试安装。';
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
