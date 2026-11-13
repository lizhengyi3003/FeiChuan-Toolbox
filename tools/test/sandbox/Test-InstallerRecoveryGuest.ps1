$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path -LiteralPath 'C:\TestScripts\run.ps1')){throw 'Only the dedicated disposable Windows Sandbox is allowed.'}
$output='C:\TestResults';$install='C:\Toolbox Upgrade Simulation\app'
$localInput=Join-Path $env:LOCALAPPDATA ('TunHotspot-InstallerTest-'+[guid]::NewGuid().ToString('N'))
$oldSetup=Join-Path $localInput 'previous-setup.exe';$newSetup=Join-Path $localInput 'candidate-setup.exe'
$result=[ordered]@{Success=$false;Cases=@();HostInstallationTouched=$false};$worker=$null
function Setup([string]$file,[string]$name,[string[]]$extra=@()){
 $log=Join-Path $output ($name+'.log')
 $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=""',('/DIR="'+$install+'"'),('/LOG="'+$log+'"'))+$extra
 $process=Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
 if(-not $process.WaitForExit(180000)){throw ('Installer timeout: '+$name)}
 $process.Refresh();return [int]$process.ExitCode
}
function Snapshot{
 $items=@();foreach($file in Get-ChildItem -LiteralPath $install -Recurse -File){
  if($file.Name -eq '.toolbox-installing.json'){continue}
  $items+=@{Path=$file.FullName.Substring($install.Length+1);Hash=(Get-FileHash -LiteralPath $file.FullName).Hash}
 };return $items
}
function Same($left,$right){
 if($left.Count -ne $right.Count){return $false}
 foreach($file in $left){if(($right | Where-Object Path -eq $file.Path).Hash -ne $file.Hash){return $false}};return $true
}
function Verify-Manifest{
 $count=0;foreach($line in Get-Content -LiteralPath (Join-Path $install 'files.sha256')){
  if($line -notmatch '^([a-f0-9]{64})  (.+)$'){throw 'Bad manifest'}
  $hash=$matches[1];$relative=$matches[2]
  if((Get-FileHash -LiteralPath (Join-Path $install $relative)).Hash.ToLowerInvariant() -ne $hash){throw ('Installed file differs: '+$relative)};$count++
 };return $count
}
function Seed{
 $exit=Setup $oldSetup 'seed'
 if($exit -ne 0){throw "Baseline installer failed before upgrade verification (exit $exit)."}
}
function Start-Mcp{
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName=Join-Path $install 'Engine\TunHotspot.Engine.exe';$start.Arguments='--mcp';$start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $script:worker=[Diagnostics.Process]::Start($start);Start-Sleep -Seconds 2
 if($worker.HasExited){throw 'MCP fixture not running'}
}
function Start-ExternalLock {
 $fixture=Join-Path $output 'lock-fixture.ps1'
 @'
$stream=[IO.File]::Open('C:\Toolbox Upgrade Simulation\app\Engine\clrjit.dll',[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{[IO.File]::WriteAllText('C:\TestResults\lock-ready.txt','ready');Start-Sleep -Seconds 150}finally{$stream.Dispose()}
'@ | Set-Content -LiteralPath $fixture -Encoding ascii
 $process=Start-Process -FilePath 'powershell.exe' -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "'+$fixture+'"') -WindowStyle Hidden -PassThru
 for($i=0;$i -lt 100 -and -not(Test-Path -LiteralPath (Join-Path $output 'lock-ready.txt'));$i++){Start-Sleep -Milliseconds 100}
 if($process.HasExited -or -not(Test-Path -LiteralPath (Join-Path $output 'lock-ready.txt'))){throw 'Lock fixture failed'}
 return $process
}
try{
 New-Item -ItemType Directory -Path $localInput | Out-Null
 Copy-Item -LiteralPath 'C:\TestScripts\previous-setup.exe' -Destination $oldSetup
 Copy-Item -LiteralPath 'C:\TestScripts\candidate-setup.exe' -Destination $newSetup
 Seed;Start-Mcp;$before=Snapshot
 $exit=Setup $newSetup '01-silent-without-exit-consent'
 $unchanged=Same $before (Snapshot)
 if($exit -eq 0 -or -not $unchanged -or $worker.HasExited){throw 'Silent mode must preserve an active app without explicit exit consent'}
 $result.Cases+=@{Name='SilentConsentRequired';ExitCode=$exit;FilesUnchanged=$unchanged;WorkerPreserved=$true}

 $exit=Setup $newSetup '02-automatic-mcp-exit' @('/CLOSEOWNAPPS=1')
 if($exit -ne 0 -or -not $worker.HasExited){throw 'Automatic MCP exit/upgrade failed'}
 $worker.Dispose();$worker=$null
 $count=Verify-Manifest
 $result.Cases+=@{Name='AutomaticMcpExit';ExitCode=$exit;FilesVerified=$count}

 Seed;$before=Snapshot
 $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8EE42691-81B4-4A6F-A115-9961835E1D45}_is1'
 $oldVersion=(Get-ItemProperty -LiteralPath $key).DisplayVersion
 $exit=Setup $newSetup '03-injected-verification-failure' @('/CLOSEOWNAPPS=1','/QAVERIFYFAIL=1')
 $unchanged=Same $before (Snapshot);$registration=(Get-ItemProperty -LiteralPath $key).DisplayVersion
 @{ExitCode=$exit;FilesUnchanged=$unchanged;OldVersion=$oldVersion;RestoredVersion=$registration} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'rollback-detail.json') -Encoding utf8
 if($exit -eq 0 -or -not $unchanged -or $registration -ne $oldVersion){throw 'Verification failure did not restore all previous program files and registration'}
 $result.Cases+=@{Name='RollbackAfterReplacement';ExitCode=$exit;FilesUnchanged=$unchanged;RegistrationRestored=$true}

 $saved=Get-Acl -LiteralPath $install;$before=Snapshot
 try{
  $acl=Get-Acl -LiteralPath $install;$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
  $rights=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
  $inherit=[Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
  $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,$rights,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Deny));Set-Acl -LiteralPath $install -AclObject $acl
  $exit=Setup $newSetup '04-denied-directory' @('/CLOSEOWNAPPS=1')
  if($exit -eq 0 -or -not(Same $before (Snapshot))){throw 'Permission refusal changed existing program files'}
  $result.Cases+=@{Name='PermissionPreflight';ExitCode=$exit;FilesUnchanged=$true}
 }finally{Set-Acl -LiteralPath $install -AclObject $saved}

 $ui=Start-Process -FilePath (Join-Path $install 'TunHotspot.exe') -ArgumentList '--background' -WindowStyle Hidden -PassThru
 Start-Sleep -Seconds 4
 if($ui.HasExited){throw 'UI fixture failed to start'}
 $exit=Setup $newSetup '05-automatic-ui-exit' @('/CLOSEOWNAPPS=1')
 if($exit -ne 0 -or -not $ui.HasExited){throw 'Automatic UI exit failed'}
 $result.Cases+=@{Name='AutomaticUiExit';ExitCode=$exit;FilesVerified=(Verify-Manifest)}

 Start-Mcp
 $exit=Setup $newSetup '06-cooperative-current-mcp-exit' @('/CLOSEOWNAPPS=1')
 if($exit -ne 0 -or -not $worker.HasExited){throw 'Current MCP cooperative exit failed'}
 if(Select-String -LiteralPath (Join-Path $output '06-cooperative-current-mcp-exit.log') -Pattern '旧版退出兜底' -Encoding utf8 -Quiet){throw 'Current MCP unexpectedly needed force exit'}
 $worker.Dispose();$worker=$null
 $result.Cases+=@{Name='CooperativeCurrentMcpExit';ExitCode=$exit;FilesVerified=(Verify-Manifest);ForceExitUsed=$false}

 $before=Snapshot;$locker=Start-ExternalLock
 try{
  $exit=Setup $newSetup '07-external-dll-lock' @('/CLOSEOWNAPPS=1')
  if($exit -eq 0 -or $locker.HasExited -or -not(Same $before (Snapshot))){throw 'External DLL holder was not safely preserved'}
  $result.Cases+=@{Name='ExternalDllLock';ExitCode=$exit;FilesUnchanged=$true;ExternalProcessPreserved=$true}
 }finally{if(-not $locker.HasExited){$locker.Kill();$locker.WaitForExit()};$locker.Dispose()}

 $ui=Start-Process -FilePath (Join-Path $install 'TunHotspot.exe') -ArgumentList '--background' -WindowStyle Hidden -PassThru
 Start-Sleep -Seconds 4
 if($ui.HasExited){throw 'Current UI fixture failed to start'}
 $exit=Setup $newSetup '08-cooperative-current-ui-exit' @('/CLOSEOWNAPPS=1')
 if($exit -ne 0 -or -not $ui.HasExited){throw 'Current UI cooperative exit failed'}
 if(Select-String -LiteralPath (Join-Path $output '08-cooperative-current-ui-exit.log') -Pattern '旧版退出兜底' -Encoding utf8 -Quiet){throw 'Current UI unexpectedly needed force exit'}
 $ui.Dispose()
 $operations=Join-Path $env:LOCALAPPDATA 'TunHotspot.WinUI\operations.jsonl'
 $cleanExit=Get-Content -LiteralPath $operations -Encoding utf8 -Tail 30 | ForEach-Object {$_ | ConvertFrom-Json} |
  Where-Object {$_.action -eq 'toolbox-closed' -and $_.details.forUpdate -eq $true}
 if(-not $cleanExit){throw 'UI exited without recording its completed update shutdown'}
 $lease=Join-Path $env:USERPROFILE '.FeiChuanToolbox\toolbox-maintenance.lock'
 $exclusive=[IO.File]::Open($lease,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 $exclusive.Dispose()
 $result.Cases+=@{Name='CooperativeCurrentUiExit';ExitCode=$exit;FilesVerified=(Verify-Manifest);ForceExitUsed=$false;DatabaseLeaseReleased=$true}

 $coordinator='C:\TestScripts\Upgrade-Coordinator.ps1';$receipt=Join-Path $output 'abandoned-prepare.json'
 & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $coordinator -Action Prepare -Target $install -Manifest (Join-Path $install 'files.sha256') -ResultFile $receipt -InstallerPid $PID -AllowExit
 if($LASTEXITCODE -ne 0){throw 'Could not prepare abandoned-update fixture'}
 $gate=Join-Path $install '.toolbox-installing.json'
 $blockedStart=[Diagnostics.ProcessStartInfo]::new()
 $blockedStart.FileName=Join-Path $install 'Engine\TunHotspot.Engine.exe';$blockedStart.Arguments='--database-health';$blockedStart.UseShellExecute=$false;$blockedStart.CreateNoWindow=$true;$blockedStart.RedirectStandardError=$true
 $blocked=[Diagnostics.Process]::Start($blockedStart)
 if(-not $blocked.WaitForExit(10000) -or $blocked.ExitCode -ne 75){throw 'Update gate did not block Engine startup'}
 $blocked.StandardError.ReadToEnd() | Set-Content -LiteralPath (Join-Path $output 'blocked-start.txt') -Encoding utf8
 $blockedUi=Start-Process -FilePath (Join-Path $install 'TunHotspot.exe') -ArgumentList '--background' -WindowStyle Hidden -PassThru
 if(-not $blockedUi.WaitForExit(10000)){throw 'Update gate left a UI process locking program files'}
 $record=Get-Content -LiteralPath $gate -Raw -Encoding utf8 | ConvertFrom-Json
 $record.InstallerPid=2147483647;$record.InstallerStarted=0
 $record | ConvertTo-Json | Set-Content -LiteralPath $gate -Encoding utf8
 # Model an older version being restarted during a previously abandoned update.
 Seed;Start-Mcp;$before=Snapshot
 $exit=Setup $newSetup '09a-abandoned-without-exit-consent'
 if($exit -eq 0 -or $worker.HasExited -or -not(Same $before (Snapshot))){throw 'Recovery bypassed silent exit consent'}
 $result.Cases+=@{Name='RecoveryRequiresExitConsent';ExitCode=$exit;FilesUnchanged=$true;WorkerPreserved=$true}
 $exit=Setup $newSetup '09-abandoned-marker-recovery' @('/CLOSEOWNAPPS=1')
 if($exit -ne 0 -or (Test-Path -LiteralPath $gate)){throw 'Abandoned update was not recovered'}
 if(-not $worker.HasExited){throw 'Recovery did not release the old MCP'};$worker.Dispose();$worker=$null
 $result.Cases+=@{Name='AbandonedMarkerRecovery';ExitCode=$exit;BlockedStartupExitCode=75;BlockedUiExited=$true;FilesVerified=(Verify-Manifest)}

 $before=Snapshot;$pipeName='TunHotspot.Scan.'+[Guid]::NewGuid().ToString('N')
 $pipe=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
 $connection=$pipe.BeginWaitForConnection($null,$null)
 $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $critical=Start-Process -FilePath (Join-Path $install 'Engine\TunHotspot.Engine.exe') -ArgumentList @('--control','--pipe',$pipeName,'--owner',$owner) -WindowStyle Hidden -PassThru
 try{
  if(-not $connection.AsyncWaitHandle.WaitOne(5000)){throw 'Critical-operation fixture could not connect'}
  $pipe.EndWaitForConnection($connection)
  $exit=Setup $newSetup '10-critical-worker-preserved' @('/CLOSEOWNAPPS=1')
  if($exit -eq 0 -or $critical.HasExited -or -not(Same $before (Snapshot))){throw 'Critical operation was not safely preserved'}
  $result.Cases+=@{Name='CriticalWorkerPreserved';ExitCode=$exit;FilesUnchanged=$true;WorkerPreserved=$true;ControlRequestSent=$false}
 }finally{$pipe.Dispose();if(-not $critical.WaitForExit(5000)){$critical.Kill();$critical.WaitForExit()};$critical.Dispose()}

 $ops=Join-Path $env:ProgramData ('TunHotspot\Installer\'+$owner+'\operations')
 $committed=Get-ChildItem -LiteralPath $ops -Directory | ForEach-Object {
  $file=Join-Path $_.FullName 'state.json';if(Test-Path -LiteralPath $file){$s=Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json;if($s.Phase -eq 'Committed'){@{Path=$_.FullName;Created=$s.Created}}}
 } | Sort-Object Created -Descending | Select-Object -First 1
 if(-not $committed){throw 'No completed backup for conflict fixture'}
 $manualFile=Join-Path $install 'release.json';$original=[IO.File]::ReadAllBytes($manualFile)
 try{
  [IO.File]::AppendAllText($manualFile,"`r`n ");$before=Snapshot
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $coordinator -Action Rollback -Operation $committed.Path -ResultFile (Join-Path $output 'manual-restore-conflict.json')
  $exit=$LASTEXITCODE
  if($exit -eq 0 -or -not(Same $before (Snapshot))){throw 'Manual restore overwrote a later change'}
  $result.Cases+=@{Name='ManualRestoreConflict';ExitCode=$exit;LaterChangePreserved=$true}
 }finally{[IO.File]::WriteAllBytes($manualFile,$original)}

 $result.Success=$true
}catch{$result.Error=$_.Exception.ToString()}
finally{
 if($worker -and -not $worker.HasExited){$worker.StandardInput.Close();if(-not $worker.WaitForExit(5000)){$worker.Kill()}}
 $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'installer-recovery.json') -Encoding utf8
}
