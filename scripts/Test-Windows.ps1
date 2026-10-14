param([switch]$Radio)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build-Windows.ps1')
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dllPath = Join-Path $projectRoot 'apps\windows\bin\Release\net10.0-windows10.0.26100.0\Wanwei.Windows.dll'
& $dotnetPath $dllPath --self-test
if ($LASTEXITCODE -ne 0) { throw 'Local logic checks failed.' }
if ($Radio) {
    $reportDirectory = Join-Path $projectRoot 'evidence\local'
    & $dotnetPath $dllPath --discover --seconds 5 --out $reportDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Native discovery did not complete. Read the saved report.' }
}
