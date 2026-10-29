param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$Publish,[switch]$Offline)
$ErrorActionPreference='Stop'
$localSdk=Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
$systemDotnet=Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet=$null
if($systemDotnet -and @(& $systemDotnet.Source --list-sdks | Where-Object {$_ -match '^(1[0-9]|[2-9][0-9])\.'}).Count){$dotnet=$systemDotnet.Source}
elseif(Test-Path -LiteralPath $localSdk){$dotnet=$localSdk}
if(-not $dotnet){throw '需要 .NET 10 SDK。请在 Visual Studio Installer 中安装 .NET 桌面/WinUI 开发组件。'}
$priorPath=$env:PATH
$priorRoot=$env:DOTNET_ROOT
$priorRootX64=$env:DOTNET_ROOT_X64
$priorWorkloadResolver=$env:MSBuildEnableWorkloadResolver
$priorTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
try {
    $env:DOTNET_ROOT=Split-Path -Parent $dotnet
    $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
    $env:PATH=$env:DOTNET_ROOT+';'+$priorPath
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:MSBuildEnableWorkloadResolver='false'
    $project=Join-Path $PSScriptRoot 'src\TunHotspot.WinUI\TunHotspot.WinUI.csproj'
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '.tools\winui-build-tools\feed') -Force | Out-Null
    $restoreArgs=@('restore',$project,'-p:Platform=x64')
    if($Offline){$restoreArgs+=@('--source',(Join-Path $PSScriptRoot '.tools\winui-build-tools\feed'))}
    & $dotnet @restoreArgs
    if($LASTEXITCODE){throw 'NuGet 还原失败。'}
    if($Publish){
        & $dotnet publish $project -c $Configuration -p:Platform=x64 --no-restore -o (Join-Path $PSScriptRoot 'artifacts\win-x64')
    }else{
        & $dotnet build $project -c $Configuration -p:Platform=x64 --no-restore
    }
    if($LASTEXITCODE){throw '编译失败。'}
} finally {
    $env:PATH=$priorPath
    $env:DOTNET_ROOT=$priorRoot
    $env:DOTNET_ROOT_X64=$priorRootX64
    $env:MSBuildEnableWorkloadResolver=$priorWorkloadResolver
    $env:DOTNET_CLI_TELEMETRY_OPTOUT=$priorTelemetry
}
