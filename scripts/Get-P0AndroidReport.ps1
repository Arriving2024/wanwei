param(
    [ValidateSet('identity','transfer')][string]$Kind = 'transfer',
    [datetimeoffset]$AfterUtc = [datetimeoffset]::MinValue,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$wanweiRoot = Split-Path -Parent $PSScriptRoot
$wanweiAdb = Join-Path $wanweiRoot '.tools\android-sdk\platform-tools\adb.exe'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $wanweiRoot 'evidence\local\p0-reports' }
$connection = & (Join-Path $PSScriptRoot 'Repair-AndroidDebug.ps1')
if (-not $connection.UsbReady) { throw 'A single authorized USB device is not ready.' }
$filename = if ($Kind -eq 'identity') { 'public-identity.json' } else { 'latest-transfer.json' }
$expectedSchema = if ($Kind -eq 'identity') { 'wanwei-public-identity/0.1' } else { 'wanwei-android-transfer/0.1' }
$process = [Diagnostics.Process]::new()
$process.StartInfo.FileName = $wanweiAdb
$process.StartInfo.Arguments = "-d exec-out run-as dev.wanwei.androidlab cat files/reports/$filename"
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
try {
    [void]$process.Start()
    $outputTask = $process.StandardOutput.ReadToEndAsync()
    $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw 'P0 report read timed out.' }
    $output = $outputTask.GetAwaiter().GetResult()
    $null = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw 'P0 report is unavailable. Run this operation in the app first.' }
} finally { $process.Dispose() }
if ([Text.Encoding]::UTF8.GetByteCount($output) -gt 262144) { throw 'Unexpected P0 report size.' }
$report = $output | ConvertFrom-Json
if ($report.schema -ne $expectedSchema) { throw 'Unexpected P0 report schema.' }
if ($Kind -eq 'identity') {
    if ($report.identity -notmatch '^[0-9a-f]{64}$' -or -not $report.certificateDerBase64) { throw 'Incomplete public identity.' }
} else {
    if (-not $report.startedAtUtc -or [datetimeoffset]$report.startedAtUtc -le $AfterUtc) { throw 'Transfer report predates this attempt.' }
}
$snapshot = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $snapshot
[void][IO.Directory]::CreateDirectory($destination)
$path = Join-Path $destination $filename
[IO.File]::WriteAllText($path, $output, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Path=$path; Kind=$Kind; Report=$report }
