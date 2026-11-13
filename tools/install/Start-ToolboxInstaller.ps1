#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string[]]$InstallerArguments = @()
)

$ErrorActionPreference = 'Stop'
$installer = Get-Item -LiteralPath $InstallerPath
if ($installer -isnot [IO.FileInfo] -or $installer.Name -notmatch '-Setup-x64\.exe$' -or
    ($installer.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw '请选择工具箱的 x64 安装包。'
}

$checksum = Get-Content -LiteralPath ($installer.FullName + '.sha256') -Raw
if ($checksum.TrimEnd() -notmatch '^([a-f0-9]{64})  (.+)$' -or $Matches[2] -cne $installer.Name) {
    throw '安装包旁的 SHA-256 文件格式或文件名不符。'
}
if ((Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash -ne $Matches[1]) {
    throw '安装包 SHA-256 不匹配。'
}

$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if (-not $localData -or -not [IO.Directory]::Exists($localData)) {
    throw '无法定位当前用户的 LocalAppData 目录。'
}
for ($parent = [IO.DirectoryInfo]::new($localData); $null -ne $parent; $parent = $parent.Parent) {
    if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'LocalAppData 路径包含重解析点，无法创建安装临时目录。'
    }
}

$temporary = [IO.DirectoryInfo]::new((Join-Path $localData ('FeiChuan-Setup-' + [guid]::NewGuid().ToString('N'))))
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
foreach ($sid in @($currentUser, $system)) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
[IO.FileSystemAclExtensions]::Create($temporary, $acl)

try {
    $start = [Diagnostics.ProcessStartInfo]::new($installer.FullName)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $installer.DirectoryName
    $start.Environment['TEMP'] = $temporary.FullName
    $start.Environment['TMP'] = $temporary.FullName
    # The installer runs Windows PowerShell 5.1 for upgrade coordination.
    [void]$start.Environment.Remove('PSModulePath')
    foreach ($argument in $InstallerArguments) {
        $start.ArgumentList.Add($argument)
    }
    $setup = [Diagnostics.Process]::Start($start)
    try {
        $setup.WaitForExit()
        $exitCode = $setup.ExitCode
    }
    finally {
        $setup.Dispose()
    }
}
finally {
    $temporary.Refresh()
    if ($temporary.Exists -and -not ($temporary.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        try { $temporary.Delete() }
        catch [IO.IOException] { Write-Host "安装临时文件保留在 $($temporary.FullName)" }
        catch [UnauthorizedAccessException] { Write-Warning "无法清理安装临时目录 $($temporary.FullName)" }
    }
}
exit $exitCode
