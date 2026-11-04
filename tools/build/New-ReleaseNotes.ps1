param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '..\..\CHANGELOG.md')
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$text = Get-Content -LiteralPath $ChangelogPath -Raw -Encoding UTF8
$pattern = '(?ms)^## ' + [regex]::Escape($Version) + ' ·[^\r\n]*\r?\n(?<body>.*?)(?=^## |\z)'
$matches = [regex]::Matches($text, $pattern)
if ($matches.Count -ne 1 -or [string]::IsNullOrWhiteSpace($matches[0].Groups['body'].Value)) {
    throw 'Exactly one nonempty changelog section is required for this release.'
}
@"
## 本版本变更

$($matches[0].Groups['body'].Value.Trim())

## 安装与验证

适用于 Windows 11 x64。安装器和完整便携 ZIP 包含运行时，无需安装 SDK。
升级前保留个人数据备份；故障排查和运行验收见同标签源码中的 docs/VALIDATION.md 与 docs/TROUBLESHOOTING.md。
下载后核对 SHA256SUMS.txt，并验证 GitHub artifact attestations。
二进制尚未做 Windows Authenticode 签名；构建证明不能代替代码签名或运行验收。
WinGet/Scoop 清单供审核和本地使用，不代表已上架社区源。
同标签源码和第三方许可随版本保留。
"@
