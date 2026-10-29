#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#define ProjectRoot AddBackslash(SourcePath) + ".."
#ifndef PublishDir
  #define PublishDir ProjectRoot + "\artifacts\toolbox-preview"
#endif

[Setup]
AppId={{8EE42691-81B4-4A6F-A115-9961835E1D45}
AppName=TUN 热点与启动工具箱
AppVersion={#AppVersion}
AppVerName=TUN 热点与启动工具箱 {#AppVersion}
AppPublisher=TunHotspot 项目
VersionInfoDescription=TUN 热点与启动工具箱安装程序
DefaultDirName={localappdata}\Programs\TunHotspot
DefaultGroupName=TUN 热点与启动工具箱
DisableProgramGroupPage=yes
AllowNoIcons=yes
DisableDirPage=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0.22000
OutputDir={#ProjectRoot}\artifacts\installer
OutputBaseFilename=TunHotspot-{#AppVersion}-Setup-x64
SetupIconFile={#ProjectRoot}\src\TunHotspot.WinUI\Assets\TunHotspot.ico
UninstallDisplayIcon={app}\TunHotspot.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=110
AppMutex=TunHotspot.WinUI.Running
SetupMutex=TunHotspot.WinUI.Setup
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
UninstallRestartComputer=no
ChangesEnvironment=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TUN 热点与启动工具箱"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; AppUserModelID: "TunHotspot.WinUI"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\TUN 热点与启动工具箱"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "TunHotspot.WinUI"

[Run]
Filename: "{app}\TunHotspot.exe"; Description: "启动 TUN 热点与启动工具箱"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{app}\TunHotspot.exe"; Parameters: "--uninstall-user-integration"; Flags: runhidden waituntilterminated; RunOnceId: "TunHotspot.UserIntegration.v1"

[Code]
function InitializeUninstall(): Boolean;
var
  ServicePath: String;
  ResultCode: Integer;
begin
  Result := True;
  if RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\TunHotspotStartupMonitor', 'ImagePath', ServicePath) then
  begin
    if UninstallSilent then
    begin
      Log('机器采集服务仍存在；静默卸载未擅自请求提升权限。请先在软件中卸载采集组件。');
      Result := False;
      Exit;
    end;
    if MsgBox('本软件的机器采集组件仍在运行。卸载它需要一次 Windows 管理员授权；历史数据会保留。是否继续？', mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;
    Result := ShellExec('runas', ExpandConstant('{app}\Engine\TunHotspot.Engine.exe'), '--remove-component', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
    if not Result then MsgBox('未能卸载机器采集组件，已取消软件卸载。可先在设置页卸载组件后重试。', mbError, MB_OK);
  end;
end;
