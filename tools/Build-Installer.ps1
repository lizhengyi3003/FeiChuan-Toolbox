param([string]$CompilerPath,[switch]$Publish,[switch]$Offline,[string]$PublishDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $PublishDirectory){$PublishDirectory=Join-Path $root 'artifacts\toolbox-preview'}
$PublishDirectory=[IO.Path]::GetFullPath($PublishDirectory)
if($Publish){& (Join-Path $root 'build.ps1') -Publish -Offline:$Offline -OutputDirectory $PublishDirectory}
if(-not $CompilerPath){
    $candidates=@((Join-Path $root '.tools\inno-build-tools\compiler\ISCC.exe'),'C:\Program Files\Inno Setup 7\ISCC.exe','C:\Program Files (x86)\Inno Setup 7\ISCC.exe')
    $CompilerPath=$candidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
}
if(-not $CompilerPath){throw '请先运行 tools\Initialize-InstallerTools.ps1，或用 -CompilerPath 指定 Inno Setup 7 的 ISCC.exe。'}
foreach($file in @('TunHotspot.exe','TunHotspot.pri','Assets\TunHotspot.ico','Engine\TunHotspot.Engine.exe','files.sha256')){
    if(-not(Test-Path -LiteralPath (Join-Path $PublishDirectory $file))){throw "发布文件缺失：$file。请先运行 build.ps1 -Publish。"}
}
[xml]$project=Get-Content -LiteralPath "$root\src\TunHotspot.WinUI\TunHotspot.WinUI.csproj"
$version=[string]$project.Project.PropertyGroup.Version
& $CompilerPath '/Q' "/DAppVersion=$version" "/DPublishDir=$PublishDirectory" "$root\installer\TunHotspot.iss"
if($LASTEXITCODE){throw '安装包编译失败。'}
$setup=Join-Path $root "artifacts\installer\TunHotspot-$version-Setup-x64.exe"
$hash=Get-FileHash -LiteralPath $setup -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ascii
Write-Output "安装包：$setup"
