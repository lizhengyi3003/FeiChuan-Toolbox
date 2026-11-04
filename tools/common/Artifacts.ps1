function Get-DistributionNames {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot, [Parameter(Mandatory = $true)][string]$Version)
    [xml]$product = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
    $prefix = [string]$product.Project.PropertyGroup.DistributionPrefix
    if ($prefix -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]+$' -or $Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -ne [string]$product.Project.PropertyGroup.Version) { throw 'Invalid distribution prefix/version.' }
    return [pscustomobject]@{ Prefix = $prefix; Installer = "$prefix-$Version-Setup-x64.exe"; Portable = "$prefix-$Version-win11-x64.zip" }
}

function Get-ArtifactLayout {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts'))
    [pscustomobject]@{
        Root = $artifactRoot
        Current = Join-Path $artifactRoot 'current'
        Archive = Join-Path $artifactRoot 'archive'
        Reports = Join-Path $artifactRoot 'reports'
        Backups = Join-Path $artifactRoot 'backups'
        Temp = Join-Path $artifactRoot 'temp'
        PreviewApp = Join-Path $artifactRoot 'temp\preview\app'
        PreviewPackages = Join-Path $artifactRoot 'temp\preview\packages'
    }
}
