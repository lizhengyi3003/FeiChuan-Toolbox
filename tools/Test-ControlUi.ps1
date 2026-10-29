param([Parameter(Mandatory=$true)][string]$ApplicationPath,[string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;using System.Runtime.InteropServices;
public static class ToolboxControlsWindow {
 public delegate bool EnumProc(IntPtr w,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb,IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 public static IntPtr Find(int pid){IntPtr found=IntPtr.Zero;EnumWindows((w,v)=>{uint p;GetWindowThreadProcessId(w,out p);if(p==pid){found=w;return false;}return true;},IntPtr.Zero);return found;}
}
"@
$result=[ordered]@{Time=(Get-Date).ToString('o');Framework='WinUI 3';Action='Only preview and cancel'}
function Named($root,[string]$name){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))}
try{
 $process=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture','controls-interactive' -WindowStyle Hidden -PassThru
 $deadline=(Get-Date).AddSeconds(12);$button=$null
 do{
  Start-Sleep -Milliseconds 200;$handle=[ToolboxControlsWindow]::Find($process.Id)
  if($handle -ne [IntPtr]::Zero){$root=[Windows.Automation.AutomationElement]::FromHandle($handle);$button=Named $root '应用启动类型'}
 }while((-not $button -or -not $button.Current.IsEnabled) -and (Get-Date) -lt $deadline)
 if(-not $button -or -not $button.Current.IsEnabled){throw '未显示可操作的服务启动类型按钮。'}
 $result.StartButton=[bool](Named $root '启动服务');$result.StopButton=[bool](Named $root '停止服务')
 if(-not $result.StartButton -or -not $result.StopButton){throw '服务实际启停按钮缺失。'}
 $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 500
 $confirm=Named $root '确认并申请管理员授权';$cancel=Named $root '取消'
 if(-not $confirm -or -not $cancel -or -not $cancel.Current.IsEnabled){throw '未显示 WinUI 修改预览和授权/取消按钮。'}
 $result.PreviewVisible=$true
 $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 200
 if(Named $root '确认并申请管理员授权'){throw '取消后预览未关闭。'}
 $result.Cancelled=$true
 if(-not $process.WaitForExit(30000)){throw 'QA 窗口未正常退出。'}
 $result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
if($OutputPath){$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8}
$result | ConvertTo-Json -Depth 5
