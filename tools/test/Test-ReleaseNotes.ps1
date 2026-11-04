param()
$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot '..\build\New-ReleaseNotes.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('toolbox-notes-' + [Guid]::NewGuid().ToString('N') + '.md')
try {
    "# Changes`n`n## 9.8.7 · fixture`n`n- current fix`n`n## 9.8.6 · old`n`n- old fix" | Set-Content -LiteralPath $temporary -Encoding utf8
    $notes = & $builder -Version '9.8.7' -ChangelogPath $temporary
    if ($notes -notmatch 'current fix' -or $notes -match 'old fix') { throw 'Release notes crossed version boundaries.' }
    foreach ($version in @('9.8.5', '9.8.7;exit')) {
        $rejected = $false
        try { $null = & $builder -Version $version -ChangelogPath $temporary } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid or missing version was accepted.' }
    }
    "## 9.8.7 · duplicate`n- one`n## 9.8.7 · duplicate`n- two" | Set-Content -LiteralPath $temporary -Encoding utf8
    $rejected = $false
    try { $null = & $builder -Version '9.8.7' -ChangelogPath $temporary } catch { $rejected = $true }
    if (-not $rejected) { throw 'Duplicate changelog section was accepted.' }
    Write-Output 'Release note version-boundary and rejection checks passed.'
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
