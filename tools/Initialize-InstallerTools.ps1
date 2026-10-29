param([string]$InstallerPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$cache=Join-Path $root '.tools\inno-build-tools'
$compilerDir=Join-Path $cache 'compiler'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if(-not $InstallerPath){
    $InstallerPath=Join-Path $cache 'innosetup-7.1.0-x64.exe'
    if(-not(Test-Path -LiteralPath $InstallerPath)){
        Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile "$InstallerPath.download"
        Move-Item -LiteralPath "$InstallerPath.download" -Destination $InstallerPath
    }
}
$signature=Get-AuthenticodeSignature -LiteralPath $InstallerPath
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.'){
    throw "Inno Setup 安装器签名无效或发布者不符，停止运行：$($signature.Status)"
}
$arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/PORTABLE=1','/NOICONS','/TASKS=',('/DIR="'+$compilerDir+'"'),('/LOG="'+(Join-Path $cache 'portable-extraction.log')+'"'))
$process=Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $compilerDir 'ISCC.exe'))){throw "Inno Setup 解包失败：$($process.ExitCode)"}
Write-Output "便携编译器已就绪：$compilerDir\ISCC.exe"
