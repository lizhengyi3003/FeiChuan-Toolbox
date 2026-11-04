param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$issues = [Collections.Generic.List[string]]::new()
$checkedReferences = 0

function Confirm-LocalReference([string]$baseDirectory, [string]$relativePath, [string]$source) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $baseDirectory $relativePath))
    if (-not $candidate.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        $issues.Add("引用超出仓库：$source -> $relativePath")
    }
    elseif (-not (Test-Path -LiteralPath $candidate)) {
        $issues.Add("引用不存在：$source -> $relativePath")
    }
    $script:checkedReferences++
}

[xml]$solution = Get-Content -LiteralPath (Join-Path $root 'TunHotspot.slnx') -Raw
foreach ($node in $solution.SelectNodes('//*[@Path]')) {
    Confirm-LocalReference $root $node.Path 'TunHotspot.slnx'
}
[xml]$packages = Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw
[xml]$product = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$product.Project.PropertyGroup.Version
foreach ($relative in @('README.md', 'docs\guides\使用说明.txt', 'docs\guides\启动管理-能力与验证.md', 'docs\guides\Kumiryo一键配置.md', 'docs\TROUBLESHOOTING.md', 'docs\ARCHITECTURE.md', 'docs\VALIDATION.md')) {
    $text = (Get-Content -LiteralPath (Join-Path $root $relative) -TotalCount 4) -join "`n"
    if ($text -notmatch [regex]::Escape($version)) { $issues.Add("当前文档未对齐产品版本：$relative -> $version") }
}
if (-not (Select-String -LiteralPath (Join-Path $root 'CHANGELOG.md') -Pattern ('^## ' + [regex]::Escape($version) + ' ·') -Quiet)) {
    $issues.Add('更新记录缺少当前产品版本。')
}
$artifactRoot = Join-Path $root 'artifacts'
if (Test-Path -LiteralPath $artifactRoot) {
    foreach ($item in Get-ChildItem -LiteralPath $artifactRoot -Force) {
        if ($item.Name -notin @('current', 'archive', 'reports', 'backups', 'temp', 'README.md', 'index.json')) {
            $issues.Add("产物目录出现散装文件或旧布局：$($item.Name)")
        }
    }
}
$packageNames = @($packages.Project.ItemGroup.PackageVersion | ForEach-Object { $_.Include })
$projects = @(Get-ChildItem -LiteralPath (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter '*.csproj' -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' })
foreach ($project in $projects) {
    [xml]$projectXml = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in $projectXml.SelectNodes('//ProjectReference|//EmbeddedResource')) {
        if ($reference.Include -notmatch '[*?]') { Confirm-LocalReference $project.DirectoryName $reference.Include $project.Name }
    }
    foreach ($reference in $projectXml.SelectNodes('//PackageReference')) {
        if ($reference.HasAttribute('Version') -or $reference.HasAttribute('VersionOverride') -or $packageNames -notcontains $reference.Include) {
            $issues.Add("NuGet 版本未统一管理：$($project.Name) -> $($reference.Include)")
        }
    }
    if ($project.DirectoryName -match '\\src\\TunHotspot.Core$' -and $projectXml.SelectNodes('//ProjectReference').Count) {
        $issues.Add('Core 不应反向引用 UI、Engine 或测试项目。')
    }
}

$scripts = @((Join-Path $root 'build.ps1'), (Join-Path $root 'test.ps1')) +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'tools'), (Join-Path $root 'installer') -Recurse -Filter '*.ps1' -File | ForEach-Object { $_.FullName })
foreach ($script in $scripts) {
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$parseErrors)
    foreach ($errorItem in $parseErrors) { $issues.Add("PowerShell 语法错误：$script : $($errorItem.Message)") }
}

$documents = @(Get-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md')) +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'docs'), (Join-Path $root 'tools'), (Join-Path $root 'tests') -Recurse -Filter '*.md' -File |
        Where-Object { $_.FullName -notmatch '\\(history|bin|obj)\\' })
foreach ($document in $documents) {
    $text = Get-Content -LiteralPath $document.FullName -Raw
    foreach ($match in [regex]::Matches($text, '\]\((?<target>[^)]+)\)')) {
        $target = $match.Groups['target'].Value.Trim('<', '>')
        if ($target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:' -or $target.StartsWith('#')) { continue }
        $target = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if ($target.Length) { Confirm-LocalReference $document.DirectoryName $target $document.Name }
    }
}

if ($issues.Count) { throw ("工程检查失败：`n" + ($issues -join "`n")) }
[pscustomobject]@{
    Success = $true
    Version = $version
    Projects = $projects.Count
    ScriptsParsed = $scripts.Count
    Documents = $documents.Count
    LocalReferences = $checkedReferences
} | ConvertTo-Json -Compress
