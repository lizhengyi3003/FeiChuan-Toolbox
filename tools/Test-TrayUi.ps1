param([Parameter(Mandatory=$true)][string]$ApplicationPath,[string]$OutputPath)
$ErrorActionPreference='Stop'
if([IO.Path]::GetFileName($ApplicationPath) -ne 'TunHotspot.exe'){throw '仅允许本软件 QA 进程。'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ToolboxTrayUiNative {
 public delegate bool EnumProc(IntPtr window,IntPtr value);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr value);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
 public static IntPtr Find(int process){IntPtr found=IntPtr.Zero;EnumWindows((window,value)=>{uint owner;GetWindowThreadProcessId(window,out owner);var title=new StringBuilder(512);GetWindowText(window,title,512);if(owner==process&&title.ToString()=="TunHotspot 托盘菜单"){found=window;return false;}return true;},IntPtr.Zero);return found;}
}
'@
$result=[ordered]@{Time=(Get-Date).ToString('o');Framework='WinUI 3'}
try{
    $process=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture','tray-interactive' -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(10);$handle=[IntPtr]::Zero
    do{Start-Sleep -Milliseconds 150;$handle=[ToolboxTrayUiNative]::Find($process.Id)}while($handle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
    if($handle -eq [IntPtr]::Zero){throw '未找到 WinUI 3 托盘窗口。'}
    $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
    $items=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button))
    $names=@($items | ForEach-Object {$_.Current.Name})
    $result.Buttons=$names
    if(@($names | Where-Object {$_ -in @('打开工具箱','全局扫描（管理员）','通知与关注','暂停监测','恢复监测','设置','退出托盘')}).Count -ne 6){throw '菜单内容缺失。'}
    $settings=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'TrayMenu.settings'))
    $settings.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if(-not $process.WaitForExit(10000)){throw '菜单点击后 QA 进程未正常结束。'}
    $log=Join-Path $env:LOCALAPPDATA 'TunHotspot.WinUI\operations.jsonl'
    $result.CallbackFound=[bool](Get-Content -LiteralPath $log -Tail 12 | Where-Object {$_ -match 'qa-tray-invoked' -and $_ -match 'settings'})
    if(-not $result.CallbackFound){throw '未收到真实 WinUI Button 点击回调。'}
    $result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
if($OutputPath){$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8}
$result | ConvertTo-Json -Depth 5
