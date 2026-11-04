param([string]$ApplicationFolder='C:\Application',[string]$OutputFolder='C:\TestResults')
$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path 'C:\TestScripts\run.ps1')){throw '仅在项目 Windows Sandbox 运行。'}
$result=[ordered]@{Time=(Get-Date).ToString('o');Checks=@()}
try{
    $engine=Join-Path $ApplicationFolder 'Engine\TunHotspot.Engine.exe'
    $refresh=Start-Process -FilePath $engine -ArgumentList '--refresh-user-data' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $OutputFolder 'bridge-snapshot.json') -RedirectStandardError (Join-Path $OutputFolder 'bridge-scan-progress.txt')
    if(-not $refresh.WaitForExit(60000)){throw '建立用户快照超时。'}
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$engine;$start.Arguments='--mcp';$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8
    $process=[Diagnostics.Process]::Start($start)
    $writer=[IO.StreamWriter]::new($process.StandardInput.BaseStream,[Text.UTF8Encoding]::new($false));$writer.AutoFlush=$true
    $script:next=0
    function Request([string]$method,$parameters){
        $script:next++;$request=@{jsonrpc='2.0';id=$script:next;method=$method;params=$parameters}
        $writer.WriteLine(($request | ConvertTo-Json -Depth 25 -Compress))
        $read=$process.StandardOutput.ReadLineAsync();if(-not $read.Wait(10000)){throw 'MCP 响应超时。'}
        $response=$read.Result | ConvertFrom-Json;if($response.error){throw $response.error.message};return $response.result
    }
    $init=Request 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='Toolbox sandbox QA';version='1'}}
    if($init.protocolVersion -ne '2025-11-25'){throw 'MCP 协议版本不匹配。'}
    $tools=Request 'tools/list' @{};if(@($tools.tools).Count -ne 4){throw 'MCP 工具数不正确。'}
    $result.Checks+='MCP 初始化与四个受限工具'
    $bundleResponse=Request 'tools/call' @{name='startup_get_analysis_bundle';arguments=@{}}
    if($bundleResponse.isError){throw $bundleResponse.content[0].text}
    $batch=$bundleResponse.content[0].text | ConvertFrom-Json;$item=$batch.items[0]
    if(-not $item){throw '分析批次为空。'}
    $result.BatchCount=@($batch.items).Count;$result.Checks+='从待分析区生成有本机和指纹约束的批次'
    $evidence=Request 'tools/call' @{name='startup_get_evidence';arguments=@{batchId=$batch.id;entryId=$item.entryId}}
    if($evidence.isError){throw $evidence.content[0].text};$result.Checks+='批次内证据读取'
    $analysis=@{protocol=1;batchId=$batch.id;deviceId=$batch.deviceId;decisions=@(@{entryId=$item.entryId;fingerprint=$item.fingerprint;advice='待分析';reason='仅验证沙盒通信，尚未完成人工用途判断。';impact='建议回传不修改系统配置。';confidence='低'})}
    $saved=Request 'tools/call' @{name='startup_submit_analysis';arguments=@{result=$analysis}}
    if($saved.isError){throw $saved.content[0].text};$result.Checks+='建议成功回传（仍待分析，不执行配置修改）'
    $analysis.deviceId='other-device'
    $rejected=Request 'tools/call' @{name='startup_submit_analysis';arguments=@{result=$analysis}}
    if(-not $rejected.isError){throw '未拒绝跨机器结果。'};$result.Checks+='拒绝跨机器建议'
    $analysis.deviceId=$batch.deviceId;$analysis.execute='arbitrary command'
    $rejected=Request 'tools/call' @{name='startup_submit_analysis';arguments=@{result=$analysis}}
    if(-not $rejected.isError){throw '未拒绝未知执行字段。'};$result.Checks+='拒绝任意执行字段'
    $writer.Close();if(-not $process.WaitForExit(10000)){throw 'MCP 服务未正常退出。'}
    $result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
finally{if($process){try{$writer.Close();$process.StandardInput.Close();if($process.WaitForExit(2000)){$result.ServerError=$process.StandardError.ReadToEnd()}}catch{};$process.Dispose()};$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputFolder 'bridge-result.json') -Encoding utf8}
