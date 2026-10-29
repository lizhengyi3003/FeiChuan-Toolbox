param([string]$ApplicationFolder='C:\Application',[string]$OutputFolder='C:\TestResults',[switch]$KeepComponent,[switch]$SkipRepeatedStateTest)
$ErrorActionPreference='Stop'
# This script refuses the host and runs only in the dedicated disposable Sandbox.
if(-not (Test-Path -LiteralPath 'C:\TestScripts\run.ps1') -or $env:USERNAME -ne 'WDAGUtilityAccount'){throw '仅允许在本项目 Windows Sandbox 测试环境执行。'}
$result=[ordered]@{Time=(Get-Date).ToString('o');Account=$env:USERNAME;Checks=@()}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$engine=Join-Path $ApplicationFolder 'Engine\TunHotspot.Engine.exe'
function Read-Exact($pipe,[int]$count){
    $bytes=New-Object byte[] $count;$offset=0
    while($offset -lt $count){$read=$pipe.ReadAsync($bytes,$offset,$count-$offset);if(-not $read.Wait(30000)){throw '管道读取超时。'};if($read.Result -eq 0){throw '采集管道已关闭。'};$offset+=$read.Result}
    return ,$bytes
}
function Receive-Message($pipe){
    $header=Read-Exact $pipe 4;$length=[BitConverter]::ToInt32($header,0)
    if($length -lt 2 -or $length -gt 33554432){throw '消息长度无效。'}
    $data=Read-Exact $pipe $length
    return ([Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json)
}
function Send-Message($pipe,$message){
    $message.protocol=1;$data=[Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Depth 20 -Compress))
    $header=[BitConverter]::GetBytes([int]$data.Length);$pipe.Write($header,0,4);$pipe.Write($data,0,$data.Length);$pipe.Flush()
}
function Service-Request($message){
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','TunHotspot.StartupMonitor.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Impersonation)
    try{$pipe.Connect(4000);Send-Message $pipe $message;$response=Receive-Message $pipe;if($response.kind -eq 'error'){throw $response.text};return $response}finally{$pipe.Dispose()}
}
function Broker-Install {
    $name='TunHotspot.Scan.'+[guid]::NewGuid().ToString('N')
    $pipe=[IO.Pipes.NamedPipeServerStream]::new($name,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
    try{
        $wait=$pipe.WaitForConnectionAsync()
        $process=Start-Process -FilePath $engine -ArgumentList '--install','--pipe',$name,'--owner',$sid -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $OutputFolder 'component-install-error.txt')
        if(-not $wait.Wait(20000)){throw '安装代理未连接。'}
        do{$message=Receive-Message $pipe;if($message.kind -eq 'error'){throw $message.text}}while($message.kind -ne 'complete')
        if(-not $process.WaitForExit(30000)){throw '安装代理未退出。'}
        $result.Checks+='管理员代理固定操作与账户核实'
    }finally{$pipe.Dispose()}
}
try{
    Broker-Install
    $service=Get-Service TunHotspotStartupMonitor
    if($service.Status -ne 'Running'){throw '机器服务没有进入 Running。'}
    $result.Checks+='SCM 机器服务启动'
    $deadline=(Get-Date).AddSeconds(60)
    do{$response=Service-Request @{kind='status'};if($response.snapshot){break};Start-Sleep -Milliseconds 500}while((Get-Date) -lt $deadline)
    if(-not $response.snapshot){throw '机器首次快照未完成。'}
    $result.Entries=@($response.snapshot.entries).Count;$result.Privilege=$response.snapshot.privilege;$result.MonitorState=$response.monitorState
    if($result.Privilege -ne 'LocalSystem' -or $result.Entries -lt 100){throw '快照权限或内容不符合预期。'}
    $result.Checks+='LocalSystem 全局快照与已授权账户 IPC'
    $first=$response.snapshot.entries | Where-Object {$_.enabled -ne $null} | Select-Object -First 1
    if($first){
        $expected=if($first.enabled){'启用'}else{'禁用'}
        Service-Request @{kind='watch';rule=@{entryId=$first.id;expected=$expected}} | Out-Null
        Service-Request @{kind='unwatch';text=$first.id} | Out-Null
        $result.Checks+='关注规则 IPC（未修改系统配置）'
    }
    Service-Request @{kind='pause';text='true'} | Out-Null
    Service-Request @{kind='pause';text='false'} | Out-Null
    $result.Checks+='暂停与恢复本软件监测'
    $initialId=$response.snapshot.id
    Service-Request @{kind='scan'} | Out-Null
    $deadline=(Get-Date).AddSeconds(60)
    do{$response=Service-Request @{kind='status'};if($response.progress){$result.SawProgress=$true};if($response.snapshot.id -ne $initialId){break};Start-Sleep -Milliseconds 400}while((Get-Date) -lt $deadline)
    if($response.snapshot.id -eq $initialId){throw '手动全局扫描没有产生新快照。'}
    $result.Checks+='手动扫描与阶段进度状态'
    $eventProcess=Start-Process -FilePath "$env:WINDIR\System32\cmd.exe" -ArgumentList '/c','exit','0' -WindowStyle Hidden -PassThru
    $eventProcess.WaitForExit();Start-Sleep -Seconds 2
    $response=Service-Request @{kind='status'}
    $result.ProcessEvents=@($response.events | Where-Object {$_.kind -eq '进程创建' -and $_.pid -eq $eventProcess.Id}).Count
    $result.Gaps=@($response.events | Where-Object {$_.kind -eq '监测缺口'} | Select-Object name,detail)
    if($result.ProcessEvents -lt 1){throw 'ETW 未记录受控子进程事件。'}
    $result.Checks+='持续 ETW 进程创建记录'
    if(-not $SkipRepeatedStateTest){
    $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';$approved='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    New-Item -Path $run,$approved -Force | Out-Null
    New-ItemProperty -LiteralPath $approved -Name 'TunHotspot.Qa.Inert' -Value ([byte[]](3,0,0,0,0,0,0,0,0,0,0,0)) -PropertyType Binary -Force | Out-Null
    New-ItemProperty -LiteralPath $run -Name 'TunHotspot.Qa.Inert' -Value 'C:\Windows\System32\cmd.exe /c exit 0' -PropertyType String -Force | Out-Null
    Service-Request @{kind='scan'} | Out-Null
    $deadline=(Get-Date).AddSeconds(40)
    do{$response=Service-Request @{kind='status'};$fixture=$response.snapshot.entries | Where-Object {$_.name -eq 'TunHotspot.Qa.Inert'} | Select-Object -First 1;if($fixture -and $fixture.enabled -eq $false){break};Start-Sleep -Milliseconds 500}while((Get-Date) -lt $deadline)
    if(-not $fixture -or $fixture.enabled -ne $false){throw '未采集到沙盒禁用测试入口。'}
    Service-Request @{kind='watch';rule=@{entryId=$fixture.id;expected='禁用'}} | Out-Null
    for($cycle=1;$cycle -le 2;$cycle++){
        New-ItemProperty -LiteralPath $approved -Name 'TunHotspot.Qa.Inert' -Value ([byte[]](2,0,0,0,0,0,0,0,0,0,0,0)) -PropertyType Binary -Force | Out-Null
        $deadline=(Get-Date).AddSeconds(40)
        do{$response=Service-Request @{kind='status'};$alerts=@($response.alerts | Where-Object {$_.entryId -eq $fixture.id});if($alerts.Count -ge $cycle -and @($alerts | Where-Object {-not $_.resolved}).Count -gt 0){break};Start-Sleep -Milliseconds 500}while((Get-Date) -lt $deadline)
        if($alerts.Count -lt $cycle){throw '连续状态变化没有产生对应的独立告警。'}
        New-ItemProperty -LiteralPath $approved -Name 'TunHotspot.Qa.Inert' -Value ([byte[]](3,0,0,0,0,0,0,0,0,0,0,0)) -PropertyType Binary -Force | Out-Null
        $deadline=(Get-Date).AddSeconds(40)
        do{$response=Service-Request @{kind='status'};$active=@($response.alerts | Where-Object {$_.entryId -eq $fixture.id -and -not $_.resolved});if($active.Count -eq 0){break};Start-Sleep -Milliseconds 500}while((Get-Date) -lt $deadline)
        if($active.Count -gt 0){throw '恢复期望状态未结案。'}
    }
    Service-Request @{kind='unwatch';text=$fixture.id} | Out-Null
    $result.RepeatedStateAlerts=$alerts.Count;$result.Checks+='注册表连续订阅、偏离提醒、恢复与再次偏离（仅沙盒测试入口）'
    }
    $result.ComponentOwner=(Get-Acl -LiteralPath "$env:ProgramFiles\TunHotspot\Monitor").Owner
    $result.Success=$true
}catch{$result.Error=$_.Exception.Message;$result.Success=$false}
finally{
    foreach($path in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run')){Remove-ItemProperty -LiteralPath $path -Name 'TunHotspot.Qa.Inert' -ErrorAction SilentlyContinue}
    if(-not $KeepComponent){
        try{$remove=Start-Process -FilePath $engine -ArgumentList '--remove-component' -WindowStyle Hidden -Wait -PassThru -RedirectStandardError (Join-Path $OutputFolder 'component-remove-error.txt');$result.Removed=-not [bool](Get-Service TunHotspotStartupMonitor -ErrorAction SilentlyContinue)}catch{$result.RemoveError=$_.Exception.Message}
    }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputFolder 'component-result.json') -Encoding utf8
}
