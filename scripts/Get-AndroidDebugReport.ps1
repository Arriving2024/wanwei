param(
    [ValidateSet('probe', 'scan', 'pairing')][string]$Kind = 'pairing',
    [datetimeoffset]$AfterUtc = [datetimeoffset]::MinValue,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$wanweiProjectRoot = Split-Path -Parent $PSScriptRoot
$wanweiAdbPath = Join-Path $wanweiProjectRoot '.tools\android-sdk\platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $wanweiAdbPath)) { throw 'Project Android platform-tools missing.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $wanweiProjectRoot 'evidence\local\phone-reports' }
$wanweiDebugStatus = & (Join-Path $PSScriptRoot 'Repair-AndroidDebug.ps1')
if (-not $wanweiDebugStatus.UsbReady) { throw 'A single authorized USB debugging device is not ready. No phone report was read.' }

function Invoke-WanweiAdbRead([string[]]$Arguments) {
    # All arguments are fixed identifiers or an enum-selected report filename.
    # Do not invoke a shell or print a device serial; support Windows PowerShell 5.1.
    $wanweiProcess = [Diagnostics.Process]::new()
    $wanweiProcess.StartInfo = [Diagnostics.ProcessStartInfo]::new()
    $wanweiProcess.StartInfo.FileName = $wanweiAdbPath
    $wanweiProcess.StartInfo.UseShellExecute = $false
    $wanweiProcess.StartInfo.CreateNoWindow = $true
    $wanweiProcess.StartInfo.RedirectStandardOutput = $true
    $wanweiProcess.StartInfo.RedirectStandardError = $true
    $wanweiProcess.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($wanweiArgument in $Arguments) {
        if ($wanweiArgument -notmatch '^[A-Za-z0-9./_-]+$') { throw 'Unexpected diagnostic command argument.' }
    }
    $wanweiProcess.StartInfo.Arguments = $Arguments -join ' '
    try {
        [void]$wanweiProcess.Start()
        $wanweiOutputTask = $wanweiProcess.StandardOutput.ReadToEndAsync()
        $wanweiErrorTask = $wanweiProcess.StandardError.ReadToEndAsync()
        if (-not $wanweiProcess.WaitForExit(15000)) {
            $wanweiProcess.Kill()
            $wanweiProcess.WaitForExit()
            throw 'ADB report read timed out after 15 seconds.'
        }
        $wanweiOutput = $wanweiOutputTask.GetAwaiter().GetResult()
        # Error output can contain device information. Keep it out of saved evidence.
        $null = $wanweiErrorTask.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $wanweiProcess.ExitCode; Output = $wanweiOutput }
    } finally { $wanweiProcess.Dispose() }
}

$wanweiState = Invoke-WanweiAdbRead @('-d', 'get-state')
if ($wanweiState.ExitCode -ne 0 -or $wanweiState.Output.Trim() -ne 'device') {
    throw 'A single authorized USB debugging device is not ready. No phone report was read.'
}

# Only read our own debuggable application report; never inspect unrelated phone data.
$wanweiRead = Invoke-WanweiAdbRead @('-d', 'exec-out', 'run-as', 'dev.wanwei.androidlab', 'cat', "files/reports/latest-$Kind.json")
if ($wanweiRead.ExitCode -ne 0) {
    throw "Cannot read the Wanwei $Kind report. Install the debug APK and run that operation first."
}
$wanweiReportBytes = [Text.Encoding]::UTF8.GetByteCount($wanweiRead.Output)
if ($wanweiReportBytes -lt 2 -or $wanweiReportBytes -gt 2000000) { throw 'Unexpected diagnostic report size.' }
$wanweiReport = $wanweiRead.Output | ConvertFrom-Json
if ($wanweiReport.schema -ne 'wanwei.android.p0.v1' -or $wanweiReport.kind -ne $Kind) { throw 'Unexpected report schema or kind.' }
if (-not $wanweiReport.generatedAtUtc -or [datetimeoffset]$wanweiReport.generatedAtUtc -le $AfterUtc) {
    throw 'The phone report is missing a timestamp or is older than the requested test.'
}

$wanweiSnapshotName = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$wanweiDestination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $wanweiSnapshotName
[void][IO.Directory]::CreateDirectory($wanweiDestination)
$wanweiLocalPath = Join-Path $wanweiDestination "wanwei-android-$Kind.json"
$wanweiFile = [IO.File]::Open($wanweiLocalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $wanweiUtf8 = [Text.UTF8Encoding]::new($false, $true).GetBytes($wanweiRead.Output)
    $wanweiFile.Write($wanweiUtf8, 0, $wanweiUtf8.Length)
    $wanweiFile.Flush($true)
} finally { $wanweiFile.Dispose() }

[pscustomobject]@{
    Path = $wanweiLocalPath
    GeneratedAtUtc = $wanweiReport.generatedAtUtc
    AppVersion = $wanweiReport.appVersion
    Kind = $wanweiReport.kind
    Capability = $wanweiReport.capabilityProbe
    Scan = $wanweiReport.scan
    Pairing = $wanweiReport.pairing
}
