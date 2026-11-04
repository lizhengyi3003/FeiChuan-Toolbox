{ Shared by the installer and the read-only UAC handoff regression probe. }
function NativeShellExecute(Window: THandle; Verb, Filename, Parameters, Directory: String;
  ShowCmd: Integer): NativeInt;
  external 'ShellExecuteW@shell32.dll stdcall';

function RestartElevated: Boolean;
var Parameters, Detail: String; ShellResult: NativeInt; ErrorCode: Integer;
begin
  Result := False;
  if IsAdmin or WizardSilent or (ExpandConstant('{param:ELEVATEDRETRY|0}') = '1') then Exit;
  Parameters := '/ELEVATEDRETRY=1 /DIR=' + AddQuotes(ExpandConstant('{app}')) +
    ' /GROUP=' + AddQuotes(WizardGroupValue) + ' /LANG=' + ExpandConstant('{language}') +
    ' /TASKS=' + AddQuotes(WizardSelectedTasks(False)) + ' /ORIGSID=' + AddQuotes(OriginalSid);
  { Inno ShellExec refuses to execute Setup itself before installation starts.
    Request the standard Windows UAC flow directly, without bypassing consent. }
  Log('Requesting Windows elevation for: ' + ExpandConstant('{srcexe}'));
  ShellResult := NativeShellExecute(WizardForm.Handle, 'runas', ExpandConstant('{srcexe}'),
    Parameters, ExpandConstant('{src}'), SW_SHOWNORMAL);
  ErrorCode := DLLGetLastError;
  Log('Windows elevation launch result ' + IntToStr(ShellResult) + ', error ' + IntToStr(ErrorCode));
  if ShellResult > 32 then
  begin
    RestartRequested := True;
    WizardForm.Close;
    Result := True;
  end
  else
  begin
    if ErrorCode = 0 then ErrorCode := ShellResult;
    if ErrorCode = 1223 then Detail := '已取消 Windows 管理员授权，可以重新点击“下一步”或选择用户可写目录。'
    else Detail := '无法启动管理员安装。Windows 错误 ' + IntToStr(ErrorCode) + '：' + SysErrorMessage(ErrorCode) +
      '' + #13#10 + '可以重试，或右键此安装包选择“以管理员身份运行”。';
    Log(Detail);
    SuppressibleMsgBox(Detail + #13#10 + '安装日志：' + ExpandConstant('{log}'), mbError, MB_OK, IDOK);
  end;
end;
