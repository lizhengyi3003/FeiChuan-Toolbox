param([Parameter(Mandatory=$true)][string]$ApplicationPath,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if([IO.Path]::GetFileName($ApplicationPath) -ne 'TunHotspot.exe'){throw '仅验证本软件。'}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$result=[ordered]@{Time=(Get-Date).ToString('o');Success=$false}
$process=$null
try{
    $process=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture','startup-error' -PassThru
    $result.Pid=$process.Id
    $deadline=(Get-Date).AddSeconds(15);$root=$null
    do{
        Start-Sleep -Milliseconds 100;$process.Refresh()
        if($process.HasExited){throw '错误处理窗口出现前进程已经退出。'}
        if($process.MainWindowHandle -ne [IntPtr]::Zero){$root=[Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)}
    }while(($null -eq $root -or $root.Current.Name -notlike '*启动诊断*') -and (Get-Date) -lt $deadline)
    if($null -eq $root -or $root.Current.Name -notlike '*启动诊断*'){throw '未找到启动诊断窗口。'}
    $result.Title=$root.Current.Name
    $byId={param($id) $root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
    $details=$null
    do{Start-Sleep -Milliseconds 100;$details=& $byId 'LaunchFailure.Details'}while(-not $details -and (Get-Date) -lt $deadline)
    if(-not $details){throw '缺少错误详情。'}
    $errorText=$details.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if($errorText -notmatch 'SQLite Error 11' -or $errorText -notmatch 'QA'){throw '错误边界未显示指定测试异常。'}
    $result.DatabaseErrorShown=$true
    # Render the actual XAML tree so a locked desktop cannot be mistaken for the window.
    $capture=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$process.Id+'\qa\startup-error.png')
    do{Start-Sleep -Milliseconds 100;$image=Get-Item -LiteralPath $capture -ErrorAction SilentlyContinue}while((!$image -or $image.LastWriteTime -lt $process.StartTime) -and (Get-Date) -lt $deadline)
    if(!$image -or $image.LastWriteTime -lt $process.StartTime){throw '未生成本次实际 XAML 界面截图。'}
    Copy-Item -LiteralPath $capture -Destination (Join-Path $OutputDirectory 'startup-error.png') -Force
    $retry=& $byId 'LaunchFailure.Retry'
    if(-not $retry){throw '缺少重试按钮。'}
    $retry.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if(-not $process.WaitForExit(15000)){throw '重试后的 QA 窗口未完成。'}
    if($process.ExitCode -ne 0){throw '重试后进程异常退出。'}
    $fixture=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$process.Id+'\toolbox.db')
    if(-not(Test-Path -LiteralPath $fixture)){throw '重试没有创建隔离的 QA 数据库。'}
    $result.RetryLoadedIsolatedToolbox=$true
    $result.Success=$true
}catch{$result.Error=$_.Exception.Message}
finally{
    # Only this exact test process is stopped; installed UI and monitor are outside this test.
    if($process -and -not $process.HasExited){Stop-Process -Id $process.Id -Force}
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'startup-failure-ui.json') -Encoding utf8
}
$result | ConvertTo-Json -Depth 4
if(-not $result.Success){exit 1}
