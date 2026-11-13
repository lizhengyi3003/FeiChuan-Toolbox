param(
    [string]$CompilerPath,
    [switch]$Publish,
    [switch]$Offline,
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$SignToolPath,
    [string]$SigningThumbprint,
    [switch]$RequireTrustedSignature
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'tools\common\Artifacts.ps1')
$layout=Get-ArtifactLayout $root
$signing = $RequireTrustedSignature -or $SignToolPath -or $SigningThumbprint
if ($signing) {
    if (-not $SignToolPath -or -not $SigningThumbprint) { throw '签名构建需要 SignToolPath 和 SigningThumbprint。' }
    if ($SigningThumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'SigningThumbprint 必须是 40 位十六进制证书指纹。' }
    $signTool = Get-Item -LiteralPath $SignToolPath
    if ($signTool -isnot [IO.FileInfo] -or $signTool.Name -ine 'signtool.exe' -or $signTool.FullName -match '[\$%]') {
        throw '请指定 Windows SDK 的 signtool.exe。'
    }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningThumbprint" -ErrorAction SilentlyContinue
    $usage = $certificate.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] }
    if (-not $certificate -or -not $certificate.HasPrivateKey -or
        $usage.EnhancedKeyUsages.Value -notcontains '1.3.6.1.5.5.7.3.3') {
        throw '当前用户证书库没有对应的代码签名证书和私钥。'
    }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        if (-not $chain.Build($certificate) -or $chain.ChainElements.Count -lt 2) {
            throw '代码签名证书需要由 Windows 信任的证书颁发机构签发。'
        }
    }
    finally { $chain.Dispose() }
}
if(-not $PublishDirectory){$PublishDirectory=$layout.PreviewApp}
$PublishDirectory=[IO.Path]::GetFullPath($PublishDirectory)
if($Publish){& (Join-Path $root 'build.ps1') -Publish -Offline:$Offline -OutputDirectory $PublishDirectory}
if(-not $CompilerPath){
    $candidates=@((Join-Path $root '.tools\inno-build-tools\compiler\ISCC.exe'),'C:\Program Files\Inno Setup 7\ISCC.exe','C:\Program Files (x86)\Inno Setup 7\ISCC.exe')
    $CompilerPath=$candidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
}
if(-not $CompilerPath){throw '请先运行 tools\build\Initialize-InstallerTools.ps1，或用 -CompilerPath 指定 Inno Setup 7 的 ISCC.exe。'}
foreach($file in @('TunHotspot.exe','TunHotspot.pri','Assets\TunHotspot.ico','Engine\TunHotspot.Engine.exe','files.sha256')){
    if(-not(Test-Path -LiteralPath (Join-Path $PublishDirectory $file))){throw "发布文件缺失：$file。请先运行 build.ps1 -Publish。"}
}
[xml]$project=Get-Content -LiteralPath "$root\Directory.Build.props"
$version=[string]$project.Project.PropertyGroup.Version
$names=Get-DistributionNames -RepositoryRoot $root -Version $version
foreach($binary in @('TunHotspot.exe','TunHotspot.Core.dll','Engine\TunHotspot.Engine.exe','Engine\TunHotspot.Core.dll')){
    $actual=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PublishDirectory $binary)).ProductVersion.Split('+')[0]
    if($actual -ne $version){throw "发布目录版本不一致：$binary 为 $actual，预期 $version。"}
}
if(-not $OutputDirectory){$OutputDirectory=$layout.PreviewPackages}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$coordinatorFile=Join-Path $root 'installer\Upgrade-Coordinator.ps1'
$coordinatorBytes=[IO.File]::ReadAllBytes($coordinatorFile)
if($coordinatorBytes.Length -lt 3 -or $coordinatorBytes[0] -ne 239 -or $coordinatorBytes[1] -ne 187 -or $coordinatorBytes[2] -ne 191){throw '升级协调器必须为 UTF-8 BOM，以兼容内置 Windows PowerShell 5.1。'}
$coordinatorHash=(Get-FileHash -LiteralPath $coordinatorFile).Hash.ToLowerInvariant()
$signingArgs = @()
if ($signing) {
    $signedCache = Join-Path $layout.Temp ('signed-uninstallers-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($signedCache) | Out-Null
    $command = '$q' + $signTool.FullName + '$q sign /sha1 ' + $SigningThumbprint +
        ' /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f'
    $signingArgs = @("/DAuthenticodeSigning=1", "/DSignedUninstallerDirectory=$signedCache", "--signtool=FeiChuanAuthenticode=$command")
}
& $CompilerPath '/Q' "--output-dir=$OutputDirectory" "/DAppVersion=$version" "/DDistributionPrefix=$($names.Prefix)" "/DPublishDir=$PublishDirectory" "/DCoordinatorSha256=$coordinatorHash" @signingArgs "$root\installer\TunHotspot.iss"
if($LASTEXITCODE){throw '安装包编译失败。'}
$setup=Join-Path $OutputDirectory $names.Installer
# Normalize only the installer produced by this build before hashing or publishing it.
Unblock-File -LiteralPath $setup
if ($signing) {
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SigningThumbprint -or
        -not $signature.TimeStamperCertificate) {
        throw "安装包签名、发布者或时间戳核验失败（$($signature.Status)）。"
    }
}
$hash=Get-FileHash -LiteralPath $setup -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding utf8
Write-Output "安装包：$setup"
