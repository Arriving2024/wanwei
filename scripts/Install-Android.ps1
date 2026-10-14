$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $projectRoot '.tools\android-sdk\platform-tools\adb.exe'
$apk = Join-Path $projectRoot 'artifacts\android\wanwei-android-lab-0.2.5-debug.apk'
if (-not (Test-Path -LiteralPath $adb)) { throw 'Project Android platform-tools missing.' }
if (-not (Test-Path -LiteralPath $apk)) { throw 'APK missing. Run scripts/Build-Android.ps1 first.' }

$debugStatus = & (Join-Path $PSScriptRoot 'Repair-AndroidDebug.ps1')
if (-not $debugStatus.UsbReady) { throw 'A single authorized USB debugging device is not ready.' }

# -d selects the single USB device and refuses ambiguous multiple-device setups.
# Do not print serial numbers, grant permissions, downgrade, or uninstall another app.
$state = & $adb -d get-state 2>&1
if ($LASTEXITCODE -ne 0 -or ($state -join "`n").Trim() -ne 'device') {
    throw 'USB debugging device is not ready. Check the phone connection/authorization and docs/building.md.'
}
& $adb -d install -r $apk
if ($LASTEXITCODE -ne 0) { throw 'APK installation failed. Existing application data has not been deleted.' }
& $adb -d shell am start -W -n 'dev.wanwei.androidlab/.MainActivity'
if ($LASTEXITCODE -ne 0) { throw 'APK was installed but could not be opened.' }
Write-Host 'Wanwei Android Lab 0.2.5 opened. Nearby-device permission is used for discovery/pairing; optional precise-location permission enables anonymous original-AP diagnostics.'
