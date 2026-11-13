param(
    [Parameter(Mandatory=$true)][ValidateSet('Identity','Inspect','Prepare','VerifyAndCommit','Rollback')][string]$Action,
    [string]$Target, [string]$Manifest, [string]$Operation, [string]$OwnerSid,
    [Parameter(Mandatory=$true)][string]$ResultFile,
    [int]$InstallerPid, [switch]$AllowExit, [switch]$InjectVerificationFailure
)
# Windows PowerShell 5.1 is built into supported Windows 11 systems. No SDK,
# third-party module, network operation, or arbitrary command execution is used.
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$sid=$identity.User.Value
$elevated=[Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$userRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.FeiChuanToolbox'
$machineRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'TunHotspot'
$operationRoot=if($elevated){Join-Path $machineRoot ('Installer\'+$sid+'\operations')}else{Join-Path $userRoot 'installer\operations'}
$uninstallKey='Software\Microsoft\Windows\CurrentVersion\Uninstall\{8EE42691-81B4-4A6F-A115-9961835E1D45}_is1'
$state=$null
$preparedByThisCall=$false
$code=0
$message=''
$result=[ordered]@{Success=$false;Action=$Action;OwnerSid=$sid;Elevated=$elevated;Operation='';Message=''}

if ($Action -ne 'Identity') {
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class ToolboxUpgradeNative {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CommandLineToArgvW(string command,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool MoveFileEx(string source,string destination,uint flags);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string value);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr value);
 delegate bool EnumProc(IntPtr window,IntPtr value);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,UIntPtr w,IntPtr l,uint flags,uint timeout,out UIntPtr result);
 public static string[] Args(string command){int count;IntPtr pointer=CommandLineToArgvW(command,out count);if(pointer==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();try{var args=new string[count];for(int i=0;i<count;i++)args[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,i*IntPtr.Size));return args;}finally{LocalFree(pointer);}}
 public static int Access(string path,bool directory){IntPtr handle=CreateFile(path,directory?2U:0x40010000U,7,IntPtr.Zero,3,directory?0x02000000U:0U,IntPtr.Zero);if(handle==new IntPtr(-1))return Marshal.GetLastWin32Error();CloseHandle(handle);return 0;}
 public static void Replace(string source,string destination){if(!MoveFileEx(source,destination,9))throw new System.ComponentModel.Win32Exception();}
 public static void RequestUiExit(int pid){uint message=RegisterWindowMessage("TunHotspot.Toolbox.ExitForUpdate.v1");EnumWindows(delegate(IntPtr window,IntPtr unused){uint owner;GetWindowThreadProcessId(window,out owner);if(owner==(uint)pid){UIntPtr result;SendMessageTimeout(window,message,UIntPtr.Zero,IntPtr.Zero,2,1000,out result);}return true;},IntPtr.Zero);}
}
'@
}

function Full([string]$path){[IO.Path]::GetFullPath($path).TrimEnd('\')}
function No-Links([string]$path){
 $current=Full $path
 while($current -and $current.Length -gt 3){
  if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "路径含重解析点，已停止：$current"}
  $current=Split-Path -Parent $current
 }
}
function Child([string]$base,[string]$relative){
 if([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')){throw '备份/清单含绝对路径。'}
 $path=Full (Join-Path $base $relative)
 if(-not $path.StartsWith((Full $base)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '路径超出本次安装目录。'}
 No-Links $path
 return $path
}
function Check-Target([string]$path,[bool]$Recorded=$false){
 $path=Full $path;No-Links $path
 if($path.StartsWith('\\') -or $path.Length -le 3){throw '不支持网络路径或磁盘根目录。'}
 foreach($forbidden in @($env:WINDIR,[Environment]::GetFolderPath('UserProfile'),[Environment]::GetFolderPath('ProgramFiles'),[Environment]::GetFolderPath('ProgramFilesX86'),$userRoot,$machineRoot)){
  if($forbidden -and ($path.Equals((Full $forbidden),[StringComparison]::OrdinalIgnoreCase) -or (Full $forbidden).StartsWith($path+'\',[StringComparison]::OrdinalIgnoreCase))){throw '不能以系统目录、个人资料或数据根目录作为安装目录。'}
 }
 if($path.StartsWith((Full $env:WINDIR)+'\',[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith((Full $userRoot)+'\',[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith((Full $machineRoot)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '安装目录与系统/备份数据目录冲突。'}
 if(-not $Recorded -and (Test-Path -LiteralPath $path) -and @(Get-ChildItem -LiteralPath $path -Force).Count){
  $exe=Join-Path $path 'TunHotspot.exe'
  if(-not(Test-Path -LiteralPath $exe)){throw '此目录包含其他内容且未确认属于工具箱，已停止。'}
  $product=[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductName
  if($product -notin @('飞船的小工具箱','TUN 热点与启动工具箱','TUN 热点助手','TunHotspot')){throw '目标程序身份未确认，已停止。'}
 }
 return $path
}
function Private-Directory([string]$path){
 No-Links $path
 if(Test-Path -LiteralPath $path){
  if($elevated){
   $acl=Get-Acl -LiteralPath $path
   $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
   if($owner -notin @($adminSid.Value,$systemSid.Value)){throw "高权限备份目录被非管理员预占，已停止：$path"}
   foreach($ace in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
    if($ace.AccessControlType -eq 'Allow' -and $ace.IdentityReference.Value -notin @($adminSid.Value,$systemSid.Value) -and ([long]$ace.FileSystemRights -band 0xD0116)){throw '高权限备份目录允许其他账户写入，已停止。'}
   }
  }
  return
 }
 $parent=Split-Path -Parent $path
 if(-not(Test-Path -LiteralPath $parent)){Private-Directory $parent}
 $security=[Security.AccessControl.DirectorySecurity]::new()
 $security.SetAccessRuleProtection($true,$false)
 $security.SetOwner($(if($elevated){$adminSid}else{$identity.User}))
 $inherit=[Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
 foreach($principal in @($adminSid,$systemSid)){$security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal,'FullControl',$inherit,'None','Allow'))}
 $rights=if($elevated){'ReadAndExecute'}else{'FullControl'}
 $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity.User,$rights,$inherit,'None','Allow'))
 [IO.Directory]::CreateDirectory($path,$security) | Out-Null
}
function Save-Json([string]$path,$value){
 No-Links $path
 $text=$value | ConvertTo-Json -Depth 12
 $temporary=$path+'.new-'+[Guid]::NewGuid().ToString('N')
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes($text)
 $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
 [ToolboxUpgradeNative]::Replace($temporary,$path)
}
function Save-State{Save-Json (Join-Path $Operation 'state.json') $script:state}
function Log-Step([string]$step,[string]$detail){
 if($Operation -and (Test-Path -LiteralPath $Operation)){
  @{Time=[DateTimeOffset]::Now.ToString('o');Step=$step;Detail=$detail} | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $Operation 'steps.jsonl') -Encoding UTF8
 }
}
function Payload([string]$file){
 if(-not(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -gt 2MB){throw '安装文件清单缺失或过大。'}
 $items=@();$seen=@{}
 foreach($line in Get-Content -LiteralPath $file -Encoding UTF8){
  if($line -notmatch '^([a-f0-9]{64})  (.+)$'){throw '安装文件清单格式无效。'}
  $hash=$matches[1];$relative=$matches[2];[void](Child $Target $relative)
  if($seen.ContainsKey($relative)){throw '安装清单重复。'};$seen[$relative]=$true
  $items+=@{Relative=$relative;Hash=$hash}
 }
 return $items
}
function Owned-Processes {
 $allowed=@((Join-Path $Target 'TunHotspot.exe'),(Join-Path $Target 'Engine\TunHotspot.Engine.exe'))
 $found=@()
 foreach($candidate in Get-CimInstance Win32_Process -Filter "Name='TunHotspot.exe' OR Name='TunHotspot.Engine.exe'"){
  if(-not $candidate.ExecutablePath -or $allowed -notcontains $candidate.ExecutablePath){continue}
  $owner=(Invoke-CimMethod -InputObject $candidate -MethodName GetOwnerSid).Sid
  if($owner -ne $sid){throw "其他账户的工具箱正在使用此目录（PID $($candidate.ProcessId)），已停止。"}
  $arguments=[ToolboxUpgradeNative]::Args($candidate.CommandLine)
  $role='界面'
  if($candidate.Name -eq 'TunHotspot.Engine.exe'){
   if($arguments.Length -eq 2 -and $arguments[1] -eq '--mcp'){$role='Codex 分析桥接'}
   elseif($arguments.Length -eq 4 -and $arguments[1] -eq '--hotspot-worker'){$role='热点状态工作进程'}
   else{throw "工具箱正在执行其他任务（PID $($candidate.ProcessId)），请等待完成后重试。"}
  }
  $process=[Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId)
  try{
   [void]$process.Handle
   if($process.MainModule.FileName -ne $candidate.ExecutablePath -or [Math]::Abs(($process.StartTime.ToUniversalTime()-$candidate.CreationDate.ToUniversalTime()).TotalSeconds) -gt 0.01){throw '进程身份在检查期间变化，请重试。'}
   $found+=@{Process=$process;Role=$role;Started=$process.StartTime.ToUniversalTime().Ticks}
  }catch{$process.Dispose();throw}
 }
 return $found
}
function Close-Owned {
 $attempts=0
 for($round=0;$round -lt 8;$round++){
  $processes=@(Owned-Processes)
  if(-not $processes.Count){Start-Sleep -Milliseconds 400;if(-not @(Owned-Processes).Count){return};continue}
  if(-not $AllowExit){foreach($entry in $processes){$entry.Process.Dispose()};throw '本软件仍在运行。静默安装需要显式 /CLOSEOWNAPPS=1；交互安装在准备页确认自动退出。'}
  if(++$attempts -gt 3){foreach($entry in $processes){$entry.Process.Dispose()};throw '工具箱进程反复被重新拉起；尚未替换文件，请暂时断开其 Codex MCP 连接后重试。'}
  # Let the UI drain its work and close the owned hotspot worker first.
  try{foreach($entry in ($processes | Sort-Object { if($_.Role -eq '界面'){0}else{1} })){
   $process=$entry.Process
   try{
    if($process.HasExited){continue}
    Log-Step '请求退出' ($entry.Role+' PID '+$process.Id)
    $eventName='Local\TunHotspot.Setup.Exit.'+$process.Id+'.'+$entry.Started
    $signalled=$false
    try{$signal=[Threading.EventWaitHandle]::OpenExisting($eventName);try{$signalled=$signal.Set()}finally{$signal.Dispose()}}catch [Threading.WaitHandleCannotBeOpenedException]{}
    if($entry.Role -eq '界面' -and $signalled){
     if(-not $process.WaitForExit(20000)){throw '工具箱仍在安全退出。尚未替换文件，请待工具箱退出后重试。'}
     continue
    }
    if($entry.Role -eq '界面'){[ToolboxUpgradeNative]::RequestUiExit($process.Id);if(-not $process.WaitForExit(1500)){[void]$process.CloseMainWindow()}}
    if(-not $process.WaitForExit(2500)){
     # The Process handle was acquired before the path/owner/start-time checks;
     # no name-wide taskkill, child-tree kill, or service control is used.
     Log-Step '旧版退出兜底' ($entry.Role+' PID '+$process.Id)
     $process.Kill();if(-not $process.WaitForExit(5000)){throw '本软件进程退出超时。'}
    }
   }finally{$process.Dispose()}
  }}finally{foreach($entry in $processes){$entry.Process.Dispose()}}
 }
 throw '无法稳定解除本软件占用，尚未替换文件。'
}
function Write-Permission {
 $directory=$Target
 while(-not(Test-Path -LiteralPath $directory)){$directory=Split-Path -Parent $directory}
 $errorCode=[ToolboxUpgradeNative]::Access($directory,$true)
 if($errorCode){
  if(-not $elevated){$script:code=10}else{$script:code=12}
  throw "安装目录不可写，Windows 错误 $errorCode。需要当前账户管理员授权，或选择可写的用户目录。"
 }
}
function Registry-Snapshot {
 $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallKey)
 if(-not $key){return @{Exists=$false;Values=@()}}
 try{
  $values=@();foreach($name in $key.GetValueNames()){
   $kind=$key.GetValueKind($name);$data=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
   if($kind -eq 'Binary'){$data=[Convert]::ToBase64String($data)}
   $values+=@{Name=$name;Kind=[int]$kind;Data=$data}
  };return @{Exists=$true;Values=$values}
 }finally{$key.Dispose()}
}
function Registry-Restore($snapshot){
 if(-not $snapshot.Exists){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($uninstallKey,$false);return}
 $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($uninstallKey)
 try{
  foreach($name in $key.GetValueNames()){$key.DeleteValue($name,$false)}
  foreach($value in $snapshot.Values){
   $kind=[Microsoft.Win32.RegistryValueKind]$value.Kind;$data=$value.Data
   if($kind -eq 'Binary'){$data=[Convert]::FromBase64String($data)}elseif($kind -eq 'DWord'){$data=[int]$data}elseif($kind -eq 'QWord'){$data=[long]$data}elseif($kind -eq 'MultiString'){$data=[string[]]$data}
   $key.SetValue($value.Name,$data,$kind)
  }
 }finally{$key.Dispose()}
}
function Read-State([string]$path){
 $script:Operation=Full $path;No-Links $Operation
 if(-not $Operation.StartsWith((Full $operationRoot)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '恢复记录不属于当前账户和权限范围。'}
 $identifier=Split-Path -Leaf $Operation;$valid=[Guid]::Empty
 if(-not[Guid]::TryParseExact($identifier,'N',[ref]$valid)){throw '恢复编号无效。'}
 Private-Directory $operationRoot
 $script:state=Get-Content -LiteralPath (Join-Path $Operation 'state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
 if($state.OwnerSid -ne $sid -or [bool]$state.Elevated -ne $elevated -or $state.Machine -ne $env:COMPUTERNAME){throw '恢复记录的账户、机器或权限不一致。'}
 $script:Target=Check-Target $state.Target $true
 foreach($file in $state.Files){[void](Child $Target $file.Relative);[void](Child (Join-Path $Operation 'files') $file.Relative)}
}
function Clear-Gate {
 $gate=Join-Path $Target '.toolbox-installing.json'
 if(Test-Path -LiteralPath $gate){
  $record=Get-Content -LiteralPath $gate -Raw -Encoding UTF8 | ConvertFrom-Json
  if($record.Operation -ne $Operation){throw '安装锁属于另一操作，未删除。'}
  [IO.File]::Delete($gate)
 }
}
function Restore-Operation {
 if($state.Phase -eq 'Committed'){
  foreach($file in $state.Payload){$path=Child $Target $file.Relative;if(-not(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.Hash){throw '程序在本次安装后已变化，拒绝用旧备份覆盖；请保留记录并重新核查。'}}
 }else{
  $gate=Join-Path $Target '.toolbox-installing.json'
  if(-not(Test-Path -LiteralPath $gate) -or (Get-Content -LiteralPath $gate -Raw -Encoding UTF8 | ConvertFrom-Json).Operation -ne $Operation){throw '未发现属于本次操作的安装锁，拒绝覆盖可能的后续安装。'}
 }
 if($Action -eq 'Rollback'){$script:AllowExit=$true}
 Close-Owned
 $state.Phase='Restoring';Save-State;Log-Step '恢复开始' '仅恢复本次程序文件和安装登记，不处理用户数据库'
 $errors=@()
 foreach($file in $state.Files){
  try{
   $destination=Child $Target $file.Relative
   if($file.Existed){
    $backup=Child (Join-Path $Operation 'files') $file.Relative
    if((Get-FileHash -LiteralPath $backup).Hash.ToLowerInvariant() -ne $file.Hash){throw '原始备份校验失败'}
    if(-not(Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $file.Hash){
     [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
     [IO.File]::Copy($backup,$destination,$true)
    }
    if((Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $file.Hash){throw '恢复回读不一致'}
   }elseif(Test-Path -LiteralPath $destination){[IO.File]::Delete($destination)}
  }catch{$errors+=$file.Relative+': '+$_.Exception.Message}
 }
 if(-not $errors.Count){try{Registry-Restore $state.Registration}catch{$errors+=$_.Exception.Message}}
 if($errors.Count){$state.Phase='RecoveryRequired';$state.Message=$errors -join "`n";Save-State;throw ('恢复未完整完成，原件仍保留：'+$Operation+"`n"+$state.Message)}
 $state.Phase='RolledBack';$state.Message='已回读恢复安装前程序文件和安装登记；不等同于原状态本来就是健康版本。';Save-State;Clear-Gate
 Log-Step '恢复完成' $state.Message
}

try{
 if($OwnerSid -and $OwnerSid -ne $sid){throw '提升权限后账户发生变化；请用当前账户提升，或安装到当前用户可写目录。'}
 if($Action -eq 'Identity'){$result.Success=$true}
 elseif($Action -in @('Inspect','Prepare')){
  $Target=Check-Target $Target
  Write-Permission
  if($Action -eq 'Inspect'){
   $message='目标权限可用；安装将退出本目录下的工具箱界面、热点工作进程和 Codex 桥接，备份后替换并核验。'
   $result.Success=$true
  }else{
   $payload=@(Payload $Manifest)
   if($InstallerPid -le 0){throw '缺少安装器身份，尚未处理进程或文件。'}
   $parent=Get-Process -Id $InstallerPid -ErrorAction Stop
   $gate=Join-Path $Target '.toolbox-installing.json'
   if(Test-Path -LiteralPath $gate){
    $requestedTarget=$Target
    $pending=Get-Content -LiteralPath $gate -Raw -Encoding UTF8 | ConvertFrom-Json
    $alive=Get-Process -Id ([int]$pending.InstallerPid) -ErrorAction SilentlyContinue
    if($alive -and $alive.StartTime.ToUniversalTime().Ticks -eq [long]$pending.InstallerStarted){throw '另一安装/恢复仍在进行。'}
    Read-State $pending.Operation
    if(-not $Target.Equals($requestedTarget,[StringComparison]::OrdinalIgnoreCase)){throw '遗留安装锁的目标目录不一致，未执行恢复。'}
    if($state.Phase -notin @('Committed','RolledBack')){Restore-Operation}else{Clear-Gate}
    $state=$null;$Operation=''
   }
   Write-Output 'INSTALL_STAGE:close'
   Close-Owned
   foreach($item in $payload){$path=Child $Target $item.Relative;if(Test-Path -LiteralPath $path){$errorCode=[ToolboxUpgradeNative]::Access($path,$false);if($errorCode){throw "文件仍被占用或无写入权限：$($item.Relative)；Windows 错误 $errorCode。尚未替换程序。"}}}
   Write-Output 'INSTALL_STAGE:backup'
   Private-Directory $operationRoot
   $Operation=Join-Path $operationRoot ([Guid]::NewGuid().ToString('N'));Private-Directory $Operation
   $result.Operation=$Operation
   Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $Operation 'Recover.ps1')
   @('@echo off','"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Recover.ps1" -Action Rollback -Operation "%~dp0." -ResultFile "%TEMP%\FeiChuan-restore-result.json"','echo.','echo Result: %TEMP%\FeiChuan-restore-result.json.message','pause') | Set-Content -LiteralPath (Join-Path $Operation 'Restore.cmd') -Encoding ascii
   $names=@($payload | ForEach-Object {$_.Relative})+@('files.sha256')
   if(Test-Path -LiteralPath $Target){$names+=@(Get-ChildItem -LiteralPath $Target -File -Filter 'unins*' | Where-Object {$_.Name -match '^unins\d+\.(exe|dat|msg)$'} | ForEach-Object {$_.Name})}
   $records=@()
   foreach($relative in $names | Select-Object -Unique){
    $path=Child $Target $relative;$exists=Test-Path -LiteralPath $path
    $hash=''
    if($exists){
     $hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant();$backup=Child (Join-Path $Operation 'files') $relative
     [IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) | Out-Null
     [IO.File]::Copy($path,$backup,$false)
     if((Get-FileHash -LiteralPath $backup).Hash.ToLowerInvariant() -ne $hash){throw '程序备份校验失败，未替换程序。'}
    }
    $records+=@{Relative=$relative;Existed=[bool]$exists;Hash=$hash}
   }
   $state=[ordered]@{Schema=1;OwnerSid=$sid;Machine=$env:COMPUTERNAME;Elevated=$elevated;Target=$Target;Phase='Prepared';Message='';Files=$records;Payload=$payload;Registration=(Registry-Snapshot);Created=[DateTimeOffset]::Now.ToString('o')}
   Save-State
   [IO.Directory]::CreateDirectory($Target) | Out-Null
   $parent=Get-Process -Id $InstallerPid -ErrorAction Stop
   Save-Json $gate @{Operation=$Operation;InstallerPid=$InstallerPid;InstallerStarted=$parent.StartTime.ToUniversalTime().Ticks}
   $preparedByThisCall=$true
   Log-Step '备份完成' ('文件记录 '+$records.Count+'；可执行 Recover.ps1 -Action Rollback -Operation 本目录 -ResultFile 临时结果文件')
   Close-Owned
   $message='已退出本软件进程并核验备份；准备替换程序。备份：'+$Operation
   $result.Success=$true
  }
 }else{
  Read-State $Operation
  $result.Operation=$Operation
  if($Action -eq 'Rollback'){if($state.Phase -ne 'RolledBack'){Restore-Operation};$message=$state.Message;$result.Success=$true}
  elseif($Action -eq 'VerifyAndCommit'){
   if($state.Phase -ne 'Prepared'){throw '安装状态与校验阶段不符，不能提交。'}
   Write-Output 'INSTALL_STAGE:verify'
   $failures=@();foreach($file in $state.Payload){$path=Child $Target $file.Relative;if(-not(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.Hash){$failures+=$file.Relative}}
   if($InjectVerificationFailure){
    if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path -LiteralPath 'C:\TestScripts\run.ps1')){throw '故障注入仅允许项目沙盒。'}
    $failures+='QA 注入的安装后校验失败'
   }
   if($failures.Count){throw ('安装后核验失败：'+($failures -join ', '))}
   $state.Phase='Verified';Save-State;Log-Step '文件核验通过' ([string]$state.Payload.Count)
   Write-Output 'INSTALL_STAGE:commit'
   $state.Phase='Committed';Save-State;Clear-Gate;Log-Step '安装完成' '程序完整性已回读，原始备份保留';$message='安装完成；备份和日志：'+$Operation;$result.Success=$true
  }
 }
}catch{
 if(-not $code){$code=20}
 $message=$_.Exception.Message
 if($state){try{$state.Message=$message;Save-State;Log-Step '错误' $message}catch{}}
}finally{
 $result.Message=$message;$result.Operation=$Operation
 $parent=Split-Path -Parent (Full $ResultFile);No-Links $parent
 if(-not(Test-Path -LiteralPath $parent)){[IO.Directory]::CreateDirectory($parent) | Out-Null}
 $encoding=[Text.UTF8Encoding]::new($false)
 [IO.File]::WriteAllText($ResultFile,($result | ConvertTo-Json -Depth 5),$encoding)
 [IO.File]::WriteAllText($ResultFile+'.message',$message,$encoding)
 [IO.File]::WriteAllText($ResultFile+'.operation',[string]$Operation,$encoding)
 [IO.File]::WriteAllText($ResultFile+'.sid',$sid,$encoding)
 [IO.File]::WriteAllText($ResultFile+'.prepared',$(if($preparedByThisCall){'1'}else{'0'}),$encoding)
}
exit $code
