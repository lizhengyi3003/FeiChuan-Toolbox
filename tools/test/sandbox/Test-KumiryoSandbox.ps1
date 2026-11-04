param([Parameter(Mandatory=$true)][string]$PublishDirectory,[Parameter(Mandatory=$true)][string]$FixtureDirectory,[Parameter(Mandatory=$true)][string]$DriverDirectory,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$Elevated)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$publish=[IO.Path]::GetFullPath($PublishDirectory);$fixture=[IO.Path]::GetFullPath($FixtureDirectory);$driver=[IO.Path]::GetFullPath($DriverDirectory);$out=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath (Join-Path $out 'sandbox.json')) -or (Test-Path -LiteralPath (Join-Path $out 'integration.json'))){throw 'Use a new output directory; previous evidence is preserved.'}
$inputFolder=Join-Path $out 'input';[IO.Directory]::CreateDirectory($inputFolder) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-KumiryoGuest.ps1') -Destination (Join-Path $inputFolder 'run.ps1')
if($Elevated){[IO.File]::WriteAllText((Join-Path $inputFolder 'elevated.flag'),'elevated')}elseif(Test-Path -LiteralPath (Join-Path $inputFolder 'elevated.flag')){throw 'Use a fresh output directory for ordinary tests.'}
function XmlText([string]$value){[Security.SecurityElement]::Escape($value)}
$configuration=@"
<Configuration>
 <Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MemoryInMB>3072</MemoryInMB>
 <MappedFolders>
  <MappedFolder><HostFolder>$(XmlText $publish)</HostFolder><SandboxFolder>C:\Application</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$(XmlText $fixture)</HostFolder><SandboxFolder>C:\ClientInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$(XmlText $driver)</HostFolder><SandboxFolder>C:\DriverInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$(XmlText $inputFolder)</HostFolder><SandboxFolder>C:\TestScripts</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$(XmlText $out)</HostFolder><SandboxFolder>C:\TestResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
 </MappedFolders>
 <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\TestScripts\run.ps1</Command></LogonCommand>
</Configuration>
"@
$file=Join-Path $out 'Kumiryo-Integration.wsb';$configuration | Set-Content -LiteralPath $file -Encoding UTF8
$process=Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsSandbox.exe') -ArgumentList ('"'+$file+'"') -WindowStyle Hidden -PassThru
[pscustomobject]@{SandboxProcessId=$process.Id;Configuration=$file;ResultFolder=$out} | ConvertTo-Json
