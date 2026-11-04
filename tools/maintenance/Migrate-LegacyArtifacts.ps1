param([switch]$Apply, [switch]$RemoveBuildCaches)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'tools\common\Artifacts.ps1')
$layout = Get-ArtifactLayout $root
$audit = Join-Path $layout.Reports 'cleanup-20261103'
$records = [Collections.Generic.List[object]]::new()
$relocations = [ordered]@{}
$deletedBytes = [long]0
$archivedBytes = [long]0
Add-Type -AssemblyName System.IO.Compression

function Confirm-WorkspacePath([string]$path) {
    $absolute = [IO.Path]::GetFullPath($path)
    if (-not $absolute.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "路径越界：$absolute" }
    $ancestor = $absolute
    while ($ancestor.Length -gt $root.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "拒绝重解析点：$ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
    return $absolute
}

function Confirm-NotRunning([string]$directory) {
    $prefix = (Confirm-WorkspacePath $directory).TrimEnd('\') + '\'
    foreach ($process in Get-CimInstance Win32_Process) {
        if ($process.ExecutablePath -and $process.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "目录仍有运行程序，保留不动：$($process.ProcessId) $directory"
        }
    }
    foreach ($service in Get-CimInstance Win32_Service | Where-Object { $_.State -eq 'Running' }) {
        if ($service.PathName -and $service.PathName.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "运行服务仍引用此目录：$($service.Name)"
        }
    }
}

function Get-SafeFiles([string]$directory) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue((Confirm-WorkspacePath $directory))
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "拒绝重解析点：$($item.FullName)" }
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) } else { $item }
        }
    }
}

