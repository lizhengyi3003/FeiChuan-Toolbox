param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$policy = Get-Content -LiteralPath (Join-Path $root '.github\repository-policy.json') -Raw | ConvertFrom-Json
if (-not $Apply) { $policy | ConvertTo-Json -Depth 30; return }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Install GitHub CLI and authenticate with repository administration permissions.' }
$temporary = Join-Path $root ('artifacts\temp\policy-' + [Guid]::NewGuid().ToString('N') + '.json')
[IO.Directory]::CreateDirectory((Split-Path -Parent $temporary)) | Out-Null
function Invoke-Policy([string]$method, [string]$path, $body = $null) {
    $arguments = @('api', '--method', $method, ('repos/' + $policy.repository + $path))
    if ($null -ne $body) {
        $body | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $temporary -Encoding utf8
        $arguments += @('--input', $temporary)
    }
    $result = & gh @arguments
    if ($LASTEXITCODE) { throw ('GitHub policy request failed: ' + $method + ' ' + $path) }
    if ($result) { return (($result -join "`n") | ConvertFrom-Json) }
}
try {
    $null = Invoke-Policy PATCH '' $policy.settings
    foreach ($request in $policy.requests) { $null = Invoke-Policy $request.method $request.path $request.body }
    $labels = @(Invoke-Policy GET '/labels?per_page=100')
    foreach ($label in $policy.labels) {
        if (-not @($labels | Where-Object { $_.name -eq $label.name }).Count) { $null = Invoke-Policy POST '/labels' $label }
    }
    $environmentRules = @(Invoke-Policy GET '/environments/release/deployment-branch-policies')
    if (-not @($environmentRules.branch_policies | Where-Object { $_.name -eq 'v*' -and $_.type -eq 'tag' }).Count) { $null = Invoke-Policy POST '/environments/release/deployment-branch-policies' @{ name = 'v*'; type = 'tag' } }
    $existing = @(Invoke-Policy GET '/rulesets')
    foreach ($rule in $policy.rulesets) {
        $match = @($existing | Where-Object { $_.name -eq $rule.name })
        if ($match.Count -gt 1) { throw 'Ambiguous existing rulesets; inspect before changing.' }
        if ($match.Count) { $null = Invoke-Policy PUT ('/rulesets/' + $match[0].id) $rule }
        else { $null = Invoke-Policy POST '/rulesets' $rule }
    }
    Write-Output 'Repository policy applied. Verify platform state and required checks before publishing.'
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
