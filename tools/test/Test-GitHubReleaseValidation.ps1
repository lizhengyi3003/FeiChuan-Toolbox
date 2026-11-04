param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
[xml]$project=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version=[string]$project.Project.PropertyGroup.Version
. (Join-Path $root 'tools\common\Artifacts.ps1')
$names=Get-DistributionNames -RepositoryRoot $root -Version $version
$folder=Join-Path $root ('artifacts\temp\release-validation-fixture-'+[Guid]::NewGuid().ToString('N'))
$packages=Join-Path $folder 'packages';[IO.Directory]::CreateDirectory($packages)|Out-Null
$sourceCommit='a'*40;$tag='v'+$version;$repository='lizhengyi3003/FeiChuan-Toolbox'
$base='https://github.com/'+$repository+'/releases/download/'+$tag+'/'
function Write-Json([string]$path,$value){$value|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $path -Encoding UTF8}
function Reject([scriptblock]$action,[string]$pattern){$rejected=$false;try{& $action|Out-Null}catch{if($_.Exception.Message -notlike $pattern){throw};$rejected=$true};if(-not $rejected){throw 'Invalid release fixture was accepted.'}}
$validator=Join-Path $root 'tools\build\Assert-GitHubRelease.ps1'
try{
    foreach($file in @($names.Installer,$names.Portable)){[IO.File]::WriteAllText((Join-Path $packages $file),'Non-executable release validation fixture.');$hash=(Get-FileHash -LiteralPath (Join-Path $packages $file)).Hash.ToLowerInvariant();($hash+'  '+$file)|Set-Content -LiteralPath (Join-Path $packages ($file+'.sha256')) -Encoding UTF8}
    $setupHash=(Get-FileHash -LiteralPath (Join-Path $packages $names.Installer)).Hash.ToLowerInvariant()
    $zipHash=(Get-FileHash -LiteralPath (Join-Path $packages $names.Portable)).Hash.ToLowerInvariant()
    $metadata=@{version=$version;sourceCommit=$sourceCommit;sourceDirty=$false;installer=@{file='packages/'+$names.Installer;sha256=$setupHash};portable=@{file='packages/'+$names.Portable;sha256=$zipHash}}
    Write-Json (Join-Path $folder 'release.json') $metadata;Write-Json (Join-Path $packages 'release.json') $metadata
    Write-Json (Join-Path $packages 'sbom.cdx.json') @{bomFormat='CycloneDX';specVersion='1.6';metadata=@{component=@{'bom-ref'='app';version=$version}};components=@();dependencies=@()}
    Write-Json (Join-Path $packages 'feichuan-toolbox.scoop.json') @{version=$version;architecture=@{'64bit'=@{url=$base+$names.Portable;hash=$zipHash}};shortcuts=(,@('TunHotspot.exe','Fixture'))}
    $definition=Get-Content -LiteralPath (Join-Path $root 'installer\TunHotspot.iss') -Raw
    $publisher=[regex]::Match($definition,'(?m)^AppPublisher=(.+)').Groups[1].Value.Trim()
    $id=[regex]::Match($definition,'(?m)^AppId=\{\{([A-F0-9-]{36})\}').Groups[1].Value
    @("PackageVersion: $version","Publisher: $publisher","ProductCode: '{$id}_is1'",('    InstallerUrl: '+$base+$names.Installer),('    InstallerSha256: '+$setupHash.ToUpperInvariant()))|Set-Content -LiteralPath (Join-Path $packages 'lizhengyi3003.FeiChuanToolbox.yaml') -Encoding UTF8
    Get-ChildItem -LiteralPath $packages -File|Sort-Object Name|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name}|Set-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt') -Encoding UTF8
    $assets=@(Get-ChildItem -LiteralPath $packages -File|ForEach-Object{@{name=$_.Name;digest='sha256:'+(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();browser_download_url='https://github.com/'+$repository+'/releases/download/untagged-fixture/'+$_.Name}})
    $draft=@{tag_name=$tag;target_commitish=$sourceCommit;draft=$true;immutable=$false;assets=$assets}
    $arguments=@{ReleaseDirectory=$folder;Repository=$repository;Tag=$tag;SourceCommit=$sourceCommit}
    & $validator @arguments -ReleaseJson ($draft|ConvertTo-Json -Depth 20) -Draft|Out-Null
    Reject {& $validator @arguments -ReleaseJson ($draft|ConvertTo-Json -Depth 20)} '*Expected a public immutable release*'
    $originalName=$draft.assets[0].name;$draft.assets[0].name='renamed.exe'
    Reject {& $validator @arguments -ReleaseJson ($draft|ConvertTo-Json -Depth 20) -Draft} '*renamed or changed*'
    $draft.assets[0].name=$originalName
    $originalDigest=$draft.assets[0].digest;$draft.assets[0].digest='sha256:'+('b'*64)
    Reject {& $validator @arguments -ReleaseJson ($draft|ConvertTo-Json -Depth 20) -Draft} '*renamed or changed*'
    $draft.assets[0].digest=$originalDigest
    $public=($draft|ConvertTo-Json -Depth 20)|ConvertFrom-Json;$public.draft=$false;$public.immutable=$true
    Reject {& $validator @arguments -ReleaseJson ($public|ConvertTo-Json -Depth 20)} '*Published asset URL differs*'
    foreach($asset in $public.assets){$asset.browser_download_url=$base+$asset.name}
    & $validator @arguments -ReleaseJson ($public|ConvertTo-Json -Depth 20)|Out-Null
    Write-Output 'Release validation: 6 draft/public normalization, hash and URL checks passed.'
}finally{
    $resolved=[IO.Path]::GetFullPath($folder)
    if(-not $resolved.StartsWith((Join-Path $root 'artifacts\temp\release-validation-fixture-'),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