function Relative([string]$path) { [IO.Path]::GetRelativePath($root, $path).Replace('\', '/') }
function Record([string]$action, [string]$source, [string]$destination, [long]$bytes, [string]$reason) {
    $entry = [ordered]@{ Action = $action; Source = (Relative $source); Destination = $destination; Bytes = $bytes; Reason = $reason }
    $records.Add($entry)
    if ($Apply) { $entry | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $audit 'operations.jsonl') -Encoding utf8 }
}

function Move-Legacy([string]$source, [string]$destination) {
    if (-not (Test-Path -LiteralPath $source)) { return }
    $source = Confirm-WorkspacePath $source
    $destination = Confirm-WorkspacePath $destination
    if (Test-Path -LiteralPath $destination) { throw "归档目标已存在：$destination" }
    $item = Get-Item -LiteralPath $source -Force
    $files = if ($item.PSIsContainer) { @(Get-SafeFiles $source) } else { @($item) }
    $bytes = [long](($files | Measure-Object Length -Sum).Sum)
    if ($Apply) {
        if ($item.PSIsContainer) { Confirm-NotRunning $source }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Move-Item -LiteralPath $source -Destination $destination
    }
    $script:relocations[(Relative $source)] = Relative $destination
    Record 'Move' $source (Relative $destination) $bytes '保留原件，归并分类；文件内容与原有私有 ACL 不改写'
}

function Remove-Generated([string]$directory, [string]$reason) {
    if (-not (Test-Path -LiteralPath $directory)) { return }
    $directory = Confirm-WorkspacePath $directory
    $files = @(Get-SafeFiles $directory)
    $bytes = [long](($files | Measure-Object Length -Sum).Sum)
    if ($Apply) {
        Confirm-NotRunning $directory
        Remove-Item -LiteralPath $directory -Recurse -Force
        $script:deletedBytes += $bytes
    }
    Record 'DeleteGenerated' $directory '' $bytes $reason
}

function Archive-Build([string]$directory) {
    $directory = Confirm-WorkspacePath $directory
    $name = Split-Path -Leaf $directory
    $zipPath = Confirm-WorkspacePath (Join-Path $layout.Archive ('builds\' + $name + '.zip'))
    if (Test-Path -LiteralPath $zipPath) { throw "构建归档已存在：$zipPath" }
    $files = @(Get-SafeFiles $directory)
    $bytes = [long](($files | Measure-Object Length -Sum).Sum)
    if ($Apply) {
        Confirm-NotRunning $directory
        Write-Output "归档并验证旧构建：$name"
        [IO.Directory]::CreateDirectory((Split-Path -Parent $zipPath)) | Out-Null
        $manifest = @($files | ForEach-Object {
            [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\', '/'); Length = $_.Length;
                LastWrite = $_.LastWriteTimeUtc.Ticks; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
        })
        $temporary = $zipPath + '.partial-' + [Guid]::NewGuid().ToString('N')
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $manifest) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $directory $file.Path), $file.Path, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
        $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
        try {
            if ($archive.Entries.Count -ne $manifest.Count) { throw '归档条目数量不符，保留原目录。' }
            foreach ($file in $manifest) {
                $entry = $archive.GetEntry($file.Path)
                if (-not $entry -or $entry.Length -ne $file.Length) { throw '归档条目缺失。' }
                $stream = $entry.Open()
                try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
                finally { $stream.Dispose() }
                $original = Get-Item -LiteralPath (Join-Path $directory $file.Path)
                if ($hash -ne $file.Sha256 -or $original.Length -ne $file.Length -or $original.LastWriteTimeUtc.Ticks -ne $file.LastWrite) {
                    throw '归档内容或原件状态变化，保留原目录。'
                }
            }
        } finally { $archive.Dispose() }
        if (@(Get-SafeFiles $directory).Count -ne $manifest.Count) { throw '归档期间新增文件，保留原目录。' }
        Confirm-NotRunning $directory
        Move-Item -LiteralPath $temporary -Destination $zipPath
        $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath ($zipPath + '.files.json') -Encoding utf8
        (Get-FileHash -LiteralPath $zipPath).Hash.ToLowerInvariant() | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ascii
        Remove-Item -LiteralPath $directory -Recurse -Force
        $script:archivedBytes += $bytes - (Get-Item -LiteralPath $zipPath).Length
    }
    $script:relocations[(Relative $directory)] = (Relative $zipPath) + '!'
    Record 'ArchiveVerifiedBuild' $directory ((Relative $zipPath) + '!') $bytes '全文件 SHA-256 与条目数量核验成功后，删除展开副本；ZIP 和逐文件清单保留'
}

if ($Apply) {
    [IO.Directory]::CreateDirectory($audit) | Out-Null
    if (Test-Path -LiteralPath (Join-Path $audit 'operations.jsonl')) { throw '已有迁移操作记录，请先检查上轮结果，禁止覆盖回执。' }
}

# All legacy names below were inventoried. Never enumerate a disk for deletion.
foreach ($folder in @('installer', 'distribution')) {
    $directory = Join-Path $layout.Root $folder
    if (-not (Test-Path -LiteralPath $directory)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Force) {
        if ($file.Name -notmatch '(?<!\d)(\d+\.\d+\.\d+)(?!\d)') { throw "无法确定历史包版本：$($file.Name)" }
        $version = $matches[1]
        if ($Apply -and $file.Extension -eq '.sha256') {
            $originalPackage = $file.FullName.Substring(0, $file.FullName.Length - '.sha256'.Length)
            # The package may already have moved earlier in this loop; destination preserves its exact name.
            if (-not (Test-Path -LiteralPath $originalPackage)) { $originalPackage = Join-Path $layout.Archive ("releases\$version\" + [IO.Path]::GetFileName($originalPackage)) }
            $line = Get-Content -LiteralPath $file.FullName -TotalCount 1
            if ($line -notmatch '^([a-fA-F0-9]{64}) ' -or (Get-FileHash -LiteralPath $originalPackage).Hash -ne $matches[1]) { throw '历史交付包校验失败，停止归档。' }
        }
        Move-Legacy $file.FullName (Join-Path $layout.Archive ("releases\$version\" + $file.Name))
    }
    if ($Apply -and -not @(Get-ChildItem -LiteralPath $directory -Force).Count) { Remove-Item -LiteralPath (Confirm-WorkspacePath $directory) }
}

$retained = [ordered]@{
    'data-recovery-20261101' = 'backups\recovery-20261101'
    'repair-settings-20261102-122615' = 'backups\repair-settings-20261102'
    'reliability-20261102-185848' = 'reports\reliability-20261102'
    'engineering-20261103' = 'reports\engineering-20261103'
    'kumiryo-injection-20261102' = 'reports\kumiryo-20261102'
    'rename-20261102-133402' = 'reports\rename-20261102'
    'startup-guard-review-20261101' = 'reports\startup-guard-20261101'
    'ui-review-20261101' = 'reports\ui-review-20261101'
    'qa' = 'reports\legacy-qa'
    'hotspot-fix-20261101-tray-ui.json' = 'reports\ui-review-20261101\hotspot-tray-ui.json'
}
foreach ($name in $retained.Keys) { Move-Legacy (Join-Path $layout.Root $name) (Join-Path $layout.Root $retained[$name]) }
foreach ($name in @('active-reliability.txt', 'active-rename.txt', 'active-repair-settings.txt')) {
    Move-Legacy (Join-Path $layout.Root $name) (Join-Path $layout.Archive ('legacy-indexes\' + $name))
}

foreach ($directory in Get-ChildItem -LiteralPath $layout.Root -Directory -Force) {
    if ($directory.Name -match '^(toolbox-|win-x64$|previous-builds$|diagnostic-engine$|hotspot-fix-20261101$|startup-guard-20261101$|legacy-preview-before-)') {
        Archive-Build $directory.FullName
    }
    elseif ($directory.Name -match '^kumiryo-fixture-(client|driver)(-|$)') {
        if (-not (Test-Path -LiteralPath (Join-Path $root 'tests\Fixtures\Kumiryo.Client\Kumiryo.Client.csproj')) -or
            -not (Test-Path -LiteralPath (Join-Path $root 'tests\Fixtures\Kumiryo.Integration\Kumiryo.Integration.csproj'))) { throw '测试夹具源码缺失，保留旧输出。' }
        Remove-Generated $directory.FullName '过期模拟客户端/集成驱动的自包含输出；源码已在 tests/Fixtures，测试结论另存 reports'
    }
}

$oldEngineering = if ($Apply) { Join-Path $layout.Reports 'engineering-20261103' } else { Join-Path $layout.Root 'engineering-20261103' }
foreach ($name in @('legacy-generated', 'source-check\bin', 'source-check\obj', 'publish', 'installer')) {
    Remove-Generated (Join-Path $oldEngineering $name) '上轮验证构建或编译缓存；源码原件 ZIP、迁移清单、测试回执和截图保留'
}
foreach ($name in @('android-debug-20261029', 'android-platform-tools-37.0.1', 'migration-archive')) {
    Move-Legacy (Join-Path $root ('.tools\' + $name)) (Join-Path $layout.Archive ('development-tools\' + $name))
}
if ($RemoveBuildCaches) {
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter '*.csproj' -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($project in $projects) {
        foreach ($name in @('bin', 'obj')) { Remove-Generated (Join-Path $project.DirectoryName $name) '可由本机 SDK 和保留的依赖 feed 重建的项目编译缓存' }
    }
}

$summary = [ordered]@{ Applied = [bool]$Apply; Operations = $records; Relocations = $relocations;
    DeletedGeneratedBytes = $deletedBytes; ArchiveSpaceSavedBytes = $archivedBytes }
if ($Apply) { $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $audit 'migration.json') -Encoding utf8 }
else { $summary | ConvertTo-Json -Depth 6 }
