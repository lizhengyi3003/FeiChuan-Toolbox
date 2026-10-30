$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path 'C:\TestScripts\run.ps1')){throw '仅允许项目 Windows Sandbox。'}
$result=[ordered]@{Time=(Get-Date).ToString('o');Checks=@()}
$install=Join-Path $env:LOCALAPPDATA 'Toolbox installed QA'
try{
    $setup=Start-Process -FilePath 'C:\TestScripts\Toolbox-Setup.exe' -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="'+$install+'"'),'/LOG="C:\TestResults\installer-install.log"' -PassThru -Wait
    if(-not(Test-Path (Join-Path $install 'Engine\TunHotspot.Engine.exe'))){throw '安装后引擎不存在。'}
    $result.Version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'TunHotspot.exe')).ProductVersion
    $expected=Get-Content -LiteralPath 'C:\Application\release.json' -Raw | ConvertFrom-Json
    if($result.Version.Split('+')[0] -ne $expected.version){throw '安装后版本不正确。'}
    $result.Checks+='当前用户安装到自定义含空格路径'
    $checked=0
    foreach($line in Get-Content -LiteralPath (Join-Path $install 'files.sha256')){
        if($line -notmatch '^([a-f0-9]{64})  (.+)$'){throw '校验清单格式无效。'}
        $hash=$matches[1];$file=Join-Path $install $matches[2]
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash){throw ('安装文件校验不匹配：'+$matches[2])}
        $checked++
    }
    $result.FilesVerified=$checked;$result.Checks+='安装后完整文件校验'
    $ui=Start-Process -FilePath (Join-Path $install 'TunHotspot.exe') -ArgumentList '--qa-capture','entries' -WindowStyle Hidden -PassThru
    if(-not $ui.WaitForExit(30000)){throw '安装后 UI 验证超时。'}
    Copy-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'TunHotspot.Toolbox\qa\entries.png') -Destination 'C:\TestResults\installed-entries.png' -Force
    $result.Checks+='安装后启动并渲染实际 XAML'
    $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $run -Force | Out-Null
    New-ItemProperty -LiteralPath $run -Name 'TunHotspot.Toolbox.Agent' -Value ('"'+(Join-Path $install 'TunHotspot.exe')+'" --background') -PropertyType String -Force | Out-Null
    $uninstall=Get-ChildItem -LiteralPath $install -File -Filter 'unins*.exe' | Select-Object -First 1
    $remove=Start-Process -FilePath $uninstall.FullName -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG="C:\TestResults\installer-remove.log"' -PassThru -Wait
    $result.ExecutableRemoved=-not(Test-Path (Join-Path $install 'TunHotspot.exe'))
    $result.OwnRunRemoved=-not [bool](Get-ItemProperty -LiteralPath $run -Name 'TunHotspot.Toolbox.Agent' -ErrorAction SilentlyContinue)
    $result.UserDataKept=Test-Path (Join-Path $env:LOCALAPPDATA 'TunHotspot.Toolbox\toolbox.db')
    if(-not $result.ExecutableRemoved -or -not $result.OwnRunRemoved -or -not $result.UserDataKept){throw '卸载后文件、登录入口或数据保留状态不正确。'}
    $result.Checks+='卸载移除自身入口与程序，保留历史数据'
    $result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath 'C:\TestResults\installer-result.json' -Encoding utf8
