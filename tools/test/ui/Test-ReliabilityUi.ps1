param([Parameter(Mandatory=$true)][string]$ApplicationPath,[Parameter(Mandatory=$true)][string]$OutputPath,[string]$ScreenshotPath,[ValidateSet('All','CategorySeverity','EmptyIntersection','Keyword')][string]$Scenario='All')
$ErrorActionPreference='Stop'
if($Scenario -eq 'All'){
 $checks=@()
 foreach($case in @('CategorySeverity','EmptyIntersection','Keyword')){
  $casePath=$OutputPath+'.'+$case+'.json'
  $arguments=@{ApplicationPath=$ApplicationPath;OutputPath=$casePath;Scenario=$case}
  if($ScreenshotPath -and $case -eq 'CategorySeverity'){$arguments.ScreenshotPath=$ScreenshotPath}
  & $PSCommandPath @arguments
  if($LASTEXITCODE){throw ('Reliability scenario failed: '+$case)}
  $checks+=Get-Content -LiteralPath $casePath -Raw | ConvertFrom-Json
 }
 $combined=[ordered]@{Success=$true;Time=(Get-Date).ToString('o');Scenarios=$checks;LoginToggleAvailable=$true;RealStartupUnchanged=$true;CategoryAndSeverityFilter=$true;EmptyIntersection=$true;KeywordFilter=$true}
 $combined | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
 $combined | ConvertTo-Json -Depth 6
 return
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
. (Join-Path $PSScriptRoot 'UiAutomation.ps1')
$result=[ordered]@{Success=$false;Time=(Get-Date).ToString('o')};$app=$null
try{
 $before=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TunHotspot.Toolbox.Agent' -ErrorAction SilentlyContinue).'TunHotspot.Toolbox.Agent'
 $app=Start-Process -FilePath $ApplicationPath -ArgumentList '--qa-capture','reliability-interactive' -PassThru
 $result.Pid=$app.Id
 $deadline=(Get-Date).AddSeconds(12);$root=$null
 do{Start-Sleep -Milliseconds 100;$app.Refresh();if($app.MainWindowHandle -ne [IntPtr]::Zero){$root=[Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)}}while(-not$root -and (Get-Date) -lt $deadline)
 function Find-Id([string]$id){$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
 $login=$null;do{Start-Sleep -Milliseconds 100;$login=Find-Id 'Settings.AutoStart'}while(-not$login -and (Get-Date) -lt $deadline)
 if(-not$login -or -not$login.Current.IsEnabled){throw 'Independent login toggle missing.'}
 $login.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle();Start-Sleep -Milliseconds 150
 $after=(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TunHotspot.Toolbox.Agent' -ErrorAction SilentlyContinue).'TunHotspot.Toolbox.Agent'
 if($before -ne $after){throw 'QA changed actual login registration.'}
 function Select-Item([string]$id,[string]$name){
  $result.CurrentControl=$id+' / '+$name
  $combo=Find-Id $id
  $ancestor=[Windows.Automation.TreeWalker]::RawViewWalker.GetParent($combo)
  while($ancestor){
   $containerScroll=$null
   if($ancestor.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$containerScroll) -and $containerScroll.Current.VerticallyScrollable){
    $viewport=$ancestor.Current.BoundingRectangle;$position=$combo.Current.BoundingRectangle
    $scrollablePixels=$viewport.Height*(100/$containerScroll.Current.VerticalViewSize-1)
    if($scrollablePixels -gt 0){
     $desiredTop=$viewport.Top+$viewport.Height*0.35
     $percent=[Math]::Clamp($containerScroll.Current.VerticalScrollPercent+100*($position.Top-$desiredTop)/$scrollablePixels,0,100)
     $containerScroll.SetScrollPercent(-1,$percent);Start-Sleep -Milliseconds 250
    }
    break
   }
   $ancestor=[Windows.Automation.TreeWalker]::RawViewWalker.GetParent($ancestor)
  }
  $combo=Find-Id $id
  $result.ComboBounds=$combo.Current.BoundingRectangle.ToString();$result.ComboOffscreen=$combo.Current.IsOffscreen
  try{$combo.SetFocus()}catch{}
  $expand=$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
  if($expand.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded){
   try{$expand.Expand()}catch{
    $latest=(Find-Id $id).GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if($latest.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Expanded){throw}
   }
  }
  $condition=[Windows.Automation.AndCondition]::new([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsOffscreenProperty,$false))
  $choiceDeadline=(Get-Date).AddSeconds(3);$item=$null
  do{Start-Sleep -Milliseconds 100;$item=Find-ProcessUiElement -OwnerProcessId $app.Id -Condition $condition}while(-not$item -and (Get-Date) -lt $choiceDeadline)
  if(-not$item){throw ('Combo choice missing: '+$name)}
  $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 100
  # WinUI may close the popup as part of selection. Requery its current provider;
  # a second Collapse must not turn an already completed selection into a failure.
  $expand=(Find-Id $id).GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
  if($expand.Current.ExpandCollapseState -eq [Windows.Automation.ExpandCollapseState]::Expanded){
   try{$expand.Collapse()}catch{
    $latest=(Find-Id $id).GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    if($latest.Current.ExpandCollapseState -ne [Windows.Automation.ExpandCollapseState]::Collapsed){throw}
   }
  }
 }
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)
 if($Scenario -eq 'CategorySeverity'){
  Select-Item 'Logs.Category' '热点';Select-Item 'Logs.Level' '警告'
  $rows=(Find-Id 'Logs.List').FindAll([Windows.Automation.TreeScope]::Children,$condition)
  if($rows.Count -ne 1 -or $rows[0].Current.Name -notlike '*QA 热点过滤测试*'){throw 'Category + severity filter failed.'}
 }
 elseif($Scenario -eq 'EmptyIntersection'){
  Select-Item 'Logs.Category' '设置';Select-Item 'Logs.Level' '警告'
  if((Find-Id 'Logs.List').FindAll([Windows.Automation.TreeScope]::Children,$condition).Count -ne 0){throw 'Empty intersection not shown.'}
 }
 else{
  Select-Item 'Logs.Category' '设置';Select-Item 'Logs.Level' '信息'
  $result.CurrentControl='Logs.Search'
  (Find-Id 'Logs.Search').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('QA 设置过滤测试');Start-Sleep -Milliseconds 100
  $rows=(Find-Id 'Logs.List').FindAll([Windows.Automation.TreeScope]::Children,$condition)
  if($rows.Count -ne 1){throw 'Keyword filter failed.'}
 }
 if(-not$app.WaitForExit(35000)){throw 'Reliability UI deadline exceeded.'}
 if($ScreenshotPath){$capture=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$app.Id+'\qa\settings-logs.png');if(-not(Test-Path -LiteralPath $capture)){throw 'Actual XAML log capture missing.'};Copy-Item -LiteralPath $capture -Destination $ScreenshotPath -Force}
 $result.Success=$true;$result.Pid=$app.Id;$result.LoginToggleAvailable=$true;$result.RealStartupUnchanged=$true;$result.Scenario=$Scenario
}catch{$result.Error=$_.Exception.Message}
finally{if($app -and -not$app.HasExited){Stop-Process -Id $app.Id -Force}}
$result | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
$result | ConvertTo-Json
if(-not$result.Success){exit 1}
