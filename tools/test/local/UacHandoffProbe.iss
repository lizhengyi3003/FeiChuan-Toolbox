; No file copy, uninstall registration, services or application launch.
; Both parent and child abort before installation. Native path uses product code.
#define ProjectRoot AddBackslash(SourcePath) + "..\..\.."
[Setup]
AppId=TunHotspot.UacHandoff.ReadOnlyProbe
AppName=工具箱管理员授权检查（只读）
AppVersion=1.0.0
DefaultDirName={tmp}\Toolbox-Uac-Probe
PrivilegesRequired=lowest
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
var OriginalSid: String; RestartRequested: Boolean;
#include ProjectRoot + "\installer\Elevation.iss"

function InitializeSetup: Boolean;
begin
  Result := True;
  if ExpandConstant('{param:ELEVATEDRETRY|0}') = '1' then
  begin
    Log('UAC_PROBE_CHILD Admin=' + IntToStr(Ord(IsAdmin)) + ' Dir=' + ExpandConstant('{param:DIR|}') +
      ' OriginalSid=' + ExpandConstant('{param:ORIGSID|}'));
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  OriginalSid := ExpandConstant('{param:ORIGSID|}');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var ErrorCode: Integer; Launched: Boolean;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Result := False;
  Log('UAC_PROBE_PARENT Admin=' + IntToStr(Ord(IsAdmin)) + ' Page=' + IntToStr(CurPageID));
  try
    Log('UAC_PROBE_DIRECTORY_READY ' + ExpandConstant('{app}'));
    if ExpandConstant('{param:METHOD|check}') = 'legacy' then
    begin
      Launched := ShellExec('runas',ExpandConstant('{srcexe}'),'/ELEVATEDRETRY=1','',SW_SHOWNORMAL,ewNoWait,ErrorCode);
      Log('UAC_PROBE_LEGACY Success=' + IntToStr(Ord(Launched)) + ' Error=' + IntToStr(ErrorCode));
    end
    else if ExpandConstant('{param:METHOD|check}') = 'native' then
    begin
      Launched := RestartElevated;
      Log('UAC_PROBE_NATIVE Success=' + IntToStr(Ord(Launched)));
    end
    else Log('UAC_PROBE_CHECK_ONLY No elevation requested');
  except
    Log('UAC_PROBE_ERROR ' + GetExceptionMessage);
  end;
  RestartRequested := True;
  WizardForm.Close;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := 'Read-only probe: installation is never allowed.';
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  Cancel := True; Confirm := False;
end;
