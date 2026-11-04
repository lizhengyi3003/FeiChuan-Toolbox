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
