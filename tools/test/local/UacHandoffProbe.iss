; Verify native Inno install-mode selection without installing any files.
#define ProjectRoot AddBackslash(SourcePath) + "..\..\.."
[Setup]
AppId=TunHotspot.UacHandoff.ReadOnlyProbe
AppName=工具箱管理员授权检查（只读）
AppVersion=1.0.0
DefaultDirName={tmp}\Toolbox-Uac-Probe
#include ProjectRoot + "\installer\inno\InstallMode.iss"
SetupArchitecture=x64
Uninstallable=no
CreateAppDir=yes
DisableDirPage=no
DisableWelcomePage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
OutputBaseFilename=UacHandoffProbe-x64
SetupLogging=yes
[Code]
function InitializeSetup: Boolean;
begin
  Log('UAC_PROBE Admin=' + IntToStr(Ord(IsAdmin)) +
    ' AllUsers=' + IntToStr(Ord(IsAdminInstallMode)));
  Result := False;
end;
