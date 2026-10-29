param([int]$ProcessId,[string]$SelectPage,[string]$InvokeName,[string]$InvokeId,[string]$ScreenshotPath,[string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ToolboxUiNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd,int mode);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd,IntPtr hdc,uint flags);
}
'@
$process=Get-Process -Id $ProcessId
if([IO.Path]::GetFileName($process.Path) -ne 'TunHotspot.exe'){throw 'UI 检查仅支持本软件进程。'}
[ToolboxUiNative]::ShowWindow($process.MainWindowHandle,9) | Out-Null
[ToolboxUiNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
$root=[Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if($SelectPage){
    $item=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$SelectPage))
    if(-not $item){throw "页面未找到：$SelectPage"}
    $pattern=$null
    if($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)){$pattern.Select()}
    elseif($item.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke()}
    else{throw '页面不支持选择或调用。'}
    Start-Sleep -Milliseconds 500
}
if($InvokeName){
    if($InvokeName -in @('开启兼容热点','关闭热点')){throw 'UI 验证不自动操作热点。'}
    $items=$root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$InvokeName))
    $item=$null
    foreach($candidate in $items){$candidatePattern=$null;if($candidate.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$candidatePattern)){$item=$candidate;break}}
    if(-not $item){throw "按钮未找到：$InvokeName"}
    $pattern=$item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern);$pattern.Invoke()
    Start-Sleep -Milliseconds 500
}
if($InvokeId){
    $item=$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$InvokeId))
    if(-not $item){throw "控件未找到：$InvokeId"}
    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
$rows=@($root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
    [pscustomobject]@{Name=$_.Current.Name;Id=$_.Current.AutomationId;Type=$_.Current.ControlType.ProgrammaticName;Enabled=$_.Current.IsEnabled;Offscreen=$_.Current.IsOffscreen}
})
if($OutputPath){$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8}
if($ScreenshotPath){
    $rect=$root.Current.BoundingRectangle
    $bitmap=New-Object Drawing.Bitmap ([int]$rect.Width),([int]$rect.Height)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{$hdc=$graphics.GetHdc();try{[ToolboxUiNative]::PrintWindow($process.MainWindowHandle,$hdc,2)|Out-Null}finally{$graphics.ReleaseHdc($hdc)};$bitmap.Save($ScreenshotPath,[Drawing.Imaging.ImageFormat]::Png)}
    finally{$graphics.Dispose();$bitmap.Dispose()}
}
$rows | Where-Object {$_.Name -and -not $_.Offscreen} | Select-Object -First 65
