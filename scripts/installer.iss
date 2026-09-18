; 片刻收纳 (Pivkey Organizer) Inno Setup 6 打包脚本
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "PivKeyUBox"
#define MyAppExeName "PivKeyUBox.exe"
#define MyAppPublisher "PivKeyU"
#define MyAppURL "https://github.com/PivKeyU/PivKeyUBox"
#define SourceDir "..\release\PivKeyUBox"

[Setup]
AppId={{9B820562-55AE-4997-B3A9-623C5E98F73A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={userpf}\{#MyAppName}
DisableProgramGroupPage=yes
; 采用当前用户模式安装，无需 UAC 管理员提权，便于后续平滑自动升级
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=PivKeyUBox-Setup-v{#MyAppVersion}
SetupIconFile=..\src\assets\icons\pivkey-organizer.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 自动关闭占用进程与服务
CloseApplications=yes
RestartApplications=no
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "开机自动启动 PivKeyUBox"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 开机自启动注册表项
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PivKeyUBox"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// 在安装前与卸载前强制关闭可能仍在后台的进程
procedure KillRunningProcesses();
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/f /im PivKeyUBox.exe /im PivkeyOrganizer.exe /im ShellMenu.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningProcesses();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    KillRunningProcesses();
  end;
end;
