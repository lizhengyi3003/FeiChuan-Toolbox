param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$spec = Get-Content -LiteralPath (Join-Path $root '.github\security-tools.json') -Raw | ConvertFrom-Json
$result = @{}
Add-Type -AssemblyName System.IO.Compression
foreach ($tool in $spec.tools) {
    if ($tool.name -notmatch '^[a-z]+$' -or $tool.sha256 -notmatch '^[a-f0-9]{64}$' -or $tool.binarySha256 -notmatch '^[a-f0-9]{64}$' -or $tool.url -notmatch '^https://github\.com/[^/]+/[^/]+/releases/download/') { throw 'Invalid security tool definition.' }
    $directory = Join-Path $root ('.tools\security\' + $tool.name)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $archive = Join-Path $directory 'tool.zip'
    $binary = Join-Path $directory ($tool.name + '.exe')
    if (-not (Test-Path -LiteralPath $binary) -or (Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant() -ne $tool.binarySha256) {
        if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $tool.sha256) {
            Invoke-WebRequest -Uri $tool.url -OutFile ($archive + '.download')
            if ((Get-FileHash -LiteralPath ($archive + '.download')).Hash.ToLowerInvariant() -ne $tool.sha256) { throw 'Security tool archive checksum mismatch.' }
            Move-Item -LiteralPath ($archive + '.download') -Destination $archive -Force
        }
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $entry = @($zip.Entries | Where-Object { $_.FullName -eq ($tool.name + '.exe') })
            if ($entry.Count -ne 1) { throw 'Security tool binary missing or ambiguous.' }
            $inputStream = $entry[0].Open(); $outputStream = [IO.File]::Create($binary)
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
        } finally { $zip.Dispose() }
    }
    if ((Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant() -ne $tool.binarySha256) { throw 'Security tool binary checksum mismatch.' }
    $result[$tool.name] = $binary
}
return $result
