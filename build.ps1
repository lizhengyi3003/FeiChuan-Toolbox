param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$Offline,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
. (Join-Path $repositoryRoot 'tools\common\DotNet.ps1')
. (Join-Path $repositoryRoot 'tools\common\Artifacts.ps1')
$artifactLayout = Get-ArtifactLayout $repositoryRoot

Invoke-WithProjectDotNet -RepositoryRoot $repositoryRoot -Action {
    param($dotnet)
    $projects = @(
        (Join-Path $repositoryRoot 'src\TunHotspot.WinUI\TunHotspot.WinUI.csproj'),
        (Join-Path $repositoryRoot 'src\TunHotspot.Engine\TunHotspot.Engine.csproj')
    )
    foreach ($project in $projects) {
        $restoreArgs = @('restore', $project, '-p:Platform=x64')
        if ($Offline) { $restoreArgs += @('--source', (Join-Path $repositoryRoot '.tools\winui-build-tools\feed')) }
        & $dotnet @restoreArgs
        if ($LASTEXITCODE) { throw "依赖还原失败：$project" }
    }
    if ($Publish) {
        if (-not $OutputDirectory) { $OutputDirectory = $artifactLayout.PreviewApp }
        $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
        & $dotnet publish $projects[0] -c $Configuration -p:Platform=x64 --no-restore -o $OutputDirectory
        if ($LASTEXITCODE) { throw '界面发布失败。' }
        & $dotnet publish $projects[1] -c $Configuration -p:Platform=x64 --no-restore -o (Join-Path $OutputDirectory 'Engine')
        if ($LASTEXITCODE) { throw '采集引擎发布失败。' }
        & (Join-Path $repositoryRoot 'tools\build\Prepare-Distribution.ps1') -PublishDirectory $OutputDirectory
    }
    else {
        foreach ($project in $projects) {
            & $dotnet build $project -c $Configuration -p:Platform=x64 --no-restore
            if ($LASTEXITCODE) { throw "编译失败：$project" }
        }
    }
}
