param([Parameter(Mandatory=$true)][string]$PreviousInstaller,[Parameter(Mandatory=$true)][string]$CandidateInstaller,[Parameter(Mandatory=$true)][string]$OutputDirectory,[string]$GuestScript=(Join-Path $PSScriptRoot 'Test-InstallerUpgradeGuest.ps1'))
$ErrorActionPreference='Stop'
$out=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath $out) -and @(Get-ChildItem -LiteralPath $out -Force).Count){throw 'Use a fresh simulation directory.'}
$inputFolder=Join-Path $out 'input';$resultFolder=Join-Path $out 'output'
[IO.Directory]::CreateDirectory($inputFolder) | Out-Null
[IO.Directory]::CreateDirectory($resultFolder) | Out-Null
Copy-Item -LiteralPath $PreviousInstaller -Destination (Join-Path $inputFolder 'previous-setup.exe')
Copy-Item -LiteralPath $CandidateInstaller -Destination (Join-Path $inputFolder 'candidate-setup.exe')
Copy-Item -LiteralPath $GuestScript -Destination (Join-Path $inputFolder 'run.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
Copy-Item -LiteralPath (Join-Path $root 'installer\Upgrade-Coordinator.ps1') -Destination (Join-Path $inputFolder 'Upgrade-Coordinator.ps1')
@{Previous=(Get-FileHash -LiteralPath $PreviousInstaller).Hash;Candidate=(Get-FileHash -LiteralPath $CandidateInstaller).Hash;GuestScript=(Get-FileHash -LiteralPath $GuestScript).Hash;Started=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'inputs.json') -Encoding utf8
$inXml=[Security.SecurityElement]::Escape($inputFolder);$outXml=[Security.SecurityElement]::Escape($resultFolder)
$xml=@"
<Configuration>
 <Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MemoryInMB>3072</MemoryInMB>
 <MappedFolders>
  <MappedFolder><HostFolder>$inXml</HostFolder><SandboxFolder>C:\TestScripts</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
  <MappedFolder><HostFolder>$outXml</HostFolder><SandboxFolder>C:\TestResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
 </MappedFolders>
 <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\TestScripts\run.ps1</Command></LogonCommand>
</Configuration>
"@
$file=Join-Path $out 'Installer-Upgrade.wsb';$xml | Set-Content -LiteralPath $file -Encoding utf8
$process=Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsSandbox.exe') -ArgumentList ('"'+$file+'"') -WindowStyle Hidden -PassThru
[pscustomobject]@{ProcessId=$process.Id;Configuration=$file;ResultFolder=$resultFolder} | ConvertTo-Json
