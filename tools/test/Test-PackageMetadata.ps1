param([Parameter(Mandatory = $true)][string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$release = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'release.json') -Raw | ConvertFrom-Json
$packages = Join-Path $ReleaseDirectory 'packages'
$setup = [IO.Path]::GetFileName($release.installer.file); $zip = [IO.Path]::GetFileName($release.portable.file)
$expected = @($setup, ($setup + '.sha256'), $zip, ($zip + '.sha256'), 'release.json', 'sbom.cdx.json', 'feichuan-toolbox.scoop.json', 'lizhengyi3003.FeiChuanToolbox.yaml', 'SHA256SUMS.txt')
$actual = @(Get-ChildItem -LiteralPath $packages -File | ForEach-Object Name)
if ($actual.Count -ne $expected.Count -or @(Compare-Object $expected $actual).Count) { throw 'Public assets differ from the explicit allowlist.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  ([^/\\]+)$') { throw 'Invalid asset checksum entry.' }
    $hash = $matches[1]; $name = $matches[2]
    if ($name -notin $expected -or $name -eq 'SHA256SUMS.txt' -or -not $seen.Add($name)) { throw 'Unexpected or duplicate asset checksum entry.' }
    if ((Get-FileHash -LiteralPath (Join-Path $packages $name)).Hash.ToLowerInvariant() -ne $hash) { throw ('Asset checksum mismatch: ' + $name) }
}
if ($seen.Count -ne $expected.Count - 1) { throw 'Asset checksum list incomplete.' }
if ((Get-FileHash -LiteralPath (Join-Path $packages 'release.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'release.json')).Hash) { throw 'Public provenance metadata differs from verified release.' }
$bom = Get-Content -LiteralPath (Join-Path $packages 'sbom.cdx.json') -Raw | ConvertFrom-Json
if ($bom.bomFormat -ne 'CycloneDX' -or $bom.specVersion -ne '1.6' -or $bom.metadata.component.version -ne $release.version -or $release.sourceDirty) { throw 'Invalid SBOM or uncommitted source.' }
$references = @($bom.metadata.component.'bom-ref') + @($bom.components | ForEach-Object { $_.'bom-ref' })
if (@($references | Sort-Object -Unique).Count -ne $references.Count) { throw 'Duplicate SBOM reference.' }
foreach ($dependency in $bom.dependencies) { foreach ($ref in @($dependency.ref) + @($dependency.dependsOn)) { if ($ref -notin $references) { throw 'Unresolved SBOM dependency.' } } }
$scoop = Get-Content -LiteralPath (Join-Path $packages 'feichuan-toolbox.scoop.json') -Raw | ConvertFrom-Json
$base = 'https://github.com/lizhengyi3003/FeiChuan-Toolbox/releases/download/v' + $release.version + '/'
if ($scoop.version -ne $release.version -or $scoop.architecture.'64bit'.hash -ne $release.portable.sha256 -or $scoop.architecture.'64bit'.url -ne ($base + [Uri]::EscapeDataString($zip))) { throw 'Scoop package identity/hash mismatch.' }
if ($scoop.shortcuts.Count -ne 1 -or $scoop.shortcuts[0] -isnot [array] -or $scoop.shortcuts[0].Count -ne 2 -or $scoop.shortcuts[0][0] -ne 'TunHotspot.exe') { throw 'Invalid Scoop shortcut declaration.' }
$winget = Get-Content -LiteralPath (Join-Path $packages 'lizhengyi3003.FeiChuanToolbox.yaml') -Raw
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$definition = Get-Content -LiteralPath (Join-Path $root 'installer\TunHotspot.iss') -Raw
$publisher = [regex]::Match($definition, '(?m)^AppPublisher=(.+)').Groups[1].Value.Trim()
$applicationId = [regex]::Match($definition, '(?m)^AppId=\{\{([A-F0-9-]{36})\}').Groups[1].Value
if (-not $publisher -or -not $applicationId -or -not $winget.Contains('Publisher: ' + $publisher) -or -not $winget.Contains("ProductCode: '{" + $applicationId + "}_is1'")) { throw 'WinGet identity differs from installed app registration.' }
foreach ($required in @(('PackageVersion: ' + $release.version), ('    InstallerUrl: ' + $base + [Uri]::EscapeDataString($setup)), ('    InstallerSha256: ' + $release.installer.sha256.ToUpperInvariant()))) { if (-not $winget.Contains($required)) { throw 'WinGet package identity/hash mismatch.' } }
Write-Output ('Verified public metadata and ' + $expected.Count + ' allowlisted assets.')
