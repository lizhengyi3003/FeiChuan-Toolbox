param([string]$ApplicationFolder='C:\Application',[string]$OutputFolder='C:\TestResults')
$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path 'C:\TestScripts\run.ps1')){throw '仅在专用 Windows Sandbox 操作测试项。'}
$result=[ordered]@{Time=(Get-Date).ToString('o');Checks=@()}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$engine=Join-Path $ApplicationFolder 'Engine\TunHotspot.Engine.exe'
$run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approved='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$fixture='TunHotspot.Qa.Inert';$serviceName='TunHotspot.Qa.Control';$taskName='TunHotspot.Qa.Task'
function Read-Exact($pipe,[int]$count){$bytes=New-Object byte[] $count;$offset=0;while($offset -lt $count){$r=$pipe.ReadAsync($bytes,$offset,$count-$offset);if(-not $r.Wait(90000) -or $r.Result -eq 0){throw '控制管道超时或断开。'};$offset+=$r.Result};return ,$bytes}
function Receive($pipe){$head=Read-Exact $pipe 4;$n=[BitConverter]::ToInt32($head,0);if($n -lt 2 -or $n -gt 33554432){throw '消息越界。'};$data=Read-Exact $pipe $n;[Text.Encoding]::UTF8.GetString($data) | ConvertFrom-Json}
function Send($pipe,$control){$message=@{protocol=1;kind='controlRequest';control=$control};$bytes=[Text.Encoding]::UTF8.GetBytes(($message | ConvertTo-Json -Depth 25 -Compress));$head=[BitConverter]::GetBytes([int]$bytes.Length);$pipe.Write($head,0,4);$pipe.Write($bytes,0,$bytes.Length);$pipe.Flush()}
function Request($control){
    $name='TunHotspot.Scan.'+[guid]::NewGuid().ToString('N');$pipe=[IO.Pipes.NamedPipeServerStream]::new($name,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
    try{
        $wait=$pipe.WaitForConnectionAsync();$process=Start-Process -FilePath $engine -ArgumentList '--control','--pipe',$name,'--owner',$sid -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $OutputFolder 'control-server-error.txt')
        if(-not $wait.Wait(20000)){throw '控制引擎未连接。'};Send $pipe $control
        do{$response=Receive $pipe}while($response.kind -eq 'progress')
        if(-not $process.WaitForExit(30000)){throw '控制引擎未退出。'}
        return $response
    }finally{$pipe.Dispose()}
}
function Snapshot {
    $file=Join-Path $OutputFolder 'controls-snapshot.json'
    $p=Start-Process -FilePath $engine -ArgumentList '--qa-control-snapshot' -WindowStyle Hidden -PassThru -RedirectStandardOutput $file -RedirectStandardError (Join-Path $OutputFolder 'controls-snapshot-error.txt')
    if(-not $p.WaitForExit(60000)){throw '测试目标快照超时。'}
    return (Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json)
}
function Apply([string]$name,[string]$operation,[string]$value){
    $snapshot=Snapshot;$item=$snapshot.entries | Where-Object {$_.entry.name -eq $name} | Select-Object -First 1
    if(-not $item){throw ('缺少测试目标：'+$name)}
    $response=Request @{deviceId=$snapshot.deviceId;entryId=$item.entry.id;provider=$item.entry.provider;fingerprint=$item.fingerprint;operation=$operation;value=$value}
    if($response.kind -eq 'error'){throw $response.text};$record=$response.controls[0]
    if($record.status -ne '已应用'){throw ($record.status+': '+$record.error)}
    return $record
}
function Restore($record){
    $response=Request @{deviceId=$record.deviceId;operation='restore';restoreId=$record.id}
    if($response.kind -eq 'error'){throw $response.text}
    if($response.controls[0].status -ne '已恢复'){throw $response.controls[0].error}
    return $response.controls[0]
}
try{
    New-Item -Path $run,$approved -Force | Out-Null
    New-ItemProperty -LiteralPath $run -Name $fixture -Value 'C:\Windows\System32\cmd.exe /c exit 0' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $approved -Name $fixture -Value ([byte[]](2,0,0,0,0,0,0,0,0,0,0,0)) -PropertyType Binary -Force | Out-Null
    $r=Apply $fixture 'setEnabled' 'false'
    if((Get-ItemPropertyValue -LiteralPath $approved -Name $fixture)[0] -ne 3){throw '登录入口禁用未落地。'}
    Restore $r | Out-Null
    if((Get-ItemPropertyValue -LiteralPath $approved -Name $fixture)[0] -ne 2){throw '登录入口恢复未落地。'}
    $result.Checks+='登录入口禁用、系统回读、原始二进制恢复'
    $machine32=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry32)
    $native64=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    $machineRun=$machine32.CreateSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Run')
    $machineApproval=$native64.CreateSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32')
    $machineFixture='TunHotspot.Qa.Machine32';$machineRun.SetValue($machineFixture,'C:\Windows\System32\cmd.exe /c exit 0')
    $r=Apply $machineFixture 'setEnabled' 'false'
    if($machineApproval.GetValue($machineFixture)[0] -ne 3){throw '32位机器入口未写入原生 Run32 状态。'}
    Restore $r | Out-Null;if($null -ne $machineApproval.GetValue($machineFixture)){throw '32位机器入口原始无批准记录未恢复。'}
    $result.Checks+='32 位机器登录入口使用原生 Run32，恢复原始缺省状态'
    $startup=Join-Path ([Environment]::GetFolderPath('Startup')) 'TunHotspot.Qa.Folder.cmd'
    Set-Content -LiteralPath $startup -Value '@exit /b 0' -Encoding ascii
    $r=Apply 'TunHotspot.Qa.Folder.cmd' 'setEnabled' 'false';Restore $r | Out-Null
    $result.Checks+='启动文件夹禁用和恢复（未运行测试文件）'
    $schedule=New-Object -ComObject Schedule.Service;$schedule.Connect();$taskRoot=$schedule.GetFolder('\')
    try{$taskFolder=$taskRoot.GetFolder('TunHotspotQa')}catch{$taskFolder=$taskRoot.CreateFolder('TunHotspotQa')}
    $definition=$schedule.NewTask(0);$definition.Settings.Enabled=$true;$definition.Settings.AllowDemandStart=$true;$definition.RegistrationInfo.Description='Disposable toolbox control fixture, never run.'
    $definition.Principal.UserId=$sid;$definition.Principal.LogonType=3;$action=$definition.Actions.Create(0);$action.Path='C:\Windows\System32\cmd.exe';$action.Arguments='/c exit 0'
    $taskFolder.RegisterTaskDefinition($taskName,$definition,6,$sid,$null,3) | Out-Null
    $r=Apply $taskName 'setEnabled' 'false';if($taskFolder.GetTask($taskName).Enabled){throw '任务禁用未落地。'}
    Restore $r | Out-Null;if(-not $taskFolder.GetTask($taskName).Enabled){throw '任务恢复未落地。'}
    $result.Checks+='第三方计划任务禁用和恢复（未触发任务）'
    $protected=Join-Path $env:ProgramFiles 'TunHotspotQaControls\Engine';New-Item -ItemType Directory -Path $protected -Force | Out-Null
    Copy-Item -Path (Join-Path $ApplicationFolder 'Engine\*') -Destination $protected -Recurse -Force
    $binary='"'+(Join-Path $protected 'TunHotspot.Engine.exe')+'" --qa-control-service'
    New-Service -Name $serviceName -BinaryPathName $binary -StartupType Manual | Out-Null
    $r=Apply $serviceName 'serviceMode' 'auto';if((Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName) -Name Start) -ne 2){throw '服务自动模式未落地。'}
    Restore $r | Out-Null;if((Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName) -Name Start) -ne 3){throw '服务启动类型未恢复。'}
    $result.Checks+='服务启动类型修改和恢复'
    $delayed=Apply $serviceName 'serviceMode' 'delayed-auto'
    if((Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName) -Name DelayedAutoStart) -ne 1){throw '延迟自动模式未落地。'}
    $normal=Apply $serviceName 'serviceMode' 'auto'
    if((Get-ItemPropertyValue -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName) -Name DelayedAutoStart) -ne 0){throw '普通自动模式未清除延迟。'}
    Restore $normal | Out-Null;Restore $delayed | Out-Null
    $result.Checks+='延迟自动、普通自动切换及恢复'
    $started=Apply $serviceName 'serviceStart' '';if((Get-Service $serviceName).Status -ne 'Running'){throw '服务未启动。'}
    $stopped=Apply $serviceName 'serviceStop' '';if((Get-Service $serviceName).Status -ne 'Stopped'){throw '服务未停止。'}
    Restore $stopped | Out-Null;Restore $started | Out-Null
    $result.Checks+='服务实际启动、停止及恢复（仅沙盒空服务）'
    $snapshot=Snapshot;$item=$snapshot.entries | Where-Object {$_.entry.name -eq $fixture} | Select-Object -First 1
    Set-ItemProperty -LiteralPath $run -Name $fixture -Value 'C:\Windows\System32\cmd.exe /c exit 1'
    $rejected=Request @{deviceId=$snapshot.deviceId;entryId=$item.entry.id;provider=$item.entry.provider;fingerprint=$item.fingerprint;operation='setEnabled';value='false'}
    if($rejected.kind -ne 'error'){throw '未拒绝过期修改预览。'}
    $result.Checks+='配置变化后拒绝过期修改请求'
    $r=Apply $fixture 'setEnabled' 'false'
    Set-ItemProperty -LiteralPath $approved -Name $fixture -Value ([byte[]](2,0,0,0,0,0,0,0,0,0,0,0))
    $rejected=Request @{deviceId=$r.deviceId;operation='restore';restoreId=$r.id}
    if($rejected.kind -ne 'error'){throw '未拒绝覆盖外部后续变化。'}
    $result.Checks+='恢复前拒绝覆盖后续变化'
    $history=Request @{deviceId=$r.deviceId;operation='history'}
    if($history.kind -eq 'error' -or @($history.controls).Count -lt 8){throw '受控恢复历史缺失。'}
    $result.ProtectedRecords=@($history.controls).Count;$result.BackupOwner=(Get-Acl -LiteralPath (Join-Path $env:ProgramData ('TunHotspot\Actions\'+$sid))).Owner
    $result.Checks+='管理员受控备份与历史同步';$result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
finally{
    if($machineRun){$machineRun.DeleteValue($machineFixture,$false);$machineRun.Dispose()};if($machineApproval){$machineApproval.DeleteValue($machineFixture,$false);$machineApproval.Dispose()};if($machine32){$machine32.Dispose()};if($native64){$native64.Dispose()}
    if($startup -and (Test-Path -LiteralPath $startup)){Remove-Item -LiteralPath $startup}
    foreach($path in @($run,$approved)){Remove-ItemProperty -LiteralPath $path -Name $fixture -ErrorAction SilentlyContinue}
    if($taskFolder){try{$taskFolder.DeleteTask($taskName,0)}catch{}}
    if(Get-Service $serviceName -ErrorAction SilentlyContinue){Stop-Service $serviceName -ErrorAction SilentlyContinue;& "$env:WINDIR\System32\sc.exe" delete $serviceName | Out-Null}
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputFolder 'controls-result.json') -Encoding utf8
}
