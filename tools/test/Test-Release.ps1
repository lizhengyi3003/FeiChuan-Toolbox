param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'tools\common\Artifacts.ps1')
if (-not $ReleaseDirectory) { $ReleaseDirectory = (Get-ArtifactLayout $root).Current }
$ReleaseDirectory = [IO.Path]::GetFullPath($ReleaseDirectory)
$app = Join-Path $ReleaseDirectory 'app'
function Resolve-ReleaseFile([string]$relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $ReleaseDirectory $relative))
    if (-not $path.StartsWith($ReleaseDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "发布文件缺失或路径越界：$relative"
    }
    return $path
}
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$bundle = Get-Content -LiteralPath (Resolve-ReleaseFile 'release.json') -Raw | ConvertFrom-Json
$runtime = Get-Content -LiteralPath (Resolve-ReleaseFile 'app\release.json') -Raw | ConvertFrom-Json
if ($bundle.version -ne $version -or $runtime.version -ne $version) { throw '源码、分发包或运行目录版本不一致。' }
foreach ($binary in @('TunHotspot.exe', 'TunHotspot.Core.dll', 'Engine\TunHotspot.Engine.exe', 'Engine\TunHotspot.Core.dll')) {
    $file = Resolve-ReleaseFile ('app\' + $binary)
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion.Split('+')[0] -ne $version) { throw "程序集版本不一致：$binary" }
}
if ((Get-Content -LiteralPath (Resolve-ReleaseFile 'app\使用说明.txt') -TotalCount 1) -notmatch [regex]::Escape($version)) { throw '包内使用说明版本未对齐。' }
$hashes = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath (Resolve-ReleaseFile 'app\files.sha256')) {
    if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw '发布清单格式无效。' }
    $expected = $matches[1]; $relative = $matches[2].Replace('\', '/')
    $file = Resolve-ReleaseFile ('app/' + $relative)
    if (-not $hashes.TryAdd($relative, $expected) -or (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $expected) { throw "清单校验失败：$relative" }
}
foreach ($file in Get-ChildItem -LiteralPath $app -Recurse -File -Force) {
    $relative = [IO.Path]::GetRelativePath($app, $file.FullName).Replace('\', '/')
    if ($relative -ne 'files.sha256' -and -not $hashes.ContainsKey($relative)) { throw "存在未列入清单的文件：$relative" }
    if ($relative -match '(?i)(^|/)(\.git|\.codex|qa|audits)(/|$)|\.(db|db-wal|db-shm|jsonl|pdb|wsb|cs|csproj|ps1)$|(^|/)(analysis-|snapshot-)|(^|/)(kumiryo\.exe|Kumiryo\.Integration\.)') {
        throw "混入本机私有资料、源码或测试夹具：$relative"
    }
}
foreach ($package in @($bundle.installer, $bundle.portable)) {
    $file = Resolve-ReleaseFile $package.file
    if ((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $package.sha256) { throw "分发包 SHA-256 不一致：$($package.file)" }
    $line = Get-Content -LiteralPath ($file + '.sha256') -TotalCount 1
    if ($line -ne ($package.sha256 + '  ' + [IO.Path]::GetFileName($file))) { throw '包旁校验值未对齐。' }
}
$setupVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-ReleaseFile $bundle.installer.file)).ProductVersion.Trim()
if ($setupVersion -ne $version) { throw '安装器自身的产品版本未对齐。' }
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-ReleaseFile $bundle.portable.file))
try {
    $entries = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
    if ($entries.Count -ne $hashes.Count + 1) { throw '便携包与运行目录文件数量不符。' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        $name = $entry.FullName.Replace('\', '/')
        if (-not $seen.Add($name)) { throw '便携包含重复路径。' }
        if ($name -eq 'files.sha256') { $expected = (Get-FileHash -LiteralPath (Join-Path $app 'files.sha256')).Hash.ToLowerInvariant() }
        elseif ($hashes.ContainsKey($name)) { $expected = $hashes[$name] }
        else { throw "便携包夹带额外文件：$name" }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -ne $expected) { throw "便携包内容不一致：$name" }
    }
} finally { $zip.Dispose() }
[pscustomobject]@{ Success = $true; Version = $version; FilesVerified = $hashes.Count; PortableFilesVerified = $entries.Count;
    SourceDirty = $runtime.sourceDirty; ReleaseDirectory = $ReleaseDirectory } | ConvertTo-Json -Compress
