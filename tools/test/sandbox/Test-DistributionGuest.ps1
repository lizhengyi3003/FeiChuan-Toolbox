param([string]$ApplicationFolder='C:\Application',[string]$OutputFolder='C:\TestResults')
$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path -LiteralPath 'C:\TestScripts\run.ps1')){throw '仅在项目 Windows Sandbox 运行。'}
New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
$os=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$result=[ordered]@{Success=$false;Time=(Get-Date).ToString('o');OS=$os.ProductName;Build=$os.CurrentBuildNumber;SDKRequired=$false;CodexRequired=$false}
try{
    $copy=Join-Path $env:LOCALAPPDATA 'Toolbox portability test'
    New-Item -ItemType Directory -Path $copy -Force | Out-Null
    Copy-Item -Path (Join-Path $ApplicationFolder '*') -Destination $copy -Recurse -Force
    $result.FilesCopied=@(Get-ChildItem -LiteralPath $copy -Recurse -File).Count
    $result.DotnetInstalled=[bool](Get-Command dotnet.exe -ErrorAction SilentlyContinue)
    $result.CodexInstalled=[bool](Get-Command codex.exe -ErrorAction SilentlyContinue)
    $engine=Join-Path $copy 'Engine\TunHotspot.Engine.exe'
    $scanOutput=Join-Path $OutputFolder 'guest-scan.json'
    $scanError=Join-Path $OutputFolder 'guest-scan-progress.txt'
    $scan=Start-Process -FilePath $engine -ArgumentList '--diagnostic-scan' -WorkingDirectory (Split-Path -Parent $engine) -RedirectStandardOutput $scanOutput -RedirectStandardError $scanError -PassThru -WindowStyle Hidden
    if(-not $scan.WaitForExit(90000)){throw '干净系统采集超过 90 秒。'}
    $scan.Refresh();$result.ScanExit=$scan.ExitCode
    $data=Get-Content -LiteralPath $scanOutput -Raw -Encoding utf8 | ConvertFrom-Json
    if($data.protocol -ne 1 -or @($data.entries).Count -lt 10){throw '采集结果为空或协议不匹配。'}
    $result.Entries=@($data.entries).Count;$result.Privilege=$data.privilege;$result.Coverage=$data.coverage | Select-Object provider,status,count
    $preview=Start-Process -FilePath (Join-Path $copy 'TunHotspot.exe') -ArgumentList '--qa-capture','overview' -WorkingDirectory $copy -PassThru -WindowStyle Hidden
    $result.UiExited=$preview.WaitForExit(45000)
    $qaRoot=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$preview.Id)
    $image=Join-Path $qaRoot 'qa\overview.png'
    $result.UiImage=Test-Path -LiteralPath $image
    if($result.UiImage){Copy-Item -LiteralPath $image -Destination (Join-Path $OutputFolder 'guest-overview.png') -Force}
    $log=Join-Path $qaRoot 'legacy-operations\operations.jsonl'
    if(Test-Path -LiteralPath $log){Copy-Item -LiteralPath $log -Destination (Join-Path $OutputFolder 'guest-ui-operations.jsonl') -Force}
    if(-not $result.UiExited -or -not $result.UiImage){throw '实际隔离 XAML 截图缺失或程序未按时退出。'}
    $result.Success=$true
}catch{$result.Error=$_.Exception.Message}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputFolder 'result.json') -Encoding utf8
