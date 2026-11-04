param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$tools = & (Join-Path $PSScriptRoot 'Initialize-SecurityTools.ps1')
$workflows = @(Get-ChildItem -LiteralPath (Join-Path $root '.github\workflows') -Filter '*.yml' -File)
if (-not $workflows.Count) { throw 'No workflows found.' }
Push-Location $root
try {
    & $tools.actionlint -color @($workflows.FullName)
    if ($LASTEXITCODE) { throw 'Workflow syntax/type validation failed.' }
    foreach ($workflow in $workflows) {
        $text = Get-Content -LiteralPath $workflow.FullName -Raw
        if ($text -match 'pull_request_target|permissions:\s*write-all') { throw 'Unsafe workflow privilege pattern.' }
        foreach ($match in [regex]::Matches($text, '(?m)^\s*-?\s*uses:\s*([^\s#]+)')) {
            if ($match.Groups[1].Value -notmatch '^\./' -and $match.Groups[1].Value -notmatch '^[\w.-]+/[\w./-]+@[a-f0-9]{40}$') { throw 'External actions must use full commit SHA pins.' }
        }
    }
} finally { Pop-Location }
Write-Output ('Validated workflows: ' + $workflows.Count)
