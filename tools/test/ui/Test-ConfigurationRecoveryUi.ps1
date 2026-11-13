param([Parameter(Mandatory=$true)][string]$ApplicationPath,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$result=[ordered]@{Success=$false;Time=(Get-Date).ToString('o')}
$app=$null
$qaTemp=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-RecoveryUi-'+[guid]::NewGuid().ToString('N'))
$previousTemp=$env:TEMP
$previousTmp=$env:TMP
try{
    New-Item -ItemType Directory -Path $qaTemp | Out-Null
    # Block the old scratch database path while leaving isolated settings writable.
    New-Item -ItemType File -Path (Join-Path $qaTemp 'TunHotspot-Unavailable') | Out-Null
    $env:TEMP=$env:TMP=$qaTemp
    $app=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture','recovery-interactive' -PassThru
    $deadline=(Get-Date).AddSeconds(10);$root=$null
    do{Start-Sleep -Milliseconds 100;$app.Refresh();if($app.MainWindowHandle -ne [IntPtr]::Zero){$root=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)}}while(-not $root -and (Get-Date) -lt $deadline)
    if(-not $root){throw 'Recovery shell missing.'}
    function Find-Id([string]$id){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
    function Find-Name([string]$name){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))}
    $failureElement=$null
    do{Start-Sleep -Milliseconds 100;$failureElement=Find-Name '启动管理数据暂时不可用'}while(-not $failureElement -and (Get-Date) -lt $deadline)
    if(-not $failureElement){throw 'Did not report actual isolated database failure.'}
    foreach($workspace in @('Workspace.Startup','Workspace.Hotspot','Workspace.Configuration')){
        (Find-Id $workspace).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
        (Find-Id 'Workspace.Settings').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
        $settingsDeadline=(Get-Date).AddSeconds(2);$settingsTitle=$null;$material=$null
        do{Start-Sleep -Milliseconds 100;$settingsTitle=Find-Id 'Settings.Title';$material=Find-Id 'Settings.Material'}while(( -not$settingsTitle -or -not$material) -and (Get-Date) -lt $settingsDeadline)
        if(-not$settingsTitle -or -not$material -or -not$material.Current.IsEnabled){$fallback=Find-Id 'Error.Details';$diagnostic=if($fallback){$fallback.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}else{'no fallback'};throw ('Global settings failed from '+$workspace+': '+$diagnostic)}
    }
    (Find-Id 'Settings.CloseToTray').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    if(-not(Find-Id 'Settings.AutoStart').Current.IsEnabled){throw 'Login auto-start depends on failed startup database.'}
    $preferences=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$app.Id+'\preferences.json')
    if((Get-Content -LiteralPath $preferences -Raw | ConvertFrom-Json).closeToTray -ne $false){throw 'Global settings did not persist while startup data was broken.'}
    (Find-Id 'Workspace.Configuration').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 400
    if(-not(Find-Id 'Configuration.Template')){throw 'Configuration page depends on failed startup database.'}
    (Find-Id 'Workspace.Hotspot').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 250
    (Find-Id 'Workspace.Startup').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 250
    if(-not(Find-Name '启动管理数据暂时不可用')){throw 'Startup view displayed a false empty baseline.'}
    $fake=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$app.Id+'\toolbox.db')
    if((Get-Content -LiteralPath $fake -Raw) -ne 'TunHotspot isolated corruption fixture'){throw 'Damaged original fixture was replaced.'}
    if(-not $app.WaitForExit(35000)){throw 'Recovery UI QA timeout.'}
    $result.Success=$true;$result.Pid=$app.Id;$result.OriginalPreserved=$true;$result.ConfigurationIndependent=$true;$result.HotspotNavigation=$true;$result.GlobalSettingsFromAllWorkspaces=$true;$result.SettingsSavedWithBrokenDatabase=$true;$result.ScratchPathBlocked=$true
}catch{$result.Error=$_.Exception.Message}
finally{
    $env:TEMP=$previousTemp
    $env:TMP=$previousTmp
    if($app -and -not $app.HasExited){Stop-Process -Id $app.Id -Force}
}
$result | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json
if(-not $result.Success){exit 1}
