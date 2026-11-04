function Get-ProjectDotNet {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    $localDotnet = Join-Path $RepositoryRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) { return $localDotnet }
    $systemDotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($systemDotnet -and @(& $systemDotnet.Source --list-sdks | Where-Object { $_ -match '^(1[0-9]|[2-9][0-9])\.' }).Count) {
        return $systemDotnet.Source
    }
    throw '请安装 global.json 指定的 .NET 10 稳定版 SDK，以及 Windows SDK 26100 / WinUI 构建组件。'
}

function Invoke-WithProjectDotNet {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $dotnet = Get-ProjectDotNet -RepositoryRoot $RepositoryRoot
    $savedEnvironment = @{}
    foreach ($name in @('PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'MSBuildEnableWorkloadResolver', 'DOTNET_CLI_TELEMETRY_OPTOUT')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    try {
        $env:DOTNET_ROOT = Split-Path -Parent $dotnet
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $env:PATH = $env:DOTNET_ROOT + ';' + $savedEnvironment['PATH']
        $env:MSBuildEnableWorkloadResolver = 'false'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        & $Action $dotnet
    }
    finally {
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
}
