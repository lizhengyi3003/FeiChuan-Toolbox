param([string]$SetupPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $SetupPath){$SetupPath=(Get-ChildItem "$root\artifacts\installer\*-Setup-x64.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName}
if(-not $SetupPath){throw '请先生成安装包。'}
$registryPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8EE42691-81B4-4A6F-A115-9961835E1D45}_is1'
if(Test-Path -LiteralPath $registryPath){throw '已安装正式版本，跳过测试以免覆盖现有安装。'}
if(Get-Process TunHotspot -ErrorAction SilentlyContinue){throw '请先手动关闭热点助手窗口，热点状态会保留。'}
$testId='installer-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$testRoot=Join-Path $root "artifacts\qa\$testId"
$installDirectory=Join-Path $testRoot 'installed'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$installExit=$null; $uninstallExit=$null; $fileCount=0; $mismatches=@()
$stateFile=Join-Path $env:LOCALAPPDATA 'TunHotspot.WinUI\state.json'
$stateBefore=if(Test-Path -LiteralPath $stateFile){(Get-FileHash -LiteralPath $stateFile).Hash}else{$null}
$desktopLink=Join-Path ([Environment]::GetFolderPath('Desktop')) 'TUN 热点助手.lnk'
$shortcutBefore=if(Test-Path -LiteralPath $desktopLink){(Get-FileHash -LiteralPath $desktopLink).Hash}else{$null}
try {
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=""','/LANG=chinesesimplified',('/DIR="'+$installDirectory+'"'),('/LOG="'+(Join-Path $testRoot 'install.log')+'"'))
    $process=Start-Process -FilePath $SetupPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $installExit=$process.ExitCode
    if($installExit -ne 0){throw "测试安装失败：$installExit"}
    $published=Join-Path $root 'artifacts\win-x64'
    foreach($file in Get-ChildItem -LiteralPath $published -File -Recurse | Where-Object Extension -ne '.pdb'){
        $relative=[IO.Path]::GetRelativePath($published,$file.FullName)
        $installed=Join-Path $installDirectory $relative
        $fileCount++
        if(-not(Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $installed).Hash){$mismatches+=$relative}
    }
    if($mismatches.Count){throw "安装文件缺失或哈希不符：$($mismatches -join ', ')"}
    if(-not(Test-Path -LiteralPath $registryPath)){throw '缺少当前用户卸载入口。'}
    if(-not(Test-Path -LiteralPath (Join-Path $installDirectory '使用说明.txt'))){throw '缺少安装后的使用说明。'}
} finally {
    $uninstaller=Join-Path $installDirectory 'unins000.exe'
    if(Test-Path -LiteralPath $uninstaller){
        $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $testRoot 'uninstall.log')+'"'))
        $process=Start-Process -FilePath $uninstaller -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        $uninstallExit=$process.ExitCode
    }
    $stateAfter=if(Test-Path -LiteralPath $stateFile){(Get-FileHash -LiteralPath $stateFile).Hash}else{$null}
    $shortcutAfter=if(Test-Path -LiteralPath $desktopLink){(Get-FileHash -LiteralPath $desktopLink).Hash}else{$null}
    $result=[ordered]@{time=[DateTimeOffset]::Now.ToString('o');setup=$SetupPath;installExit=$installExit;verifiedRuntimeFiles=$fileCount;hashMismatches=$mismatches;uninstallExit=$uninstallExit;uninstallEntryRemoved=(-not(Test-Path -LiteralPath $registryPath));testApplicationRemoved=(-not(Test-Path -LiteralPath (Join-Path $installDirectory 'TunHotspot.exe')));stateUnchanged=($stateBefore -eq $stateAfter);desktopShortcutUnchanged=($shortcutBefore -eq $shortcutAfter)}
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'result.json') -Encoding utf8
    $result | ConvertTo-Json -Depth 4
}
if($uninstallExit -ne 0 -or -not $result.uninstallEntryRemoved -or -not $result.testApplicationRemoved -or -not $result.stateUnchanged -or -not $result.desktopShortcutUnchanged){throw "安装往返检查未通过，见 $testRoot"}
