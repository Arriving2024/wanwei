$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build-Windows.ps1')
$appPath = Join-Path $projectRoot 'apps\windows\bin\Release\net10.0-windows10.0.26100.0\Wanwei.Windows.exe'
$reportDirectory = Join-Path $projectRoot 'evidence\local'
# The user is starting the interactive app. No service or background watcher is installed.
Start-Process -FilePath $appPath -ArgumentList @('--out', ('"' + $reportDirectory + '"')) -WorkingDirectory $projectRoot
