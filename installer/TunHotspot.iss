#ifndef AppVersion
  #error 请通过 tools/build/Build-Installer.ps1 提供统一的 AppVersion
#endif
#ifndef CoordinatorSha256
  #error 请通过 Build-Installer.ps1 固定安装协调脚本的 SHA-256
#endif
#define ProjectRoot AddBackslash(SourcePath) + ".."
#ifndef PublishDir
  #define PublishDir ProjectRoot + "\artifacts\temp\preview\app"
#endif

[Setup]
AppId={{8EE42691-81B4-4A6F-A115-9961835E1D45}
AppName=飞船的小工具箱
AppVersion={#AppVersion}
AppVerName=飞船的小工具箱 {#AppVersion}
AppPublisher=飞船的小工具箱
VersionInfoDescription=飞船的小工具箱安装程序
DefaultDirName={localappdata}\Programs\TunHotspot
DefaultGroupName=飞船的小工具箱
UsePreviousGroup=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
DisableDirPage=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
MinVersion=10.0.22000
OutputDir={#ProjectRoot}\artifacts\temp\preview\packages
OutputBaseFilename=飞船的小工具箱-{#AppVersion}-Setup-x64
SetupIconFile={#ProjectRoot}\src\TunHotspot.WinUI\Assets\TunHotspot.ico
UninstallDisplayIcon={app}\TunHotspot.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=110
; Per-target mutex is acquired in PrepareToInstall after permission checks.
SetupLogging=yes
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
Source: "{#ProjectRoot}\installer\Upgrade-Coordinator.ps1"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\飞船的小工具箱"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; AppUserModelID: "TunHotspot.WinUI"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\飞船的小工具箱"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "TunHotspot.WinUI"

[Run]
Filename: "{app}\TunHotspot.exe"; Description: "启动 飞船的小工具箱"; Flags: postinstall nowait skipifsilent unchecked; Check: CanLaunchNormally

[UninstallRun]
Filename: "{app}\TunHotspot.exe"; Parameters: "--uninstall-user-integration"; Flags: runhidden waituntilterminated; RunOnceId: "TunHotspot.UserIntegration.v1"

[Code]
var
  CoordinatorReady, Prepared, Committed, RecoveryComplete, RestartRequested: Boolean;
  CoordinatorPath, ResultPath, PayloadPath, OriginalSid, OperationPath, LastCoordMessage, InstallFailureMessage: String;
  InstallMutex: THandle;

function NativeGetCurrentProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function NativeCreateMutex(Security: NativeUInt; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function NativeReleaseMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function NativeCloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function NativeCreateFile(Name: String; Access, Sharing: LongWord; Security: NativeUInt;
  Disposition, Flags: LongWord; Template: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

function ReadUtf8(const Path: String): String;
var Raw: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(Path, Raw) then Result := UTF8Decode(Raw);
end;

function RunCoordinator(const Mode: String): Integer;
var Parameters, MessageText: String; ExitCode: Integer; ScriptLock: THandle;
begin
  Result := 99;
  if not CoordinatorReady then
  begin
    ExtractTemporaryFile('Upgrade-Coordinator.ps1');
    ExtractTemporaryFile('files.sha256');
    CoordinatorPath := ExpandConstant('{tmp}\Upgrade-Coordinator.ps1');
    PayloadPath := ExpandConstant('{tmp}\files.sha256');
    ResultPath := ExpandConstant('{tmp}\coordination-result.json');
    CoordinatorReady := True;
  end;
  if CompareText(GetSHA256OfFile(CoordinatorPath), '{#CoordinatorSha256}') <> 0 then
  begin LastCoordMessage := '安装协调文件校验失败，未替换程序。'; Exit; end;
  ScriptLock := NativeCreateFile(CoordinatorPath, $80000000, 1, 0, 3, 0, 0);
  if ScriptLock = THandle(-1) then
  begin LastCoordMessage := '不能锁定已核验的安装协调文件，未替换程序。'; Exit; end;
  try
    DeleteFile(ResultPath + '.message');
    DeleteFile(ResultPath + '.operation');
    DeleteFile(ResultPath + '.sid');
    DeleteFile(ResultPath + '.prepared');
    Parameters := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(CoordinatorPath) +
      ' -Action ' + Mode +
      ' -Manifest ' + AddQuotes(PayloadPath) + ' -ResultFile ' + AddQuotes(ResultPath) +
      ' -InstallerPid ' + IntToStr(NativeGetCurrentProcessId);
    if Mode <> 'Identity' then Parameters := Parameters + ' -Target ' + AddQuotes(ExpandConstant('{app}'));
    if OriginalSid <> '' then Parameters := Parameters + ' -OwnerSid ' + AddQuotes(OriginalSid);
    if OperationPath <> '' then Parameters := Parameters + ' -Operation ' + AddQuotes(OperationPath);
    if (not WizardSilent) or (ExpandConstant('{param:CLOSEOWNAPPS|0}') = '1') then
      Parameters := Parameters + ' -AllowExit';
    if (Mode = 'Verify') and (ExpandConstant('{param:QAVERIFYFAIL|0}') = '1') then
      Parameters := Parameters + ' -InjectVerificationFailure';
    Log('Installer coordination: ' + Mode);
    if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters,
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then Result := ExitCode;
    LastCoordMessage := ReadUtf8(ResultPath + '.message');
    MessageText := Trim(ReadUtf8(ResultPath + '.operation'));
    if MessageText <> '' then OperationPath := MessageText;
    if OriginalSid = '' then OriginalSid := Trim(ReadUtf8(ResultPath + '.sid'));
    if (Mode = 'Prepare') and (Trim(ReadUtf8(ResultPath + '.prepared')) = '1') then Prepared := True;
    Log('Installer coordination result ' + IntToStr(Result) + ': ' + LastCoordMessage);
    if LastCoordMessage = '' then LastCoordMessage :=
      '无法完成安装检查；未擅自跳过。请查看安装日志：' + ExpandConstant('{log}');
  finally
    NativeCloseHandle(ScriptLock);
  end;
end;

function CanLaunchNormally: Boolean;
begin
  { A runas restart cannot recover the original unelevated token. Use Explorer's shortcut instead. }
  Result := Committed and not IsAdmin;
end;

#include "Elevation.iss"

procedure InitializeWizard;
begin
  OriginalSid := ExpandConstant('{param:ORIGSID|}');
  if RunCoordinator('Identity') <> 0 then
    RaiseException(LastCoordMessage);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
  begin
    WizardForm.ReadyMemo.Lines.Add('');
    WizardForm.ReadyMemo.Lines.Add('安装将退出本目录下的工具箱界面、热点工作进程和 Codex 分析桥接；其他任务未完成时会暂停。先备份程序和安装登记，写入后逐文件核验，失败恢复安装前状态。');
    WizardForm.ReadyMemo.Lines.Add(LastCoordMessage);
  end;
  if (CurPageID = wpFinished) and Prepared and not Committed then
  begin
    WizardForm.FinishedHeadingLabel.Caption := '更新未完成';
    WizardForm.FinishedLabel.Caption := InstallFailureMessage;
    WizardForm.RunList.Visible := False;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if RestartRequested then begin Cancel := True; Confirm := False; end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var CheckResult: Integer;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  CheckResult := RunCoordinator('Inspect');
  if CheckResult = 0 then Exit;
  Result := False;
  if CheckResult = 10 then
  begin
    RestartElevated;
    Exit;
  end;
  SuppressibleMsgBox(LastCoordMessage, mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var MutexName: String; CheckResult: Integer;
begin
  Result := '';
  if RestartRequested then begin Result := '已转交管理员安装，当前窗口退出。'; Exit; end;
  CheckResult := RunCoordinator('Inspect');
  if CheckResult <> 0 then begin Result := LastCoordMessage; Exit; end;
  if InstallMutex = 0 then
  begin
    MutexName := 'Global\TunHotspot.Update.' + GetSHA256OfString(Lowercase(ExpandFileName(ExpandConstant('{app}'))));
    InstallMutex := NativeCreateMutex(0, True, MutexName);
    if (InstallMutex = 0) or (DLLGetLastError = 183) then
    begin
      if InstallMutex <> 0 then NativeCloseHandle(InstallMutex);
      InstallMutex := 0;
      Result := '另一安装/恢复正在使用此目录，尚未替换文件。'; Exit;
    end;
  end;
  if RunCoordinator('Prepare') <> 0 then Result := LastCoordMessage;
end;

procedure DeinitializeSetup;
begin
  if Prepared and not Committed and not RecoveryComplete then
  begin
    if RunCoordinator('Rollback') <> 0 then
      SuppressibleMsgBox('程序恢复未完整完成。请保留备份并使用其中 Restore.cmd 重试恢复：' + #13#10 +
        OperationPath + #13#10 + LastCoordMessage, mbError, MB_OK, IDOK);
  end;
  if InstallMutex <> 0 then begin NativeReleaseMutex(InstallMutex); NativeCloseHandle(InstallMutex); InstallMutex := 0; end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  { Inno may report normal completion after an ssPostInstall exception. }
  if Prepared and not Committed then Result := 9;
end;

procedure RemoveOwnedLegacyShortcut(const ShortcutPath, ExpectedTarget: String);
var
  Shell, Shortcut: Variant;
begin
  if not FileExists(ShortcutPath) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    if CompareText(Shortcut.TargetPath, ExpectedTarget) = 0 then
    begin
      if not DeleteFile(ShortcutPath) then Log('Could not remove the owned legacy shortcut: ' + ShortcutPath);
    end
    else Log('Kept a legacy-named shortcut with a different target: ' + ShortcutPath);
  except
    Log('Legacy shortcut ownership could not be verified; shortcut was preserved.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  OldGroup, AppTarget, GuideTarget: String;
  CheckResult: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  CheckResult := RunCoordinator('Verify');
  if CheckResult = 0 then CheckResult := RunCoordinator('Commit');
  if CheckResult <> 0 then
  begin
    InstallFailureMessage := LastCoordMessage;
    RecoveryComplete := RunCoordinator('Rollback') = 0;
    InstallFailureMessage := InstallFailureMessage + #13#10 + LastCoordMessage +
      '' + #13#10 + '备份与步骤日志：' + OperationPath;
    RaiseException(InstallFailureMessage);
  end;
  Committed := True;
  AppTarget := ExpandConstant('{app}\TunHotspot.exe');
  GuideTarget := ExpandConstant('{app}\使用说明.txt');
  RemoveOwnedLegacyShortcut(ExpandConstant('{userdesktop}\TUN 热点与启动工具箱.lnk'), AppTarget);
  RemoveOwnedLegacyShortcut(ExpandConstant('{userdesktop}\TUN 热点助手.lnk'), AppTarget);
  OldGroup := ExpandConstant('{userprograms}\TUN 热点与启动工具箱');
  RemoveOwnedLegacyShortcut(OldGroup + '\TUN 热点与启动工具箱.lnk', AppTarget);
  RemoveOwnedLegacyShortcut(OldGroup + '\使用说明.lnk', GuideTarget);
  RemoveDir(OldGroup); // Only succeeds for an empty directory; never recursive.
end;

function InitializeUninstall(): Boolean;
var
  ServicePath: String;
  ResultCode: Integer;
begin
  Result := True;
  if CheckForMutexes('TunHotspot.WinUI.Running') then
  begin
    SuppressibleMsgBox('请先从托盘退出工具箱后再卸载。', mbError, MB_OK, IDOK);
    Result := False; Exit;
  end;
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
