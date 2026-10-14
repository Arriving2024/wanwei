$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Initialize-Toolchain.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = Join-Path $projectRoot '.tools\dotnet'
$dotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
Push-Location $projectRoot
try {
    & $dotnetPath restore 'apps\windows\Wanwei.Windows.csproj' --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
    & $dotnetPath build 'apps\windows\Wanwei.Windows.csproj' -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
} finally { Pop-Location }
