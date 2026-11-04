param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Offline,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
& (Join-Path $repositoryRoot 'tools\test\Test-Repository.ps1')
. (Join-Path $repositoryRoot 'tools\common\DotNet.ps1')

Invoke-WithProjectDotNet -RepositoryRoot $repositoryRoot -Action {
    param($dotnet)
    $project = Join-Path $repositoryRoot 'tests\TunHotspot.Tests\TunHotspot.Tests.csproj'
    if (-not $NoRestore) {
        $restoreArgs = @('restore', $project, '-p:Platform=x64')
        if ($Offline) { $restoreArgs += @('--source', (Join-Path $repositoryRoot '.tools\winui-build-tools\feed')) }
        & $dotnet @restoreArgs
        if ($LASTEXITCODE) { throw '功能检查项目依赖还原失败。' }
    }
    & $dotnet run --project $project -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE) { throw '功能检查失败。' }
}
