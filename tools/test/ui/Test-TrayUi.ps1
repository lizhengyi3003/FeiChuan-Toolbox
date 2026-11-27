param([Parameter(Mandatory=$true)][string]$ApplicationPath,[string]$OutputPath,[string]$ScreenshotPath,[ValidateSet('Light','Dark')][string]$Theme='Light')
$ErrorActionPreference='Stop'
if([IO.Path]::GetFileName($ApplicationPath) -ne 'TunHotspot.exe'){throw '仅允许本软件 QA 进程。'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ToolboxTrayUiNative {
 public delegate bool EnumProc(IntPtr window,IntPtr value);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr value);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
 [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 public static void HideFixtureMainWindow(int process,IntPtr menu){EnumWindows((window,value)=>{uint owner;GetWindowThreadProcessId(window,out owner);var title=new StringBuilder(512);GetWindowText(window,title,512);if(owner==process&&window!=menu&&title.ToString()=="飞船的小工具箱")ShowWindow(window,0);return true;},IntPtr.Zero);}
 public static IntPtr Find(int process){IntPtr found=IntPtr.Zero;EnumWindows((window,value)=>{uint owner;GetWindowThreadProcessId(window,out owner);var title=new StringBuilder(512);GetWindowText(window,title,512);if(owner==process&&title.ToString()=="飞船的小工具箱 · 托盘菜单"){found=window;return false;}return true;},IntPtr.Zero);return found;}
}
'@
$result=[ordered]@{Time=(Get-Date).ToString('o');Framework='WinUI 3'}
$savedCursor=$null
try{
    $qaPage=if($Theme -eq 'Dark'){'tray-dark-interactive'}else{'tray-interactive'}
    $process=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture',$qaPage -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(10);$handle=[IntPtr]::Zero
    do{Start-Sleep -Milliseconds 150;$handle=[ToolboxTrayUiNative]::Find($process.Id)}while($handle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline)
    if($handle -eq [IntPtr]::Zero){throw '未找到 WinUI 3 托盘窗口。'}
    $settingsCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'TrayMenu.settings')
    $deadline=(Get-Date).AddSeconds(10);$settings=$null
    do{
        Start-Sleep -Milliseconds 150
        try{
            $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
            $settings=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$settingsCondition)
        }catch [Windows.Automation.ElementNotAvailableException]{}
    }while(-not $settings -and (Get-Date) -lt $deadline)
    if(-not $settings){throw '原生菜单未完成加载。'}
    $items=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::MenuItem))
    $names=@($items | ForEach-Object {$_.Current.Name})
    $result.Buttons=$names
    if(@($names | Where-Object {$_ -match '^(打开工具箱|兼容热点|Kumiryo 一键配置|全局扫描（管理员）|通知与关注.*|暂停监测|恢复监测|设置|退出托盘)$'}).Count -ne 8){throw '菜单内容缺失。'}
    $rect=$window.Current.BoundingRectangle
    foreach($item in $items){
        $bounds=$item.Current.BoundingRectangle
        if($bounds.Left -lt $rect.Left -or $bounds.Top -lt $rect.Top -or $bounds.Right -gt $rect.Right -or $bounds.Bottom -gt $rect.Bottom){throw '菜单项超出其自身窗口。'}
    }
    if($ScreenshotPath){
        [ToolboxTrayUiNative]::HideFixtureMainWindow($process.Id,$handle)
        $savedCursor=[ToolboxTrayUiNative+Point]::new()
        [void][ToolboxTrayUiNative]::GetCursorPos([ref]$savedCursor)
        $exit=($items | Where-Object {$_.Current.AutomationId -eq 'TrayMenu.exit'} | Select-Object -First 1).Current.BoundingRectangle
        [void][ToolboxTrayUiNative]::SetCursorPos([int]($exit.X+$exit.Width/2),[int]($exit.Y+$exit.Height/2))
    }
    Start-Sleep -Milliseconds 850
    $result.VisibleScrollbars=@($window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ScrollBar)) | Where-Object {-not $_.Current.IsOffscreen}).Count
    if($result.VisibleScrollbars -gt 0){throw '菜单仍显示滚动条。'}
    if($ScreenshotPath){
        $rect=$window.Current.BoundingRectangle
        $result.MenuBounds=$rect.ToString()
        $result.MenuWindowHandle=$window.Current.NativeWindowHandle
        if($rect.Width -lt 50 -or $rect.Height -lt 50){throw '菜单可见范围异常。'}
        $bounds=[System.Windows.Forms.Screen]::FromPoint([Drawing.Point]::new([int]$rect.X,[int]$rect.Y)).Bounds
        $left=[Math]::Max($bounds.Left,[int]$rect.X-24)
        $top=[Math]::Max($bounds.Top,[int]$rect.Y-12)
        $right=[Math]::Min($bounds.Right,[int]$rect.Right+24)
        $bottom=[Math]::Min($bounds.Bottom,[int]$rect.Bottom+100)
        $bitmap=New-Object Drawing.Bitmap ($right-$left),($bottom-$top)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try{$graphics.CopyFromScreen($left,$top,0,0,$bitmap.Size);$bitmap.Save($ScreenshotPath,[Drawing.Imaging.ImageFormat]::Png)}
        finally{$graphics.Dispose();$bitmap.Dispose()}
        $result.Screenshot=$ScreenshotPath
    }
    $settings.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if(-not $process.WaitForExit(10000)){throw '菜单点击后 QA 进程未正常结束。'}
    $log=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$process.Id+'\legacy-operations\operations.jsonl')
    $frame=Get-Content -LiteralPath $log | ForEach-Object {$_ | ConvertFrom-Json} |
        Where-Object action -eq 'tray-menu-opened' | Select-Object -Last 1
    $result.MenuFrameConfigured=$null -ne $frame -and $null -ne $frame.details.cornerResult -and
        [bool]$frame.details.frameRemoved -and $null -ne $frame.details.borderResult -and $frame.details.cornerResult -eq 0 -and $frame.details.borderResult -eq 0
    if(-not $result.MenuFrameConfigured){throw '系统未接受托盘圆角或无边框设置。'}
    $result.CallbackFound=[bool](Get-Content -LiteralPath $log -Tail 12 | Where-Object {$_ -match 'qa-tray-invoked' -and $_ -match 'settings'})
    if(-not $result.CallbackFound){throw '未收到真实 WinUI 菜单点击回调。'}
    $result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
finally{if($null -ne $savedCursor){[void][ToolboxTrayUiNative]::SetCursorPos($savedCursor.X,$savedCursor.Y)}}
if($OutputPath){$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8}
$result | ConvertTo-Json -Depth 5
