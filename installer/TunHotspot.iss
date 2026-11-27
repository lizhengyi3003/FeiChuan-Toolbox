#ifndef AppVersion
  #error 请通过 tools/build/Build-Installer.ps1 提供统一的 AppVersion
#endif
#ifndef CoordinatorSha256
  #error 请通过 Build-Installer.ps1 固定安装协调脚本的 SHA-256
#endif
#ifndef DistributionPrefix
  #error 请通过 Build-Installer.ps1 提供统一的 DistributionPrefix
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
DefaultDirName={code:DefaultInstallDirectory}
DefaultGroupName=飞船的小工具箱
UsePreviousGroup=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
DisableDirPage=no
#include "inno\InstallMode.iss"
SetupArchitecture=x64
#ifdef AuthenticodeSigning
SignTool=FeiChuanAuthenticode
SignedUninstaller=yes
SignedUninstallerDir={#SignedUninstallerDirectory}
#endif
ArchitecturesAllowed=x64os
MinVersion=10.0.22000
OutputDir={#ProjectRoot}\artifacts\temp\preview\packages
OutputBaseFilename={#DistributionPrefix}-{#AppVersion}-Setup-x64
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
Source: "{#ProjectRoot}\installer\scripts\Upgrade-Coordinator.ps1"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\飞船的小工具箱"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; AppUserModelID: "TunHotspot.WinUI"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\飞船的小工具箱"; Filename: "{app}\TunHotspot.exe"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "TunHotspot.WinUI"

[UninstallRun]
Filename: "{app}\TunHotspot.exe"; Parameters: "--uninstall-user-integration"; Flags: runhidden waituntilterminated; RunOnceId: "TunHotspot.UserIntegration.v1"

[Code]
var
  CoordinatorReady, Prepared, Committed, RecoveryComplete: Boolean;
  CoordinatorPath, ResultPath, PayloadPath, OriginalSid, OperationPath, LastCoordMessage, InstallFailureMessage: String;
  InstallMutex: THandle;
  DataPage: TInputDirWizardPage;
  PreviousApp: String;
  CoordinationProgress: TOutputMarqueeProgressWizardPage;
  ShowingCoordinationProgress: Boolean;

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

procedure CoordinatorOutput(const Line: String; const Error, FirstLine: Boolean);
begin
  if Error or (Pos('INSTALL_STAGE:', Line) <> 1) then Exit;
  Log(Line);
  if not ShowingCoordinationProgress then Exit;
  if Line = 'INSTALL_STAGE:close' then
    CoordinationProgress.SetText('正在等待旧版本退出…', '')
  else if Line = 'INSTALL_STAGE:backup' then
    CoordinationProgress.SetText('正在备份安装文件…', '')
  else if Line = 'INSTALL_STAGE:verify' then
    CoordinationProgress.SetText('正在逐文件核验安装结果…', '')
  else if Line = 'INSTALL_STAGE:commit' then
    CoordinationProgress.SetText('正在保存安装结果…', '');
  CoordinationProgress.Animate;
end;

const
  ToolboxUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8EE42691-81B4-4A6F-A115-9961835E1D45}_is1';

function DefaultInstallDirectory(Param: String): String;
var PreviousDirectory: String;
begin
  if IsAdminInstallMode and RegQueryStringValue(HKCU64, ToolboxUninstallKey, 'InstallLocation', PreviousDirectory)
    and FileExists(AddBackslash(PreviousDirectory) + 'TunHotspot.exe') then
    Result := RemoveBackslashUnlessRoot(PreviousDirectory)
  else
    Result := ExpandConstant('{autopf}\TunHotspot');
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
    if Mode = 'VerifyOriginalAccount' then
    begin
      Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(CoordinatorPath) +
        ' -VerifyAccount -OwnerSid ' + AddQuotes(OriginalSid);
      Log('Verifying original Windows account using the child exit code');
      if ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters,
        ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
        Result := ExitCode
      else
        Log('Original-account verification could not start: ' + IntToStr(ExitCode));
      Log('Original-account verification result: ' + IntToStr(Result));
      Exit;
    end;
    DeleteFile(ResultPath + '.message');
    DeleteFile(ResultPath + '.operation');
    DeleteFile(ResultPath + '.sid');
    DeleteFile(ResultPath + '.prepared');
    Parameters := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(CoordinatorPath) +
      ' -Action ' + Mode +
      ' -Manifest ' + AddQuotes(PayloadPath) + ' -ResultFile ' + AddQuotes(ResultPath) +
      ' -InstallerPid ' + IntToStr(NativeGetCurrentProcessId);
    if Mode <> 'Identity' then Parameters := Parameters + ' -Target ' + AddQuotes(ExpandConstant('{app}'));
    if (Mode = 'InspectData') or (Mode = 'Prepare') then
      Parameters := Parameters + ' -DataDirectory ' + AddQuotes(RemoveBackslashUnlessRoot(Trim(DataPage.Values[0])));
    if OriginalSid <> '' then Parameters := Parameters + ' -OwnerSid ' + AddQuotes(OriginalSid);
    if IsAdminInstallMode and (Mode <> 'Identity') then Parameters := Parameters + ' -AdminInstallMode';
    if OperationPath <> '' then Parameters := Parameters + ' -Operation ' + AddQuotes(OperationPath);
    if (not WizardSilent) or (ExpandConstant('{param:CLOSEOWNAPPS|0}') = '1') then
      Parameters := Parameters + ' -AllowExit';
    if (Mode = 'VerifyAndCommit') and (ExpandConstant('{param:QAVERIFYFAIL|0}') = '1') then
      Parameters := Parameters + ' -InjectVerificationFailure';
    Log('Installer coordination: ' + Mode);
    if ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters,
      ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode, @CoordinatorOutput) then Result := ExitCode;
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

function RunWithProgress(const Mode, Status: String): Integer;
begin
  if WizardSilent then
  begin
    Result := RunCoordinator(Mode);
    Exit;
  end;
  CoordinationProgress.SetText(Status, '');
  CoordinationProgress.Show;
  ShowingCoordinationProgress := True;
  try
    CoordinationProgress.Animate;
    Result := RunCoordinator(Mode);
  finally
    ShowingCoordinationProgress := False;
    CoordinationProgress.Hide;
  end;
end;

function InitializeSetup: Boolean;
var CheckResult: Integer;
begin
  Result := False;
  if (RunCoordinator('Identity') <> 0) or (OriginalSid = '') then
  begin
    SuppressibleMsgBox(LastCoordMessage, mbError, MB_OK, IDOK);
    Exit;
  end;
  if IsAdminInstallMode then
  begin
    CheckResult := RunCoordinator('VerifyOriginalAccount');
    if CheckResult = 21 then
    begin
      SuppressibleMsgBox('授权账户与启动安装的 Windows 账户不同。请使用相同账户授权，或选择“仅为我安装”。', mbError, MB_OK, IDOK);
      Exit;
    end;
    if CheckResult <> 0 then
    begin
      SuppressibleMsgBox('无法完成 Windows 账户核对。安装日志：' + ExpandConstant('{log}'), mbError, MB_OK, IDOK);
      Exit;
    end;
  end;
  Result := True;
end;

procedure InitializeWizard;
begin
  CoordinationProgress := CreateOutputMarqueeProgressPage('正在处理安装', '当前步骤完成后将自动继续。');
  DataPage := CreateInputDirPage(wpSelectDir, '应用数据目录', '设置数据库与用户数据的位置',
    '默认使用安装目录旁的独立文件夹。也可以选择其他本地磁盘上的专用目录。', False, '');
  DataPage.Add('数据库、设置和日志');
end;

procedure UpdateDataDirectory;
var AppDirectory, Requested: String;
begin
  AppDirectory := ExpandFileName(ExpandConstant('{app}'));
  if CompareText(AppDirectory, PreviousApp) = 0 then Exit;
  PreviousApp := AppDirectory;
  Requested := ExpandConstant('{param:DATADIR|}');
  if Requested = '' then Requested := Trim(ReadUtf8(AppDirectory + '\user-data-' + OriginalSid + '.path'));
  if Requested = '' then Requested := AppDirectory + '.Data';
  DataPage.Values[0] := Requested;
end;


procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = DataPage.ID then UpdateDataDirectory;
  if CurPageID = wpReady then
  begin
    WizardForm.ReadyMemo.Lines.Add('');
    WizardForm.ReadyMemo.Lines.Add('安装将退出本目录下的工具箱界面、热点工作进程和 Codex 分析桥接；其他任务未完成时会暂停。先备份程序和安装登记，写入后逐文件核验，失败恢复安装前状态。');
    WizardForm.ReadyMemo.Lines.Add('应用数据：' + DataPage.Values[0]);
  end;
  if (CurPageID = wpFinished) and Prepared and not Committed then
  begin
    WizardForm.FinishedHeadingLabel.Caption := '更新未完成';
    WizardForm.FinishedLabel.Caption := InstallFailureMessage;
  end
  else if (CurPageID = wpFinished) and Committed then
  begin
    WizardForm.FinishedLabel.Caption := '安装已完成。可从开始菜单或桌面快捷方式打开工具箱。';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var CheckResult: Integer;
begin
  Result := True;
  if CurPageID = DataPage.ID then
  begin
    if Trim(DataPage.Values[0]) = '' then
    begin
      Result := False;
      MsgBox('请选择要保存应用数据的目录。', mbError, MB_OK);
      Exit;
    end;
    if RunWithProgress('InspectData', '正在检查应用数据目录…') <> 0 then
    begin
      Result := False;
      SuppressibleMsgBox(LastCoordMessage, mbError, MB_OK, IDOK);
    end;
    Exit;
  end;
  if CurPageID <> wpSelectDir then Exit;
  CheckResult := RunWithProgress('Inspect', '正在检查安装目录和写入权限…');
  if CheckResult = 0 then Exit;
  Result := False;
  if CheckResult = 10 then
    LastCoordMessage := LastCoordMessage + #13#10 +
      '请返回选择当前账户可写的目录，或关闭安装器后重新打开，在开头选择“为所有用户安装”。';
  SuppressibleMsgBox(LastCoordMessage, mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var MutexName: String;
begin
  Result := '';
  UpdateDataDirectory;
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
  if RunWithProgress('Prepare', '正在检查文件占用、关闭旧版本并备份安装文件…') <> 0 then
    Result := LastCoordMessage;
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
  CheckResult := RunWithProgress('VerifyAndCommit', '正在核验已安装的文件并完成安装…');
  if CheckResult <> 0 then
  begin
    InstallFailureMessage := LastCoordMessage;
    RecoveryComplete := RunWithProgress('Rollback', '正在恢复安装前的文件和设置…') = 0;
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
