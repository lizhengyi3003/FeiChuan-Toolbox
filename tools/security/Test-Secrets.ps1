param([switch]$History)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$tools = & (Join-Path $PSScriptRoot 'Initialize-SecurityTools.ps1')
$temporary = Join-Path $root ('artifacts\temp\secret-scan-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $paths = & git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE) { throw 'Cannot enumerate source files.' }
    foreach ($relative in $paths | Sort-Object -Unique) {
        if ($relative -match '^(artifacts|\.git|\.tools|\.vs)/') { throw 'Private directory is tracked.' }
        $source = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $source.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escaped repository.' }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source contains a reparse point.' }
        $target = Join-Path $temporary $relative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    & $tools.gitleaks dir $temporary --redact=100 --no-banner
    if ($LASTEXITCODE) { throw 'Source secret scan failed; inspect redacted findings.' }
    if ($History) {
        & $tools.gitleaks git $root --redact=100 --no-banner '--log-opts=--all'
        if ($LASTEXITCODE) { throw 'Git history secret scan failed.' }
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if (-not $resolved.StartsWith((Join-Path $root 'artifacts\temp\secret-scan-'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe scanner cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
