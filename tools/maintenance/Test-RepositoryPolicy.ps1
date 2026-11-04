param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$policy = Get-Content -LiteralPath (Join-Path $root '.github\repository-policy.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI with repository administration read access is required.' }
function Read-Policy([string]$path) {
    $result = & gh api ('repos/' + $policy.repository + $path)
    if ($LASTEXITCODE) { throw ('Cannot read policy: ' + $path) }
    if ($result) { return (($result -join "`n") | ConvertFrom-Json -AsHashtable) }
    return @{}
}
function Test-Subset($expected, $actual) {
    if ($null -eq $expected) { return $null -eq $actual }
    if ($expected -is [Collections.IDictionary]) {
        foreach ($key in $expected.Keys) {
            if ($null -eq $actual -or -not $actual.Contains($key) -or -not (Test-Subset $expected[$key] $actual[$key])) { return $false }
        }
        return $true
    }
    if ($expected -is [array]) {
        if (@($expected).Count -ne @($actual).Count) { return $false }
        foreach ($entry in $expected) {
            if (-not @($actual | Where-Object { Test-Subset $entry $_ }).Count) { return $false }
        }
        return $true
    }
    return $expected -ceq $actual
}
$failures = [Collections.Generic.List[string]]::new()
$repository = Read-Policy ''
if (-not (Test-Subset $policy.settings $repository)) { $failures.Add('Repository settings') }
foreach ($request in $policy.requests) {
    $path = $request.path
    if ($request.method -eq 'PATCH') {
        if (-not (Test-Subset $request.body $repository)) { $failures.Add('Security settings') }
        continue
    }
    $actual = Read-Policy $path
    $expected = $request.body
    if ($path -eq '/topics') { $expected = @{names=$request.body.names} }
    elseif ($path -eq '/vulnerability-alerts') { continue } # GET must succeed (204 when enabled).
    elseif ($path -in @('/automated-security-fixes','/private-vulnerability-reporting','/immutable-releases')) { $expected = @{enabled=$true} }
    elseif ($path -eq '/environments/release') {
        $expected = @{can_admins_bypass=$request.body.can_admins_bypass;deployment_branch_policy=$request.body.deployment_branch_policy}
        $review = @($actual.protection_rules | Where-Object { $_.type -eq 'required_reviewers' })
        $reviewers = @($review.reviewers | ForEach-Object { @{type=$_.type;id=$_.reviewer.id} })
        if ($review.Count -ne 1 -or -not (Test-Subset $request.body.reviewers $reviewers) -or $review[0].prevent_self_review -ne $request.body.prevent_self_review) { $failures.Add('Release reviewer policy') }
    }
    if ($null -ne $expected -and -not (Test-Subset $expected $actual)) { $failures.Add($path) }
}
$remoteRules = @(Read-Policy '/rulesets')
foreach ($rule in $policy.rulesets) {
    $found = @($remoteRules | Where-Object { $_.name -ceq $rule.name })
    if ($found.Count -ne 1 -or -not (Test-Subset $rule (Read-Policy ('/rulesets/' + $found[0].id)))) { $failures.Add('Ruleset: ' + $rule.name) }
}
$branches = Read-Policy '/environments/release/deployment-branch-policies'
if (-not @($branches.branch_policies | Where-Object { $_.name -ceq 'v*' -and $_.type -eq 'tag' }).Count) { $failures.Add('Release tag scope') }
$labels = @(Read-Policy '/labels?per_page=100')
foreach ($label in $policy.labels) {
    if (-not @($labels | Where-Object { Test-Subset $label $_ }).Count) { $failures.Add('Label: ' + $label.name) }
}
if ($failures.Count) { throw ('Repository policy differs: ' + ($failures -join ', ')) }
foreach ($check in $policy.manual_checks) {
    if ($repository.security_and_analysis[$check.field].status -cne $check.desired_status) {
        Write-Warning ('Manual security setting remains pending: ' + $check.field + '. ' + $check.reason)
    }
}
Write-Output 'Repository, security, Actions, rulesets and release approval settings match the declared policy.'
