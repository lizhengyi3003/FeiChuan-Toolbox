param([switch]$RemoveComponent)
$ErrorActionPreference='Stop'
if(-not [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem -or -not(Test-Path 'C:\TestScripts\run.ps1')){throw '只允许隔离沙盒中的 SYSTEM 测试账户。'}
function Read-Exact($pipe,[int]$count){$bytes=New-Object byte[] $count;$offset=0;while($offset -lt $count){$r=$pipe.ReadAsync($bytes,$offset,$count-$offset);if(-not $r.Wait(10000) -or $r.Result -eq 0){throw 'IPC 读取失败。'};$offset+=$r.Result};return ,$bytes}
$result=@{Time=(Get-Date).ToString('o');Success=$false}
try{
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.','TunHotspot.StartupMonitor.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous,[Security.Principal.TokenImpersonationLevel]::Impersonation)
    # The service rejects the actual connected SID before it reads any request, then sends a typed error.
    $pipe.Connect(5000)
    $head=Read-Exact $pipe 4;$bytes=Read-Exact $pipe ([BitConverter]::ToInt32($head,0));$message=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if($message.kind -ne 'error' -or $message.snapshot){throw '未拒绝与已授权 owner 不符的身份。'}
    $result.Success=$true;$result.Check='即使管道 ACL 允许 SYSTEM 连接，非 owner 请求仍被实际账户校验拒绝。'
}catch{$result.Error=$_.Exception.Message}
finally{if($pipe){$pipe.Dispose()};if($RemoveComponent){& 'C:\Application\Engine\TunHotspot.Engine.exe' --remove-component;$result.Removed=-not [bool](Get-Service TunHotspotStartupMonitor -ErrorAction SilentlyContinue)};$result | ConvertTo-Json | Set-Content -LiteralPath 'C:\TestResults\service-boundary-result.json' -Encoding utf8}
