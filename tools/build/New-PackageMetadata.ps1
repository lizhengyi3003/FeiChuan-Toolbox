param([Parameter(Mandatory = $true)][string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$release = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'release.json') -Raw | ConvertFrom-Json
if ($release.sourceDirty) { throw 'Public package metadata requires committed source.' }
$version = $release.version
$packages = Join-Path $ReleaseDirectory 'packages'
$sourceCommit = [string]$release.sourceCommit
$libraries = @{}; $edges = @{}
foreach ($project in @('TunHotspot.WinUI', 'TunHotspot.Engine')) {
    $assets = Get-Content -LiteralPath (Join-Path $root ('src\' + $project + '\obj\project.assets.json')) -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' }) { $libraries[$library.Name] = $library.Value }
    foreach ($target in $assets.targets.PSObject.Properties.Value) {
        foreach ($library in $target.PSObject.Properties) {
            if (-not $edges.ContainsKey($library.Name)) { $edges[$library.Name] = [Collections.Generic.HashSet[string]]::new() }
            foreach ($dependency in $library.Value.dependencies.PSObject.Properties.Name) { [void]$edges[$library.Name].Add($dependency) }
        }
    }
}
function Purl([string]$key) { $id, $v = $key.Split('/', 2); return 'pkg:nuget/' + [Uri]::EscapeDataString($id) + '@' + $v }
$components = @(); $dependencies = @()
foreach ($key in $libraries.Keys | Sort-Object) {
    $id, $v = $key.Split('/', 2)
    $component = [ordered]@{ type = 'library'; 'bom-ref' = (Purl $key); name = $id; version = $v; purl = (Purl $key); properties = @(@{ name = 'feichuan:inventory'; value = 'resolved-build-dependency' }) }
    if ($libraries[$key].sha512) { $component.hashes = @(@{ alg = 'SHA-512'; content = [Convert]::ToHexString([Convert]::FromBase64String($libraries[$key].sha512)).ToLowerInvariant() }) }
    $components += $component
    $resolved = @($edges[$key] | ForEach-Object { $name = $_; $libraries.Keys | Where-Object { $_.Split('/')[0] -eq $name } } | Sort-Object -Unique | ForEach-Object { Purl $_ })
    $dependencies += @{ ref = (Purl $key); dependsOn = $resolved }
}
$sbom = [ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.6'; serialNumber = 'urn:uuid:' + [Guid]::NewGuid(); version = 1
    metadata = @{ timestamp = [DateTimeOffset]::UtcNow.ToString('o'); component = @{ type = 'application'; name = 'FeiChuan-Toolbox'; version = $version; licenses = @(@{ expression = 'GPL-3.0-only' }); properties = @(@{ name = 'feichuan:source-commit'; value = $sourceCommit }) } }
    components = $components; dependencies = $dependencies }
$sbom | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $packages 'sbom.cdx.json') -Encoding utf8
$base = 'https://github.com/lizhengyi3003/FeiChuan-Toolbox/releases/download/v' + $version + '/'
$installerUrl = $base + [Uri]::EscapeDataString([IO.Path]::GetFileName($release.installer.file))
$zipUrl = $base + [Uri]::EscapeDataString([IO.Path]::GetFileName($release.portable.file))
$scoop = [ordered]@{ version = $version; description = 'Windows 11 WinUI 3 toolbox for startup management, hotspot and Kumiryo configuration'; homepage = 'https://github.com/lizhengyi3003/FeiChuan-Toolbox'; license = 'GPL-3.0-only'
    architecture = @{ '64bit' = @{ url = $zipUrl; hash = $release.portable.sha256 } }; shortcuts = @(@('TunHotspot.exe', '飞船的小工具箱')); checkver = @{ github = 'https://github.com/lizhengyi3003/FeiChuan-Toolbox' }
    autoupdate = @{ architecture = @{ '64bit' = @{ url = 'https://github.com/lizhengyi3003/FeiChuan-Toolbox/releases/download/v$version/飞船的小工具箱-$version-win11-x64.zip' } } } }
$scoop | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $packages 'feichuan-toolbox.scoop.json') -Encoding utf8
$winget = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.singleton.1.10.0.schema.json
PackageIdentifier: lizhengyi3003.FeiChuanToolbox
PackageVersion: $version
PackageLocale: zh-CN
Publisher: lizhengyi3003
PackageName: 飞船的小工具箱
License: GPL-3.0-only
LicenseUrl: https://github.com/lizhengyi3003/FeiChuan-Toolbox/blob/v$version/LICENSE
ShortDescription: Windows 11 启动管理、兼容热点与 Kumiryo 配置工具箱
PackageUrl: https://github.com/lizhengyi3003/FeiChuan-Toolbox
InstallerType: inno
Scope: user
InstallerSwitches:
  Custom: /CLOSEOWNAPPS=1
MinimumOSVersion: 10.0.22000.0
Installers:
  - Architecture: x64
    InstallerUrl: $installerUrl
    InstallerSha256: $($release.installer.sha256.ToUpperInvariant())
ManifestType: singleton
ManifestVersion: 1.10.0
"@
$winget | Set-Content -LiteralPath (Join-Path $packages 'lizhengyi3003.FeiChuanToolbox.yaml') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $ReleaseDirectory 'release.json') -Destination (Join-Path $packages 'release.json')
Get-ChildItem -LiteralPath $packages -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name } | Set-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt') -Encoding utf8
Write-Output ('Package inventory: ' + $components.Count + ' resolved dependencies; manifests and SHA256SUMS created.')
