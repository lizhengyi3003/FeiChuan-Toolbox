param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $root 'tools\common\DotNet.ps1')
Invoke-WithProjectDotNet -RepositoryRoot $root -Action {
    param($dotnet)
    $output = Join-Path $root 'artifacts\temp\dependency-audit'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $root 'src'), (Join-Path $root 'tests') -Recurse -Filter '*.csproj' -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) {
        & $dotnet restore $project.FullName -p:Platform=x64 --locked-mode -p:NuGetAudit=true -p:NuGetAuditMode=all
        if ($LASTEXITCODE) { throw 'Locked dependency restore/audit failed.' }
        $json = & $dotnet list $project.FullName package --vulnerable --include-transitive --format json --no-restore
        if ($LASTEXITCODE) { throw 'Dependency vulnerability query failed.' }
        $result = ($json -join "`n") | ConvertFrom-Json
        if ($result.problems) { throw 'Vulnerability data source reported a problem.' }
        $findings = @($result.projects | ForEach-Object { $_.frameworks } | ForEach-Object { @($_.topLevelPackages) + @($_.transitivePackages) } | Where-Object { $_.vulnerabilities })
        $json | Set-Content -LiteralPath (Join-Path $output ($project.BaseName + '.json')) -Encoding utf8
        if ($findings.Count) { throw 'Known vulnerable dependencies detected; inspect dependency-audit.' }
    }
}
