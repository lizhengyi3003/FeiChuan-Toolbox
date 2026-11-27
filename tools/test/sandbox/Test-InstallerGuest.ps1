$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path 'C:\TestScripts\run.ps1')){throw '仅允许项目 Windows Sandbox。'}
$result=[ordered]@{Time=(Get-Date).ToString('o');Checks=@()}
$install=Join-Path $env:LOCALAPPDATA 'Toolbox installed QA'
try{
    # C:\TestScripts is a Windows Sandbox mapped folder served by the host. Inno Setup could not
    # create its extraction directory when started from that mapping (error 5), so run a local
    # copy, which is also how a downloaded installer is normally started.
    $media=Join-Path $env:LOCALAPPDATA 'Toolbox setup media'
    New-Item -ItemType Directory -Path $media -Force | Out-Null
    $data=Join-Path $env:LOCALAPPDATA 'Toolbox custom data QA'
    $localSetup=Join-Path $media 'Toolbox-Setup.exe'
    Copy-Item -LiteralPath 'C:\TestScripts\Toolbox-Setup.exe' -Destination $localSetup -Force
    $result.SetupFromMapping='C:\TestScripts\Toolbox-Setup.exe'
    $result.SetupLocalCopy=$localSetup
    $result.Checks+='安装包复制到本地磁盘后运行'
    $setup=Start-Process -FilePath $localSetup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',('/DIR="'+$install+'"'),('/DATADIR="'+$data+'"'),'/LOG="C:\TestResults\installer-install.log"' -PassThru -Wait
    $result.SetupExitCode=$setup.ExitCode
    if($setup.ExitCode -ne 0){throw ('安装程序退出码不正确：'+$setup.ExitCode)}
    if(-not(Test-Path (Join-Path $install 'Engine\TunHotspot.Engine.exe'))){throw '安装后引擎不存在。'}
    $result.Version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'TunHotspot.exe')).ProductVersion
    $expected=Get-Content -LiteralPath 'C:\Application\release.json' -Raw | ConvertFrom-Json
    if($result.Version.Split('+')[0] -ne $expected.version){throw '安装后版本不正确。'}
    if(Get-Process TunHotspot -ErrorAction SilentlyContinue){throw '安装器自动启动了工具箱。'}
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pointer=Join-Path $install ('user-data-'+$sid+'.path')
    if([IO.File]::ReadAllText($pointer) -ne $data){throw '自定义数据路径未登记。'}
    $result.Checks+='程序和数据安装到各自含空格的自定义路径，工具箱没有自动启动'
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
    $qaRoot=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$ui.Id)
    Copy-Item -LiteralPath (Join-Path $qaRoot 'qa\entries.png') -Destination 'C:\TestResults\installed-entries.png' -Force
    $result.Checks+='安装后启动并渲染实际 XAML'
    # Seed real guest-user history so uninstall retention is an actual check, not a QA-path assumption.
    $seed=Start-Process -FilePath (Join-Path $install 'Engine\TunHotspot.Engine.exe') -ArgumentList '--refresh-user-data' -PassThru -WindowStyle Hidden -RedirectStandardOutput 'C:\TestResults\retained-snapshot.json' -RedirectStandardError 'C:\TestResults\retained-scan.txt'
    if(-not $seed.WaitForExit(90000)){throw '建立隔离来宾历史超时。'}
    $userDatabase=Join-Path $data 'toolbox.db'
    $userHash=(Get-FileHash -LiteralPath $userDatabase).Hash
    $uninstallKey='Software\Microsoft\Windows\CurrentVersion\Uninstall\{8EE42691-81B4-4A6F-A115-9961835E1D45}_is1'
    $userRegistration='HKCU:\'+$uninstallKey
    $machineRegistration='HKLM:\'+$uninstallKey
    if(-not(Test-Path -LiteralPath $userRegistration)){throw '缺少当前用户安装登记。'}
    $allUsers=Start-Process -FilePath $localSetup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/ALLUSERS','/LOG="C:\TestResults\installer-all-users.log"' -PassThru -Wait
    $result.AllUsersExitCode=$allUsers.ExitCode
    if($allUsers.ExitCode -ne 0){throw ('所有用户安装退出码：'+$allUsers.ExitCode)}
    if(-not (Select-String -LiteralPath 'C:\TestResults\installer-all-users.log' -Pattern 'Original-account verification result: 0' -SimpleMatch -Quiet)){throw '缺少原始账户退出码核对成功的记录。'}
    $machineEntry=Get-ItemProperty -LiteralPath $machineRegistration
    if(Test-Path -LiteralPath $userRegistration){throw '同目录升级后仍有重复的当前用户安装登记。'}
    if($machineEntry.InstallLocation.TrimEnd('\') -ne $install -or (Get-FileHash -LiteralPath $userDatabase).Hash -ne $userHash){throw '所有用户升级的安装位置或数据有变化。'}
    if(Get-Process TunHotspot -ErrorAction SilentlyContinue){throw '所有用户安装自动启动了工具箱。'}
    $result.Checks+='Inno 原生所有用户模式接替旧当前用户登记并保留自选数据'
    $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $run -Force | Out-Null
    New-ItemProperty -LiteralPath $run -Name 'TunHotspot.Toolbox.Agent' -Value ('"'+(Join-Path $install 'TunHotspot.exe')+'" --background') -PropertyType String -Force | Out-Null
    $uninstall=$machineEntry.UninstallString.Trim('"')
    $remove=Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG="C:\TestResults\installer-remove.log"' -PassThru -Wait
    $result.ExecutableRemoved=-not(Test-Path (Join-Path $install 'TunHotspot.exe'))
    $result.MachineRegistrationRemoved=-not(Test-Path -LiteralPath $machineRegistration)
    $result.OwnRunRemoved=-not [bool](Get-ItemProperty -LiteralPath $run -Name 'TunHotspot.Toolbox.Agent' -ErrorAction SilentlyContinue)
    $result.UserDataKept=(Test-Path -LiteralPath $userDatabase) -and (Get-FileHash -LiteralPath $userDatabase).Hash -eq $userHash
    if(-not $result.ExecutableRemoved -or -not $result.OwnRunRemoved -or -not $result.UserDataKept -or -not $result.MachineRegistrationRemoved){throw '卸载后文件、登录入口或数据保留状态不正确。'}
    $result.Checks+='卸载移除自身入口与程序，保留历史数据'
    $result.Success=$true
}catch{$result.Success=$false;$result.Error=$_.Exception.Message}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath 'C:\TestResults\installer-result.json' -Encoding utf8
