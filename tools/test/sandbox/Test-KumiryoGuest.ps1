$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path -LiteralPath 'C:\TestScripts\run.ps1')){throw 'Only the dedicated project Windows Sandbox is allowed.'}
$result=[ordered]@{Success=$false;Stage='Prepare'}
try{
    $testRoot=Join-Path $env:TEMP ('TunHotspot-Kumiryo-Integration-'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    $driver=Join-Path $testRoot 'Driver';$client=Join-Path $testRoot 'Client';$output=Join-Path $testRoot 'Output'
    Copy-Item -LiteralPath 'C:\DriverInput' -Destination $driver -Recurse
    Copy-Item -LiteralPath 'C:\ClientInput' -Destination $client -Recurse
    [IO.Directory]::CreateDirectory($output) | Out-Null
    Add-Type @'
using System;using System.ComponentModel;using System.Runtime.InteropServices;using System.Text;
public static class KumiryoRestrictedQa {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {public int cb;public string reserved,desktop,title;public int x,y,w,h,xc,yc,fill,flags;public short show,reserved2;public IntPtr data,input,output,error;}
 [StructLayout(LayoutKind.Sequential)] struct Info {public IntPtr process,thread;public int pid,tid;}
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool SaferCreateLevel(uint scope,uint level,uint flags,out IntPtr result,IntPtr reserved);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool SaferComputeTokenFromLevel(IntPtr level,IntPtr input,out IntPtr token,uint flags,IntPtr reserved);
 [DllImport("advapi32.dll")] static extern bool SaferCloseLevel(IntPtr level);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetTokenInformation(IntPtr token,int kind,IntPtr data,int length);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessWithTokenW(IntPtr token,uint logon,string app,StringBuilder command,uint flags,IntPtr environment,string directory,ref Startup startup,out Info info);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static int Start(string executable,string arguments,string directory){
  IntPtr level=IntPtr.Zero,token=IntPtr.Zero;
  try{
   if(!SaferCreateLevel(1,0x20000,1,out level,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
   if(!SaferComputeTokenFromLevel(level,IntPtr.Zero,out token,0,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
   var sid=new System.Security.Principal.SecurityIdentifier("S-1-16-8192");var bytes=new byte[sid.BinaryLength];sid.GetBinaryForm(bytes,0);int header=IntPtr.Size==8?16:8;IntPtr label=Marshal.AllocHGlobal(header+bytes.Length);
   try{Marshal.Copy(bytes,0,label+header,bytes.Length);Marshal.WriteIntPtr(label,label+header);Marshal.WriteInt32(label+IntPtr.Size,0x20);if(!SetTokenInformation(token,25,label,header+bytes.Length))throw new Win32Exception(Marshal.GetLastWin32Error());}finally{Marshal.FreeHGlobal(label);}
   var startup=new Startup{cb=Marshal.SizeOf(typeof(Startup)),desktop="winsta0\\default"};Info info;
   if(!CreateProcessWithTokenW(token,0,executable,new StringBuilder("\""+executable+"\" "+arguments),0,IntPtr.Zero,directory,ref startup,out info))throw new Win32Exception(Marshal.GetLastWin32Error());
   CloseHandle(info.process);CloseHandle(info.thread);return info.pid;
  }finally{if(token!=IntPtr.Zero)CloseHandle(token);if(level!=IntPtr.Zero)SaferCloseLevel(level);}
 }
}
'@
    $exe=Join-Path $driver 'Kumiryo.Integration.exe'
    $arguments='"C:\Application\Engine\TunHotspot.Engine.exe" "'+$client+'" "'+$output+'"'
    if(Test-Path -LiteralPath 'C:\TestScripts\elevated.flag'){$arguments+=' elevated'}
    $result.Stage='Integration'
    $driverId=[KumiryoRestrictedQa]::Start($exe,$arguments,$driver)
    $process=Get-Process -Id $driverId
    $deadline=(Get-Date).AddSeconds(115)
    do{
        Start-Sleep -Milliseconds 100
        $elevationFile=Join-Path $output 'elevation-request.json'
        if(Test-Path -LiteralPath $elevationFile){
            $elevation=Get-Content -LiteralPath $elevationFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $validId=[Guid]::Empty;$validNonce=[Guid]::Empty
            if($elevation.kind -notin @('--kumiryo-preview','--kumiryo-job') -or $elevation.ownerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or -not [Guid]::TryParseExact($elevation.id,'N',[ref]$validId) -or -not [Guid]::TryParseExact($elevation.nonce,'N',[ref]$validNonce)){throw 'Invalid fixture elevation relay request.'}
            $elevated=Start-Process -FilePath 'C:\Application\Engine\TunHotspot.Engine.exe' -ArgumentList $elevation.kind,$elevation.id,'--owner',$elevation.ownerSid -WindowStyle Hidden -PassThru
            $reply=Join-Path $output ('elevation-response-'+$elevation.nonce+'.json')
            if($elevation.kind -eq '--kumiryo-preview'){
                if(-not $elevated.WaitForExit(20000)){throw 'Privileged preview timeout.'}
                [IO.File]::WriteAllText($reply,('{"pid":'+$elevated.Id+',"exitCode":'+[int]$elevated.ExitCode+'}'))
            }else{[IO.File]::WriteAllText($reply,('{"pid":'+$elevated.Id+'}'))}
            Remove-Item -LiteralPath $elevationFile
        }
        $killFile=Join-Path $output 'kill-request.json'
        if(Test-Path -LiteralPath $killFile){
            $kill=Get-Content -LiteralPath $killFile -Raw -Encoding UTF8 | ConvertFrom-Json
            $validJob=[Guid]::Empty
            if(-not [Guid]::TryParseExact($kill.jobId,'N',[ref]$validJob)){throw 'Invalid fixture job identity.'}
            $reportPath=Join-Path $env:USERPROFILE ('.FeiChuanToolbox\KumiryoConfiguration\operations\'+$kill.jobId+'\report.json')
            $jobReport=Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $owned=[Diagnostics.Process]::GetProcessById([int]$kill.pid)
            if($owned.MainModule.FileName -eq 'C:\Application\Engine\TunHotspot.Engine.exe' -and $jobReport.id -eq $kill.jobId -and [int]$jobReport.workerPid -eq $owned.Id -and [long]$jobReport.workerStarted -eq $owned.StartTime.ToUniversalTime().Ticks){
                $owned.Kill();$owned.WaitForExit(5000) | Out-Null
            }else{throw 'Refused to terminate a process outside the exact fixture job.'}
            $owned.Dispose()
            Remove-Item -LiteralPath $killFile
        }
        $process.Refresh()
    }while(-not $process.HasExited -and (Get-Date) -lt $deadline)
    if(-not $process.HasExited){throw 'Integration driver timeout.'}
    $result.DriverExitCode=$process.ExitCode
    $file=Join-Path $output 'integration.json'
    if(-not(Test-Path -LiteralPath $file)){throw 'Integration report missing.'}
    Copy-Item -LiteralPath $file -Destination 'C:\TestResults\integration.json'
    Get-ChildItem -LiteralPath $output -Filter 'failed-operation-*.json' -File | Copy-Item -Destination 'C:\TestResults'
    $report=Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    if(-not $report.success){throw 'Integration checks failed. See integration.json.'}
    # Also verify the self-contained WinUI distribution without an SDK or Codex installation.
    $ui=Start-Process -FilePath 'C:\Application\TunHotspot.exe' -ArgumentList '--qa-capture','configuration' -PassThru
    if(-not $ui.WaitForExit(30000)){throw 'WinUI QA timeout.'}
    if($ui.ExitCode -ne 0){throw 'WinUI QA failed.'}
    $qaRoot=Join-Path ([IO.Path]::GetTempPath()) ('TunHotspot-QA\'+$ui.Id)
    Copy-Item -LiteralPath (Join-Path $qaRoot 'qa\configuration.png') -Destination 'C:\TestResults\configuration.png'
    $result.Success=$true;$result.Stage='Complete'
}catch{$result.Error=$_.Exception.ToString()}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath 'C:\TestResults\sandbox.json' -Encoding UTF8
