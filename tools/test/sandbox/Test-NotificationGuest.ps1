$ErrorActionPreference='Stop'
if($env:USERNAME -ne 'WDAGUtilityAccount' -or -not(Test-Path 'C:\TestScripts\run.ps1')){throw '仅在项目 Windows Sandbox 运行。'}
$app=Start-Process -FilePath 'C:\Application\TunHotspot.exe' -ArgumentList '--qa-notification' -WindowStyle Hidden -PassThru
if(-not $app.WaitForExit(30000)){throw '通知验证超时。'}
Copy-Item -LiteralPath (Join-Path $env:USERPROFILE '.FeiChuanToolbox\qa\notification.json') -Destination 'C:\TestResults\notification-result.json' -Force
