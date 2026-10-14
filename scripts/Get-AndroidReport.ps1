param([datetimeoffset]$AfterUtc = [datetimeoffset]::MinValue)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$shellApp = New-Object -ComObject Shell.Application
$phones = @($shellApp.Namespace(17).Items() | Where-Object Name -eq 'Honor Magic7')
if ($phones.Count -ne 1) { throw 'Expected one connected Honor Magic7 in file-transfer mode.' }

# Enumerate only the storage root and usual report destinations; never read other phone files.
$reportCandidates = @()
foreach ($volume in @($phones[0].GetFolder.Items())) {
    $rootItems = @($volume.GetFolder.Items())
    $reportCandidates += @($rootItems | Where-Object { -not $_.IsFolder -and $_.Name -match '^wanwei-android-\d+(?:\s*\(\d+\))?\.json$' })
    foreach ($folder in @($rootItems | Where-Object { $_.IsFolder -and $_.Name -in @('Download','Downloads','Documents','My Files','MyFiles',"$([char]0x6211)$([char]0x7684)$([char]0x6587)$([char]0x4EF6)") })) {
        $reportCandidates += @($folder.GetFolder.Items() | Where-Object { -not $_.IsFolder -and $_.Name -match '^wanwei-android-\d+(?:\s*\(\d+\))?\.json$' })
    }
}
if ($reportCandidates.Count -eq 0) { throw 'No Wanwei JSON is visible. Export a new report from the phone app to Download.' }
$selected = $reportCandidates | Sort-Object Name -Descending | Select-Object -First 1
$expectedBytes = [long]$selected.ExtendedProperty('System.Size')
if ($expectedBytes -lt 2 -or $expectedBytes -gt 2000000) { throw 'Unexpected report size.' }
if ([IO.Path]::GetFileName($selected.Name) -ne $selected.Name) { throw 'Unexpected report filename.' }

# A fresh directory preserves earlier evidence even if a phone export reuses a filename.
$snapshotName = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$destination = New-Item -ItemType Directory -Force (Join-Path $projectRoot ('evidence\local\phone-reports\' + $snapshotName))
$localPath = Join-Path $destination.FullName $selected.Name
$shellApp.Namespace($destination.FullName).CopyHere($selected, 20)
$deadline = [datetime]::UtcNow.AddSeconds(15)
do {
    Start-Sleep -Milliseconds 400
    if ((Test-Path -LiteralPath $localPath) -and (Get-Item -LiteralPath $localPath).Length -eq $expectedBytes) {
        $report = Get-Content -LiteralPath $localPath -Raw | ConvertFrom-Json
        if ($report.schema -ne 'wanwei.android.p0.v1') { throw 'Unexpected report schema.' }
        if ([datetimeoffset]$report.generatedAtUtc -le $AfterUtc) { throw 'Latest visible report is still older than the requested test. Wait for MTP to refresh; do not use it as new evidence.' }
        [pscustomobject]@{ Path=$localPath; GeneratedAtUtc=$report.generatedAtUtc; AppVersion=$report.appVersion; Capability=$report.capabilityProbe; Scan=$report.scan; Pairing=$report.pairing }
        return
    }
} while ([datetime]::UtcNow -lt $deadline)
throw 'The diagnostic copy did not complete within 15 seconds.'
