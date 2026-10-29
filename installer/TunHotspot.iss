#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#define ProjectRoot AddBackslash(SourcePath) + ".."
#define PublishDir ProjectRoot + "\artifacts\win-x64"

[Setup]
AppId={{8EE42691-81B4-4A6F-A115-9961835E1D45}
AppName=TUN 热点助手
AppVersion={#AppVersion}
AppVerName=TUN 热点助手 {#AppVersion}
AppPublisher=TUN 热点助手项目
VersionInfoDescription=TUN 热点助手安装程序
DefaultDirName={localappdata}\Programs\TunHotspot
DefaultGroupName=TUN 热点助手
DisableProgramGroupPage=yes
AllowNoIcons=yes
DisableDirPage=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0.19041
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
Source: "{#ProjectRoot}\docs\使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\TUN 热点助手"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; AppUserModelID: "TunHotspot.WinUI"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\TUN 热点助手"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "TunHotspot.WinUI"

[Run]
Filename: "{app}\TunHotspot.exe"; Description: "启动 TUN 热点助手"; Flags: postinstall nowait skipifsilent unchecked
