param(
    [switch]$RestartServer,
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$adb = Join-Path $projectRoot '.tools\android-sdk\platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw 'Project Android platform-tools missing.' }

# Force libusb only for child processes launched here. Do not change persistent
# environment variables, USB drivers, device settings, or application data.
$previousLibusb = [Environment]::GetEnvironmentVariable('ADB_LIBUSB', 'Process')
$env:ADB_LIBUSB = '1'

function Invoke-BoundedAdb {
    param([string]$Arguments, [int]$TimeoutSeconds = 15)
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $adb
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw 'ADB process could not be started.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # Terminate this command only. Never terminate unrelated ADB clients
            # or an active server that another development operation may use.
            try { $process.Kill() } catch { }
            throw ('ADB command timed out: ' + $Arguments)
        }
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdoutTask.Result
            Stderr = $stderrTask.Result
        }
    } finally {
        $process.Dispose()
    }
}

try {
    # Restart is explicit because it interrupts any active ADB operations.
    # Do not switch to adb-compat while this libusb server is in use.
    if ($RestartServer) {
        $stop = Invoke-BoundedAdb 'kill-server'
        if ($stop.ExitCode -ne 0) { throw 'ADB server could not be stopped.' }
    }
    $start = Invoke-BoundedAdb 'start-server'
    if ($start.ExitCode -ne 0) { throw 'ADB server could not be started.' }

    $status = Invoke-BoundedAdb 'server-status'
    $backend = 'unknown'
    if ($status.Stdout -match '(?m)^usb_backend:\s*(\S+)') { $backend = $Matches[1] }
    $forced = $status.Stdout -match '(?m)^usb_backend_forced:\s*true'
    $version = Invoke-BoundedAdb 'version'
    $versionText = (($version.Stdout -split '\r?\n' | Select-Object -First 2) -join ' ').Trim()

    # Raw device-list output contains serials; retain only enumerated states.
    $list = Invoke-BoundedAdb 'devices -l'
    $states = @($list.Stdout -split '\r?\n' | ForEach-Object {
        if ($_ -match '^\S+\s+(device|unauthorized|offline|recovery|sideload|bootloader)(\s|$)') {
            $Matches[1]
        }
    })
    $usbState = Invoke-BoundedAdb '-d get-state'
    $usbReady = $usbState.ExitCode -eq 0 -and $usbState.Stdout.Trim() -eq 'device'
    $result = [pscustomobject]@{
        Schema = 'wanwei.android.debug-connection.v1'
        CheckedAtUtc = [datetime]::UtcNow.ToString('o')
        AdbPath = $adb
        AdbVersion = $versionText
        UsbBackend = $backend
        UsbBackendForced = $forced
        DeviceCount = $states.Count
        DeviceStates = $states
        UsbReady = $usbReady
        ServerRestartRequested = [bool]$RestartServer
        DriversChanged = $false
        PhoneSettingsChanged = $false
        PhoneApplicationDataRead = $false
    }
    if ($EvidencePath) {
        $resolvedEvidencePath = [IO.Path]::GetFullPath($EvidencePath)
        $evidenceDirectory = Split-Path -Parent $resolvedEvidencePath
        if (-not (Test-Path -LiteralPath $evidenceDirectory)) {
            New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
        }
        $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resolvedEvidencePath -Encoding UTF8
    }
    $result
} finally {
    [Environment]::SetEnvironmentVariable('ADB_LIBUSB', $previousLibusb, 'Process')
}
