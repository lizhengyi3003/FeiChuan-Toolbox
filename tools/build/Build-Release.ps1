param([switch]$Offline, [string]$OutputDirectory, [string]$CompilerPath, [switch]$WorkingTree)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'tools\common\Artifacts.ps1')
$layout = Get-ArtifactLayout $root
[xml]$properties = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '发布版本格式无效。' }
$sourceChanges = (& git -C $root status --porcelain --untracked-files=all)
if ($LASTEXITCODE) { throw '无法检查源码状态。' }
if ($sourceChanges -and -not $WorkingTree) { throw '正式发布前请提交源码和说明（包括新增文件）；本地验证可显式使用 -WorkingTree，元数据会如实记录未提交状态。' }
if ((Get-Content -LiteralPath (Join-Path $root 'docs\guides\使用说明.txt') -TotalCount 1) -notmatch [regex]::Escape($version)) { throw '使用说明版本与统一版本不一致。' }
if (-not (Select-String -LiteralPath (Join-Path $root 'CHANGELOG.md') -Pattern ('^## ' + [regex]::Escape($version) + ' ·') -Quiet)) { throw '更新记录缺少本版说明。' }

function Confirm-ArtifactPath([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($layout.Root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '发布目录必须位于 artifacts 内，不能替换仓库根目录或系统目录。' }
    $ancestor = $absolute
    while ($ancestor.Length -gt $layout.Root.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '发布路径含重解析点。' }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $absolute
}
function Confirm-NoRunningApp([string]$directory) {
    foreach ($process in Get-CimInstance Win32_Process) {
        if ($process.ExecutablePath -and $process.ExecutablePath.StartsWith($directory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw '当前发布目录仍有运行程序，请先退出其中的程序；不会替你终止。'
        }
    }
    foreach ($service in Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' }) {
        if ($service.PathName -and $service.PathName.IndexOf($directory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw '运行服务仍引用当前发布目录，保留原目录。'
        }
    }
}
if (-not $OutputDirectory) { $OutputDirectory = $layout.Current }
$destination = Confirm-ArtifactPath $OutputDirectory
if (Test-Path -LiteralPath $destination) {
    Confirm-NoRunningApp $destination
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count -and -not (Test-Path -LiteralPath (Join-Path $destination 'release.json'))) {
        throw '目标已有非发布资料，拒绝覆盖。'
    }
}
$stage = Confirm-ArtifactPath (Join-Path $layout.Temp ('release-' + [Guid]::NewGuid().ToString('N')))
$publish = Join-Path $stage 'app'
$packages = Join-Path $stage 'packages'
[IO.Directory]::CreateDirectory($stage) | Out-Null
& (Join-Path $root 'build.ps1') -Publish -Offline:$Offline -OutputDirectory $publish
$runtime = Get-Content -LiteralPath (Join-Path $publish 'release.json') -Raw | ConvertFrom-Json
if ($runtime.version -ne $version) { throw '发布元数据版本不一致。' }
& (Join-Path $root 'tools\build\Build-Installer.ps1') -PublishDirectory $publish -OutputDirectory $packages -CompilerPath $CompilerPath
$zipName = "飞船的小工具箱-$version-win11-x64.zip"
$setupName = "飞船的小工具箱-$version-Setup-x64.exe"
$zip = Join-Path $packages $zipName
$setup = Join-Path $packages $setupName
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$zipHash = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
"$zipHash  $zipName" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding utf8
$bundle = [ordered]@{
    version = $version; sourceCommit = $runtime.sourceCommit; sourceDirty = $runtime.sourceDirty
    sourceMode = $(if ($runtime.sourceDirty) { 'working-tree' } else { 'committed' })
    builtAt = $runtime.builtAt; runtimeIdentifier = $runtime.runtimeIdentifier
    installer = [ordered]@{ file = 'packages/' + $setupName; sha256 = (Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant() }
    portable = [ordered]@{ file = 'packages/' + $zipName; sha256 = $zipHash }
}
$bundle | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'release.json') -Encoding utf8
# The current entry changes only after binaries, package hashes and ZIP contents all pass.
& (Join-Path $root 'tools\test\Test-Release.ps1') -ReleaseDirectory $stage
$archived = $null
if (Test-Path -LiteralPath $destination) {
    Confirm-NoRunningApp $destination
    if (@(Get-ChildItem -LiteralPath $destination -Force).Count) {
        $previous = Get-Content -LiteralPath (Join-Path $destination 'release.json') -Raw | ConvertFrom-Json
        if ($previous.version -notmatch '^\d+\.\d+\.\d+$') { throw '旧发布元数据无效，保留旧目录。' }
        $archived = Confirm-ArtifactPath (Join-Path $layout.Archive ('releases\' + $previous.version + '\bundle-' + [Guid]::NewGuid().ToString('N')))
        [IO.Directory]::CreateDirectory((Split-Path -Parent $archived)) | Out-Null
        Move-Item -LiteralPath $destination -Destination $archived
    }
    else { Remove-Item -LiteralPath (Confirm-ArtifactPath $destination) }
}
try { Move-Item -LiteralPath (Confirm-ArtifactPath $stage) -Destination $destination }
catch {
    if ($archived -and -not (Test-Path -LiteralPath $destination)) { Move-Item -LiteralPath (Confirm-ArtifactPath $archived) -Destination $destination }
    throw
}
if ($destination.Equals($layout.Current, [StringComparison]::OrdinalIgnoreCase)) {
$index = [ordered]@{ version = $version; current = [IO.Path]::GetRelativePath($layout.Root, $destination).Replace('\', '/');
    app = 'app/TunHotspot.exe'; release = 'release.json'; sourceDirty = $runtime.sourceDirty }
$index | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $layout.Root 'index.json') -Encoding utf8
@"
# 构建产物

当前版本 **$version**，统一入口：current/app/TunHotspot.exe。
安装包、便携包及各自 SHA-256：current/packages。整体元数据：current/release.json。

| 目录 | 内容 |
| --- | --- |
| current | 本次验证通过的运行目录、安装包和便携包 |
| archive | 历史版本交付包、完整校验的旧构建 ZIP、旧开发工具 |
| reports | 测试、诊断、迁移及清理记录；部分为本机私有证据 |
| backups | 原始数据库、设置和源码备份；禁止随产品分发 |
| temp | 可重新生成的预览与中间构建，失败构建供排查 |

版本、安装包与便携包身份以 current/release.json 为准。sourceDirty=true 表示由未提交工作区生成。
历史原路径迁移表：reports/cleanup-20261103/migration.json。
"@ | Set-Content -LiteralPath (Join-Path $layout.Root 'README.md') -Encoding utf8
}
[pscustomobject]@{ Success = $true; Version = $version; ReleaseDirectory = $destination; SourceDirty = $runtime.sourceDirty } | ConvertTo-Json
