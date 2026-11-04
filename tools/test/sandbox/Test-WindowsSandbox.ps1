param([string]$PublishDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $root 'tools\common\Artifacts.ps1')
$layout=Get-ArtifactLayout $root
$publish=if($PublishDirectory){[IO.Path]::GetFullPath($PublishDirectory)}else{Join-Path $layout.Current 'app'}
$runRoot=Join-Path $layout.Reports ('tests\sandbox-'+[Guid]::NewGuid().ToString('N'))
$out=Join-Path $runRoot 'output'
$inputFolder=Join-Path $runRoot 'input'
New-Item -ItemType Directory -Path $out,$inputFolder -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-DistributionGuest.ps1') -Destination (Join-Path $inputFolder 'run.ps1') -Force
function XmlText([string]$value){[Security.SecurityElement]::Escape($value)}
$configuration=@"
<Configuration>
 <Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MemoryInMB>3072</MemoryInMB>
 <MappedFolders>
  <MappedFolder><HostFolder>$(XmlText $publish)</HostFolder><SandboxFolder>C:\Application</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$(XmlText $inputFolder)</HostFolder><SandboxFolder>C:\TestScripts</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$(XmlText $out)</HostFolder><SandboxFolder>C:\TestResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
 </MappedFolders>
 <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\TestScripts\run.ps1</Command></LogonCommand>
</Configuration>
"@
$file=Join-Path $runRoot 'Toolbox-Offline.wsb'
$configuration | Set-Content -LiteralPath $file -Encoding utf8
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'))
$start.UseShellExecute=$false
$start.ArgumentList.Add($file)
$process=[Diagnostics.Process]::Start($start)
[pscustomobject]@{SandboxProcessId=$process.Id;Configuration=$file;ResultFolder=$out} | ConvertTo-Json -Compress
