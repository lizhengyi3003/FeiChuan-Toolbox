param([switch]$Offline,[string]$OutputDirectory,[string]$CompilerPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
[xml]$properties=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version=[string]$properties.Project.PropertyGroup.Version
if($version -notmatch '^\d+\.\d+\.\d+$'){throw '发布版本格式无效。'}
$trackedChanges=(& git -C $root status --porcelain --untracked-files=no)
if($LASTEXITCODE -or $trackedChanges){throw '正式发布前请提交本次源码和说明，保证提交与生成文件一致。'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts\releases\'+$version+'-'+[Guid]::NewGuid().ToString('N'))}
$publish=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath $publish) -and @(Get-ChildItem -LiteralPath $publish -Force).Count){throw '正式发布必须使用空目录，避免夹带旧版文件；请保留旧目录并选择新目录。'}
if((Get-Content -LiteralPath (Join-Path $root 'docs\使用说明.txt') -TotalCount 1) -notmatch [regex]::Escape($version)){throw '使用说明版本与统一版本不一致。'}
if(-not(Select-String -LiteralPath (Join-Path $root 'CHANGELOG.md') -Pattern ('^## '+[regex]::Escape($version)+' ·') -Quiet)){throw '更新记录缺少本版说明。'}
& (Join-Path $root 'build.ps1') -Publish -Offline:$Offline -OutputDirectory $publish
$release=Get-Content -LiteralPath (Join-Path $publish 'release.json') -Raw | ConvertFrom-Json
if($release.version -ne $version){throw '发布元数据版本不一致。'}
$listed=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($line in Get-Content -LiteralPath (Join-Path $publish 'files.sha256')){
    if($line -notmatch '^([a-f0-9]{64})  (.+)$'){throw '发布校验表格式无效。'}
    $hash=$matches[1];$relative=$matches[2]
    $file=[IO.Path]::GetFullPath((Join-Path $publish $relative))
    if(-not $file.StartsWith($publish+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '发布校验表路径越界。'}
    if(-not $listed.Add($relative) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash){throw "发布文件校验失败：$relative"}
}
foreach($file in Get-ChildItem -LiteralPath $publish -Recurse -File){
    $relative=[IO.Path]::GetRelativePath($publish,$file.FullName)
    if($relative -match '(?i)(^|[\\/])(\.git|\.codex|qa|audits)([\\/]|$)|\.(db|db-wal|db-shm|jsonl|pdb|wsb)$|(^|[\\/])(analysis-|snapshot-)'){throw "发布目录混入私有或开发文件：$relative"}
    if($relative -ne 'files.sha256' -and -not $listed.Contains($relative)){throw "未列入校验表：$relative"}
}
& (Join-Path $root 'tools\Build-Installer.ps1') -PublishDirectory $publish -CompilerPath $CompilerPath
$distribution=Join-Path $root 'artifacts\distribution'
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
$zip=Join-Path $distribution "TunHotspot-$version-win11-x64.zip"
# Replace only this exact release archive; never delete a directory or other versions.
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip}
[IO.Compression.ZipFile]::CreateFromDirectory($publish,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$zipHash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
$setup=Join-Path $root "artifacts\installer\TunHotspot-$version-Setup-x64.exe"
$manifest=[ordered]@{version=$version;sourceCommit=$release.sourceCommit;runtimeIdentifier=$release.runtimeIdentifier;filesVerified=$listed.Count;installer=[ordered]@{file=[IO.Path]::GetFileName($setup);sha256=(Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()};portable=[ordered]@{file=[IO.Path]::GetFileName($zip);sha256=$zipHash}}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $distribution "release-$version.json") -Encoding utf8
[pscustomobject]@{PublishDirectory=$publish;Installer=$setup;Portable=$zip;FilesVerified=$listed.Count} | ConvertTo-Json
