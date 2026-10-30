param([Parameter(Mandatory=$true)][string]$PublishDirectory,[string]$VCRuntimeDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$publish=[IO.Path]::GetFullPath($PublishDirectory)
if(-not(Test-Path -LiteralPath (Join-Path $publish 'TunHotspot.exe'))){throw '未找到已发布的工具箱。'}
if(-not $VCRuntimeDirectory){
    $runtimeCandidates=@(Get-ChildItem -Path 'C:\Program Files (x86)\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT','C:\Program Files\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\x64\Microsoft.VC*.CRT' -Directory -ErrorAction SilentlyContinue)
    $VCRuntimeDirectory=($runtimeCandidates | Sort-Object FullName -Descending | Select-Object -First 1).FullName
}
if(-not $VCRuntimeDirectory){throw '需要官方 Visual C++ x64 app-local CRT。请通过 -VCRuntimeDirectory 指定 Visual Studio VC\Redist 中的 Microsoft.VC*.CRT 目录。'}
foreach($dll in Get-ChildItem -LiteralPath $VCRuntimeDirectory -Filter '*.dll' -File){
    $signature=Get-AuthenticodeSignature -LiteralPath $dll.FullName
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft'){throw "CRT 文件签名未通过：$($dll.Name)"}
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $publish $dll.Name) -Force
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $publish ('Engine\'+$dll.Name)) -Force
}
$notices=Join-Path $publish 'Notices'
Copy-Item -LiteralPath (Join-Path $root 'docs\使用说明.txt') -Destination (Join-Path $publish '使用说明.txt') -Force
Copy-Item -LiteralPath (Join-Path $root 'CHANGELOG.md') -Destination (Join-Path $publish 'CHANGELOG.md') -Force
New-Item -ItemType Directory -Path $notices -Force | Out-Null
$libraries=@{}
foreach($assetFile in @('src\TunHotspot.WinUI\obj\project.assets.json','src\TunHotspot.Engine\obj\project.assets.json')){
    $assets=Get-Content -LiteralPath (Join-Path $root $assetFile) -Raw -Encoding utf8 | ConvertFrom-Json
    foreach($property in $assets.libraries.PSObject.Properties){
        if($property.Value.type -ne 'package'){continue}
        $name=$property.Name
        $packageFolder=$null
        foreach($packageRoot in $assets.packageFolders.PSObject.Properties.Name){$candidate=Join-Path $packageRoot $property.Value.path;if(Test-Path -LiteralPath $candidate){$packageFolder=$candidate;break}}
        if(-not $packageFolder){throw "无法定位依赖：$name"}
        $licenseExpression='See package notices'
        $spec=Get-ChildItem -LiteralPath $packageFolder -Filter '*.nuspec' -File | Select-Object -First 1
        if($spec){[xml]$nuspec=Get-Content -LiteralPath $spec.FullName -Raw -Encoding utf8;$node=$nuspec.SelectSingleNode("//*[local-name()='license']");if($node){$licenseExpression=$node.InnerText}}
        $libraries[$name]=[ordered]@{Package=$name;License=$licenseExpression}
        $destination=Join-Path $notices ($name -replace '/','.')
        foreach($license in Get-ChildItem -LiteralPath $packageFolder -File | Where-Object {$_.Name -match '^(LICENSE|NOTICE|THIRD.PARTY)'} ){
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $destination $license.Name) -Force
        }
    }
}
$libraries.Values | Sort-Object Package | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $notices 'dependencies.json') -Encoding utf8
@'
Visual C++ x64 CRT app-local redistributable files are included from the installed Visual Studio VC\Redist directory.
These are Microsoft redistributable runtime files; no developer tools, user configuration, Codex binaries, or system databases are bundled.
All application/runtime package license files available in the NuGet packages are collected in this directory.
'@ | Set-Content -LiteralPath (Join-Path $notices 'DISTRIBUTION.txt') -Encoding utf8
# Remove only PDB files under this explicitly resolved publish directory. No recursive directory deletion.
[xml]$properties=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$sourceCommit=(& git -C $root rev-parse HEAD 2>$null)
if($LASTEXITCODE){throw '无法读取发布对应的 Git 提交。'}
$sourceDirty=[bool](& git -C $root status --porcelain --untracked-files=no)
[ordered]@{version=[string]$properties.Project.PropertyGroup.Version;targetFramework=[string]$properties.Project.PropertyGroup.TargetFramework;runtimeIdentifier='win-x64';selfContained=$true;sourceCommit=[string]$sourceCommit;sourceDirty=$sourceDirty;builtAt=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'release.json') -Encoding utf8
foreach($pdb in Get-ChildItem -LiteralPath $publish -Recurse -File -Filter '*.pdb'){
    if(-not $pdb.FullName.StartsWith($publish+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '发布路径边界检查失败。'}
    Remove-Item -LiteralPath $pdb.FullName
}
$manifest=@(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {$_.Name -ne 'files.sha256'} | Sort-Object FullName | ForEach-Object {
    $relative=[IO.Path]::GetRelativePath($publish,$_.FullName)
    $hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
})
$manifest | Set-Content -LiteralPath (Join-Path $publish 'files.sha256') -Encoding utf8
Write-Output "分发依赖与校验已生成：$publish"
