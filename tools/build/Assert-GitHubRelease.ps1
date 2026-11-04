param(
    [Parameter(Mandatory=$true)][string]$ReleaseJson,
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$Repository,
    [Parameter(Mandatory=$true)][string]$Tag,
    [Parameter(Mandatory=$true)][string]$SourceCommit,
    [switch]$Draft
)
$ErrorActionPreference='Stop'
if($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Tag -notmatch '^v\d+\.\d+\.\d+$' -or $SourceCommit -notmatch '^[a-f0-9]{40}$'){throw 'Invalid release verification identity.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
& (Join-Path $root 'tools\test\Test-PackageMetadata.ps1') -ReleaseDirectory $ReleaseDirectory
$metadata=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'release.json') -Raw|ConvertFrom-Json
$release=$ReleaseJson|ConvertFrom-Json
if($metadata.sourceCommit -cne $SourceCommit -or ('v'+$metadata.version) -cne $Tag -or $release.tag_name -cne $Tag -or $release.target_commitish -cne $SourceCommit){throw 'Remote release/source identity differs from verified packages.'}
if($Draft){if(-not $release.draft){throw 'Expected an unpublished draft.'}}
elseif($release.draft -or -not $release.immutable){throw 'Expected a public immutable release.'}
$assets=@(Get-ChildItem -LiteralPath (Join-Path $ReleaseDirectory 'packages') -File)
$remoteAssets=@($release.assets)
if($remoteAssets.Count -ne $assets.Count){throw 'Uploaded asset count differs from verified local assets.'}
foreach($asset in $assets){
    $match=@($remoteAssets|Where-Object{$_.name -ceq $asset.Name})
    $digest='sha256:'+(Get-FileHash -LiteralPath $asset.FullName).Hash.ToLowerInvariant()
    if($match.Count -ne 1 -or $match[0].digest -cne $digest){throw 'GitHub renamed or changed an uploaded asset.'}
    if(-not $Draft){
        $expected='https://github.com/'+$Repository+'/releases/download/'+$Tag+'/'+$asset.Name
        if($match[0].browser_download_url -cne $expected){throw 'Published asset URL differs from package metadata.'}
    }
}
$base='https://github.com/'+$Repository+'/releases/download/'+$Tag+'/'
$scoop=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'packages\feichuan-toolbox.scoop.json') -Raw|ConvertFrom-Json
$winget=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'packages\lizhengyi3003.FeiChuanToolbox.yaml') -Raw
if($scoop.architecture.'64bit'.url -cne ($base+[IO.Path]::GetFileName($metadata.portable.file)) -or -not $winget.Contains($base+[IO.Path]::GetFileName($metadata.installer.file))){throw 'Package manager URLs differ from publication identity.'}
Write-Output ('GitHub '+$(if($Draft){'draft'}else{'public'})+' identity, names and digests verified.')
