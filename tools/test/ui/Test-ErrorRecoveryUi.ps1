param([Parameter(Mandatory=$true)][string]$ApplicationPath,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$result=[ordered]@{Success=$false;Time=(Get-Date).ToString('o')};$app=$null
try{
    $app=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture','page-error-interactive' -PassThru
    $deadline=(Get-Date).AddSeconds(10);$root=$null
    do{Start-Sleep -Milliseconds 100;$app.Refresh();if($app.MainWindowHandle -ne [IntPtr]::Zero){$root=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)}}while(-not$root -and (Get-Date) -lt $deadline)
    if(-not$root){throw 'QA window missing.'}
    function Find-Id([string]$id){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
    $details=$null
    do{Start-Sleep -Milliseconds 100;$details=Find-Id 'Error.Details'}while(-not$details -and (Get-Date) -lt $deadline)
    if(-not$details){throw 'Page failure fallback missing.'}
    $text=$details.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if($text -notmatch '错误编号：([a-f0-9]{12})'){throw 'Incident identity missing.'};$incident=$matches[1]
    $diagnostic=Get-ChildItem -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$app.Id+'\diagnostics')) -Filter ('*'+$incident+'.json')
    if(-not$diagnostic){throw 'Error was not persisted independently.'}
    (Find-Id 'Error.Retry').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    if(Find-Id 'Error.Details'){throw 'Retry did not reload the failed page.'}
    (Find-Id 'Workspace.Settings').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 250
    $history=(Find-Id 'Settings.Diagnostics').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    if($history -notlike ('*'+$incident+'*')){throw 'Incident missing from global settings history.'}
    if(-not$app.WaitForExit(22000)){throw 'QA shutdown timeout.'}
    $result.Success=$true;$result.IncidentId=$incident;$result.LogSaved=$true;$result.ReadRetryPassed=$true;$result.SettingsHistoryPassed=$true
}catch{$result.Error=$_.Exception.Message}
finally{if($app -and -not$app.HasExited){Stop-Process -Id $app.Id -Force}}
$result | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json
if(-not$result.Success){exit 1}
