$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdkDir = Join-Path $projectRoot '.tools\dotnet'
$sdkVersion = '10.0.401'
$sdkHash = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$dotnetPath = Join-Path $sdkDir 'dotnet.exe'
if (Test-Path -LiteralPath $dotnetPath) {
    $installed = & $dotnetPath --list-sdks
    if ($installed -match [regex]::Escape($sdkVersion)) { return }
}
$downloadDir = Join-Path $projectRoot '.tools\downloads'
New-Item -ItemType Directory -Force $downloadDir | Out-Null
$archive = Join-Path $downloadDir 'dotnet-sdk.zip'
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdkHash) {
    Write-Host 'Downloading the pinned Microsoft .NET SDK into this project...'
    Invoke-WebRequest "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" -OutFile $archive -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdkHash) { throw 'SDK SHA512 verification failed.' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkDir -Force
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'SDK installation did not produce dotnet.exe.' }
