$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path -LiteralPath 'C:\TestScripts\run.ps1')){throw 'Only the dedicated disposable Windows Sandbox is allowed.'}
$output='C:\TestResults'
$install='C:\Toolbox Upgrade Simulation\app'
$oldSetup='C:\TestScripts\previous-setup.exe'
$newSetup='C:\TestScripts\candidate-setup.exe'
$result=[ordered]@{Success=$false;Cases=@();HostInstallationTouched=$false}
$ownedWorker=$null
function Run-Setup([string]$file,[string]$name){
 $log=Join-Path $output ($name+'.log')
 $args=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=""',('/DIR="'+$install+'"'),('/LOG="'+$log+'"'))
 $process=Start-Process -FilePath $file -ArgumentList $args -WindowStyle Hidden -PassThru
 if(-not $process.WaitForExit(120000)){throw ('Installer timeout: '+$name)}
 $process.Refresh();[pscustomobject]@{Name=$name;ExitCode=$process.ExitCode;Log=$log}
}
function Manifest-Hashes {
 $rows=@();foreach($line in Get-Content -LiteralPath (Join-Path $install 'files.sha256')){
  if($line -notmatch '^([a-f0-9]{64})  (.+)$'){throw 'Invalid installed manifest'}
  $relative=$matches[2];$path=Join-Path $install $relative
  $hash=if(Test-Path -LiteralPath $path){(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()}else{'missing'}
  $rows+=[pscustomobject]@{Path=$relative;Hash=$hash}
 };return $rows
}
function Equal-Hashes($before,$after){
 if($before.Count -ne $after.Count){return $false}
 foreach($row in $before){if(($after | Where-Object Path -eq $row.Path).Hash -ne $row.Hash){return $false}};return $true
}
try{
 $seed=Run-Setup $oldSetup '01-seed-previous';if($seed.ExitCode -ne 0){throw 'Previous installer seed failed'}
 $before=Manifest-Hashes
 $engine=Join-Path $install 'Engine\TunHotspot.Engine.exe'
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName=$engine;$start.Arguments='--mcp';$start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $ownedWorker=[Diagnostics.Process]::Start($start)
 Start-Sleep -Seconds 2
 if($ownedWorker.HasExited){throw 'Owned MCP fixture exited unexpectedly'}
 $locked=Run-Setup $newSetup '02-upgrade-with-mcp-running'
 $after=Manifest-Hashes
 $result.Cases+=@{Name='McpInUse';ExitCode=$locked.ExitCode;FilesUnchanged=(Equal-Hashes $before $after);WorkerStillAlive=(-not $ownedWorker.HasExited);Log=$locked.Log}
 $ownedWorker.StandardInput.Close()
 if(-not $ownedWorker.WaitForExit(5000)){$ownedWorker.Kill();$ownedWorker.WaitForExit()}
 $ownedWorker.Dispose();$ownedWorker=$null

 $reset=Run-Setup $oldSetup '02b-reset-previous';if($reset.ExitCode -ne 0){throw 'Fixture reset failed'}
 $before=Manifest-Hashes
 $savedAcl=Get-Acl -LiteralPath $install
 try{
  $acl=Get-Acl -LiteralPath $install
  $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
  $rights=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
  $inherit=[Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
  $rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,$rights,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Deny)
  $acl.AddAccessRule($rule);Set-Acl -LiteralPath $install -AclObject $acl
  $permission=Run-Setup $newSetup '03-upgrade-with-denied-dll-write'
  $result.Cases+=@{Name='AccessDenied';ExitCode=$permission.ExitCode;FilesUnchanged=(Equal-Hashes $before (Manifest-Hashes));Log=$permission.Log}
 }finally{Set-Acl -LiteralPath $install -AclObject $savedAcl}

 $clean=Run-Setup $newSetup '04-upgrade-without-blockers'
 $result.Cases+=@{Name='CleanUpgrade';ExitCode=$clean.ExitCode;Version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'TunHotspot.exe')).ProductVersion;Log=$clean.Log}
 $result.Success=($seed.ExitCode -eq 0 -and $locked.ExitCode -ne 0 -and $permission.ExitCode -ne 0 -and $clean.ExitCode -eq 0)
}catch{$result.Error=$_.Exception.ToString()}
finally{
 if($ownedWorker -and -not $ownedWorker.HasExited){$ownedWorker.StandardInput.Close();if(-not $ownedWorker.WaitForExit(5000)){$ownedWorker.Kill()}}
 $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'upgrade-simulation.json') -Encoding utf8
}
