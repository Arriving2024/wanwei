param(
    [Parameter(Mandatory)][string]$PrivateSessionPath,
    [switch]$EndAfterSuccess = $true
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'P0-UsbLabTools.ps1')

# Transport-only acceptance harness. Both apps and the native Direct session
# must already be active. USB exchanges laboratory control/report metadata and
# reads a saved-file digest; it never copies the fixture bytes or starts an app.
$wwP0FixtureHash = 'fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83'
$wwP0FixtureBytes = 1048576
$wwP0Stage = 'private_session_validation'
$wwP0Directions = [Collections.Generic.List[object]]::new()
$wwP0Output = $null
$wwP0Private = $null
$wwP0ControlAuthorized = $false
$wwP0LastWindows = $null
$wwP0LastAndroid = $null
$wwP0FailureDiagnostics = $null

function Get-WwP0Field {
    param($Value, [string]$Name)
    if ($null -eq $Value) { return $null }
    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Assert-WwP0 {
    param([bool]$Condition, [string]$Check)
    if (-not $Condition) { throw ('P0_CHECK_' + $Check) }
}
function Assert-WwP0SafeDirectory {
    param([string]$Directory)
    $current = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Directory))
    while ($null -ne $current) {
        if ($current.Exists) { Assert-WwP0 (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'DIRECTORY_REPARSE' }
        $current = $current.Parent
    }
}
function Read-WwP0LocalJson {
    param([string]$Path, [int]$MaximumBytes = 262144, [switch]$AllowMissing)
    try {
        $item = Get-Item -LiteralPath $Path
        Assert-WwP0 (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'REPORT_REPARSE'
        Assert-WwP0 ($item.Length -gt 0 -and $item.Length -le $MaximumBytes) 'REPORT_SIZE'
        return [IO.File]::ReadAllText($item.FullName, [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    } catch {
        if ($AllowMissing -and ($_.Exception -is [IO.IOException] -or $_.CategoryInfo.Category -eq 'ObjectNotFound')) { return $null }
        throw
    }
}
function Read-WwP0PhoneJson {
    param([ValidateSet('identity','transfer','status')][string]$Kind, [int]$TimeoutMs = 2000, [switch]$AllowMissing)
    $name = switch ($Kind) { identity { 'public-identity.json' } transfer { 'latest-transfer.json' } status { 'lab-status.json' } }
    try {
        $raw = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','cat',('files/reports/' + $name)) -TimeoutMs $TimeoutMs
        Assert-WwP0 ([Text.Encoding]::UTF8.GetByteCount($raw) -le 262144) 'ANDROID_REPORT_SIZE'
        return $raw | ConvertFrom-Json
    } catch { if ($AllowMissing) { return $null }; throw }
}
function Write-WwP0SafeJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
}
function Convert-WwP0SafeReport {
    param($Report)
    # Public-key fingerprints are not secrets, but deliberately omit complete
    # identities/certificates and lab capabilities from these acceptance copies.
    $copy = $Report | ConvertTo-Json -Depth 30 | ConvertFrom-Json
    foreach ($property in @($copy.PSObject.Properties)) {
        if ($property.Name -match '(?i)(identity|pin|certificate|sessionNonce|bootstrapPath)') {
            $copy.PSObject.Properties.Remove($property.Name)
        }
    }
    if ($copy.PSObject.Properties['errorCode']) { $copy.errorCode = Get-WwP0AllowedError -Value $copy.errorCode }
    if ($copy.PSObject.Properties['errorType']) { $copy.errorType = Get-WwP0AllowedError -Value $copy.errorType -ExceptionType }
    return $copy
}
function Get-WwP0AllowedError {
    param($Value, [switch]$ExceptionType)
    if ($null -eq $Value -or [string]$Value -eq '') { return $null }
    if ($ExceptionType) {
        $allowed = @('SocketException','SocketTimeoutException','SSLHandshakeException','SSLException','CertificateException','CertificateExpiredException','CertificateNotYetValidException','Failure','PeerFailure','ErrnoException','TimeoutException','ExecutionException','IOException','IllegalStateException','IllegalArgumentException','FileNotFoundException','SecurityException','KeyStoreException','InvalidKeyException')
        if ([string]$Value -cin $allowed) { return [string]$Value }
        return 'OTHER_EXCEPTION_TYPE'
    }
    $code = [string]$Value
    $plain = if ($code.StartsWith('PEER_', [StringComparison]::Ordinal)) { $code.Substring(5) } else { $code }
    $allowed = @('UNSUPPORTED_VERSION','UNSUPPORTED_SERVICE','INVALID_MESSAGE','INVALID_FRAME','UNEXPECTED_MESSAGE','IDENTITY_MISMATCH','SIZE_MISMATCH','HASH_MISMATCH','STORAGE_FAILED','CANCELLED','TIMEOUT','DIRECT_PATH_UNVERIFIED','TLS_AUTHENTICATION_FAILED','CONNECTION_FAILED','STORAGE_CLEANUP_UNCONFIRMED')
    if ($plain -cin $allowed) { return $code }
    return 'UNCLASSIFIED_ERROR_CODE'
}
function Save-WwP0FailureReports {
    param($Windows, $Android)
    if (-not $wwP0Output) { return }
    $directory = Join-Path $wwP0Output 'failure-before-cancel'
    Assert-WwP0SafeDirectory $directory
    [void][IO.Directory]::CreateDirectory($directory)
    if ($null -ne $Windows) { Write-WwP0SafeJson -Path (Join-Path $directory 'windows-transfer.json') -Value (Convert-WwP0SafeReport $Windows) }
    if ($null -ne $Android) { Write-WwP0SafeJson -Path (Join-Path $directory 'android-transfer.json') -Value (Convert-WwP0SafeReport $Android) }
    $script:wwP0FailureDiagnostics = [pscustomobject]@{
        CapturedUtc=[datetimeoffset]::UtcNow.ToString('O');BeforeHarnessCancellation=$true;RelativeDirectory='failure-before-cancel';
        WindowsReportAvailable=($null -ne $Windows);WindowsState=(Get-WwP0Field $Windows 'state');
        WindowsErrorCode=(Get-WwP0AllowedError -Value (Get-WwP0Field $Windows 'errorCode'));
        AndroidReportAvailable=($null -ne $Android);AndroidState=(Get-WwP0Field $Android 'state');
        AndroidErrorCode=(Get-WwP0AllowedError -Value (Get-WwP0Field $Android 'errorCode'));
        AndroidErrorType=(Get-WwP0AllowedError -Value (Get-WwP0Field $Android 'errorType') -ExceptionType)
    }
    Write-WwP0SafeJson -Path (Join-Path $directory 'diagnostics.json') -Value $wwP0FailureDiagnostics
}
function Save-WwP0Direction {
    param([string]$Name, $Windows, $Android)
    $directory = Join-Path $wwP0Output $Name
    Assert-WwP0SafeDirectory $directory
    [void][IO.Directory]::CreateDirectory($directory)
    Write-WwP0SafeJson -Path (Join-Path $directory 'windows-transfer.json') -Value (Convert-WwP0SafeReport $Windows)
    Write-WwP0SafeJson -Path (Join-Path $directory 'android-transfer.json') -Value (Convert-WwP0SafeReport $Android)
}
function Wait-WwP0Pair {
    param([string]$WindowsRole, [string]$AndroidRole, [string]$ExpectedWindowsId,
        [datetimeoffset]$ExpectedWindowsStartedUtc = [datetimeoffset]::MinValue,
        [string]$ExpectedAndroidId, [string]$PreviousWindowsId, [string]$PreviousAndroidId, [datetimeoffset]$AfterUtc)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.ElapsedMilliseconds -lt 30000) {
        $windows = Read-WwP0LocalJson -Path (Join-Path $wwP0Private.reportDirectory 'latest-transfer.json') -AllowMissing
        $remaining = 30000 - $clock.ElapsedMilliseconds
        if ($remaining -le 0) { break }
        $android = Read-WwP0PhoneJson -Kind transfer -TimeoutMs ([int][Math]::Min(1500, $remaining)) -AllowMissing
        if ($null -ne $windows) { $script:wwP0LastWindows = $windows }
        if ($null -ne $android) { $script:wwP0LastAndroid = $android }
        $winId = [string](Get-WwP0Field $windows 'runId'); $androidId = [string](Get-WwP0Field $android 'id')
        $winCurrent = $winId -and (($ExpectedWindowsId -and $winId -eq $ExpectedWindowsId) -or (-not $ExpectedWindowsId -and $winId -ne $PreviousWindowsId)) -and (Get-WwP0Field $windows 'role') -eq $WindowsRole
        $androidCurrent = $androidId -and (($ExpectedAndroidId -and $androidId -eq $ExpectedAndroidId) -or (-not $ExpectedAndroidId -and $androidId -ne $PreviousAndroidId)) -and (Get-WwP0Field $android 'role') -eq $AndroidRole
        if ($winCurrent -and (Get-WwP0Field $windows 'state') -in @('failed','cancelled','rejected','storage_rejected','cleanup_unconfirmed')) { Save-WwP0FailureReports -Windows $windows -Android $android; throw 'P0_CHECK_WINDOWS_TRANSFER_FAILED' }
        if ($androidCurrent -and (Get-WwP0Field $android 'state') -eq 'finished' -and (Get-WwP0Field $android 'outcome') -notin @('saved_locally','peer_saved_confirmed')) { Save-WwP0FailureReports -Windows $windows -Android $android; throw 'P0_CHECK_ANDROID_TRANSFER_FAILED' }
        if ($winCurrent -and $androidCurrent -and (Get-WwP0Field $windows 'state') -eq 'complete' -and (Get-WwP0Field $windows 'cleanupFinished') -eq $true -and (Get-WwP0Field $android 'state') -eq 'finished') {
            Assert-WwP0 ([datetimeoffset]$android.startedAtUtc -ge $AfterUtc.AddSeconds(-10)) 'ANDROID_FRESH_TIMESTAMP'
            if ($ExpectedWindowsId) { Assert-WwP0 ([datetimeoffset]$windows.startedUtc -eq $ExpectedWindowsStartedUtc) 'WINDOWS_EXPECTED_RECEIVER_TIMESTAMP' }
            else { Assert-WwP0 ([datetimeoffset]$windows.startedUtc -ge $AfterUtc.AddSeconds(-2)) 'WINDOWS_FRESH_SENDER_TIMESTAMP' }
            Assert-WwP0 ([datetimeoffset]$windows.startedUtc -le [datetimeoffset]::UtcNow.AddSeconds(5)) 'WINDOWS_TIMESTAMP_NOT_FUTURE'
            return [pscustomobject]@{Windows=$windows;Android=$android}
        }
        Start-Sleep -Milliseconds 200
    }
    Save-WwP0FailureReports -Windows $wwP0LastWindows -Android $wwP0LastAndroid
    throw 'P0_CHECK_TRANSFER_WAIT_30S_TIMEOUT'
}
function Test-WwP0Direction {
    param([string]$Direction, $Windows, $Android)
    Assert-WwP0 ($Windows.schema -eq 'wanwei.p0.transfer/1' -and $Android.schema -eq 'wanwei-android-transfer/0.1') 'TRANSFER_SCHEMA'
    Assert-WwP0 ($Windows.state -eq 'complete' -and $Android.state -eq 'finished') 'FINAL_STATES'
    Assert-WwP0 ($Windows.localIdentity -ceq $wwP0Private.windowsPin -and $Windows.trustedPeerIdentity -ceq $wwP0Private.androidPin -and $Windows.authenticatedPeerIdentity -ceq $wwP0Private.androidPin) 'WINDOWS_FULL_IDENTITY_BINDING'
    Assert-WwP0 ($Android.localIdentity -ceq $wwP0Private.androidPin -and $Android.confirmedPeerIdentity -ceq $wwP0Private.windowsPin -and $Android.authenticatedPeerIdentity -ceq $wwP0Private.windowsPin) 'ANDROID_FULL_IDENTITY_BINDING'
    Assert-WwP0 ($Windows.tlsProtocol -eq 'Tls13' -and $Windows.sessionResumptionAllowed -eq $false -and $Android.tlsProtocol -eq 'TLSv1.3' -and $Android.applicationIdentityAuthenticated -eq $true -and $Android.tlsHandshakeCompleted -eq $true -and $Android.sessionTicketsDisabledBeforeHandshake -eq $true -and $Android.freshTlsContextUsedOnce -eq $true) 'MUTUAL_TLS13'
    Assert-WwP0 ($Windows.endpointSource -eq 'native_WiFiDirectDevice_and_actual_bound_socket' -and $Android.directPathVerified -eq $true -and $Windows.nativeLocalAddress -eq $Windows.socketLocalAddress -and $Windows.nativeRemoteAddress -eq $Windows.socketRemoteAddress -and $Android.nativeLocalAddress -eq $Android.socketLocalAddress) 'NATIVE_SOCKET_PATH'
    Assert-WwP0 ($Windows.socketLocalAddress -eq $Android.socketRemoteAddress -and $Windows.socketRemoteAddress -eq $Android.socketLocalAddress -and [int]$Windows.socketLocalPort -eq [int]$Android.socketRemotePort -and [int]$Windows.socketRemotePort -eq [int]$Android.socketLocalPort) 'RECIPROCAL_SOCKET_ENDPOINTS'
    foreach ($address in @($Windows.socketLocalAddress, $Windows.socketRemoteAddress)) {
        $parsed = $null
        Assert-WwP0 ([Net.IPAddress]::TryParse($address, [ref]$parsed) -and $parsed.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($parsed) -and -not $parsed.Equals([Net.IPAddress]::Any)) 'IPV4_DIRECT_ENDPOINT'
    }
    Assert-WwP0 ($Windows.bytes -eq $wwP0FixtureBytes -and $Android.bytesTransferred -eq $wwP0FixtureBytes -and $Windows.sha256 -ceq $wwP0FixtureHash -and $Android.sha256 -ceq $wwP0FixtureHash -and $Windows.transferId -ceq $Android.transferId -and $Windows.transferId -cmatch '^[0-9a-f]{32}\z') 'EXACT_FIXTURE_AND_TASK'
    Assert-WwP0 ($Windows.socketClosed -eq $true -and $Windows.cleanupFinished -eq $true -and $Windows.temporaryFileCleanupConfirmed -eq $true -and $Android.socketResourcesClosed -eq $true) 'TRANSPORT_CLEANUP'
    Assert-WwP0 ($Windows.operatorFixturePreauthorized -eq $true -and $Windows.receiverUserAccepted -eq $false -and $Android.operatorFixturePreauthorized -eq $true -and $Android.userAcceptedOffer -eq $false) 'HONEST_EXPERIMENT_PREAUTHORIZATION'
    $disk = $null
    if ($Direction -eq 'android-to-windows') {
        Assert-WwP0 ($Windows.role -eq 'receiver' -and $Windows.localSaved -eq $true -and $Android.role -eq 'sender' -and $Android.peerSaveConfirmed -eq $true -and $Android.outcome -eq 'peer_saved_confirmed') 'WINDOWS_SAVED_ANDROID_CONFIRMED'
        Assert-WwP0 ($Windows.savedLocalName -cmatch '^[0-9a-f]{32}\.bin$') 'WINDOWS_SAFE_SAVED_NAME'
        $base = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WanweiLab\P0Received'))
        $file = [IO.Path]::GetFullPath((Join-Path $base $Windows.savedLocalName))
        Assert-WwP0SafeDirectory $base
        Assert-WwP0 ($file.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'WINDOWS_SAVE_DIRECTORY'
        $item = Get-Item -LiteralPath $file
        Assert-WwP0 (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and $item.Length -eq $wwP0FixtureBytes) 'WINDOWS_SAVED_FILE_BYTES'
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert-WwP0 ($hash -ceq $wwP0FixtureHash) 'WINDOWS_INDEPENDENT_DISK_HASH'
        $disk = [pscustomobject]@{Receiver='Windows';SizeBytes=$item.Length;Sha256=$hash;Method='independent_local_file_read'}
    } else {
        Assert-WwP0 ($Windows.role -eq 'sender' -and $Windows.peerSaveConfirmed -eq $true -and $Android.role -eq 'receiver' -and $Android.savedLocally -eq $true -and $Android.completeSent -eq $true -and $Android.fileSyncSucceeded -eq $true -and $Android.directorySyncSucceeded -eq $true -and $Android.outcome -eq 'saved_locally') 'ANDROID_SAVED_WINDOWS_CONFIRMED'
        Assert-WwP0 ($Android.savedBasename -cmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.bin$') 'ANDROID_SAFE_SAVED_NAME'
        $relative = 'files/p0-received/' + $Android.savedBasename
        $hashOutput = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','sha256sum',$relative) -TimeoutMs 5000
        $pattern = '^([0-9a-f]{64})\s+\*?' + [regex]::Escape($relative) + '\s*$'
        Assert-WwP0 ($hashOutput -cmatch $pattern) 'ANDROID_DISK_HASH_OUTPUT'
        $hash = $Matches[1]
        $sizeOutput = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','stat','-c','%s',$relative) -TimeoutMs 5000
        Assert-WwP0 ($sizeOutput.Trim() -ceq '1048576' -and $hash -ceq $wwP0FixtureHash) 'ANDROID_INDEPENDENT_DISK_HASH'
        $disk = [pscustomobject]@{Receiver='Android';SizeBytes=$wwP0FixtureBytes;Sha256=$hash;Method='USB_read_of_private_saved_file_digest_and_size_only'}
    }
    return [pscustomobject]@{Direction=$Direction;Passed=$true;Bytes=$wwP0FixtureBytes;Sha256=$wwP0FixtureHash;
        IdentityReciprocityVerified=$true;MutualTls13Verified=$true;NativeSocketEndpointsReciprocal=$true;
        SavedAndCompleteConfirmed=$true;SocketsClosed=$true;OperatorFixedFixturePreauthorized=$true;HumanAcceptButtonClaimed=$false;DiskAudit=$disk}
}

try {
    $sessionFile = [IO.Path]::GetFullPath($PrivateSessionPath)
    Assert-WwP0 ([IO.Path]::GetFileName($sessionFile) -eq 'private-session.json') 'PRIVATE_SESSION_FILE_NAME'
    $workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $privateRoots = @((Join-Path $workspace 'evidence'), (Join-Path $workspace 'artifacts'))
    Assert-WwP0 ([bool]($privateRoots | Where-Object { $sessionFile.StartsWith([IO.Path]::GetFullPath($_) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })) 'IGNORED_PRIVATE_SESSION_DIRECTORY'
    Assert-WwP0SafeDirectory (Split-Path -Parent $sessionFile)
    $wwP0Private = Read-WwP0LocalJson -Path $sessionFile -MaximumBytes 16384
    foreach ($key in @('sessionNonce','windowsPin','androidPin','reportDirectory','bootstrapPath')) { Assert-WwP0 ([bool](Get-WwP0Field $wwP0Private $key)) 'PRIVATE_SESSION_FIELDS' }
    Assert-WwP0 ($wwP0Private.sessionNonce -cmatch '^[0-9a-f]{32}\z' -and $wwP0Private.windowsPin -cmatch '^[0-9a-f]{64}\z' -and $wwP0Private.androidPin -cmatch '^[0-9a-f]{64}\z') 'PRIVATE_SESSION_IDENTITIES'
    $wwP0Output = Split-Path -Parent $sessionFile
    $wwP0Private.reportDirectory = [IO.Path]::GetFullPath($wwP0Private.reportDirectory)
    Assert-WwP0 ($wwP0Private.reportDirectory.StartsWith($wwP0Output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'REPORT_DIRECTORY_WITHIN_PRIVATE_RUN'
    Assert-WwP0SafeDirectory $wwP0Private.reportDirectory
    $bootstrap = Read-WwP0LocalJson -Path ([IO.Path]::GetFullPath($wwP0Private.bootstrapPath)) -MaximumBytes 16384
    Assert-WwP0 ($bootstrap.sessionNonce -ceq $wwP0Private.sessionNonce -and $bootstrap.peerPin -ceq $wwP0Private.androidPin -and $bootstrap.fixtureConsent -eq $true -and $bootstrap.fixture -eq 'ramp256-1mib-v1') 'WINDOWS_BOOTSTRAP_BINDING'
    $wwP0Stage = 'active_lab_and_listening_validation'
    $windowsLab = Read-WwP0LocalJson -Path (Join-Path $wwP0Private.reportDirectory 'p0-lab-session.json')
    $nonceToken = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($wwP0Private.sessionNonce))).ToLowerInvariant()
    Assert-WwP0 ($windowsLab.sessionToken -ceq $nonceToken -and $windowsLab.state -eq 'receiving_fixture' -and $windowsLab.transfersStarted -eq 1) 'ACTIVE_WINDOWS_INITIAL_RECEIVER'
    $androidLab = Read-WwP0PhoneJson -Kind status
    Assert-WwP0 ($androidLab.state -eq 'active' -and $androidLab.sessionToken -ceq $nonceToken) 'ACTIVE_ANDROID_SAME_LAB'
    $windowsIdentity = Read-WwP0LocalJson -Path (Join-Path $wwP0Private.reportDirectory 'p0-public-identity.json')
    $androidIdentity = Read-WwP0PhoneJson -Kind identity
    Assert-WwP0 ($windowsIdentity.identity -ceq $wwP0Private.windowsPin -and $androidIdentity.identity -ceq $wwP0Private.androidPin) 'LIVE_PUBLIC_IDENTITY_MATCH'
    $wwP0ControlAuthorized = $true
    $firstWin = Read-WwP0LocalJson -Path (Join-Path $wwP0Private.reportDirectory 'latest-transfer.json')
    Assert-WwP0 ($firstWin.schema -eq 'wanwei.p0.transfer/1' -and $firstWin.role -eq 'receiver' -and $firstWin.state -eq 'listening' -and $firstWin.cleanupFinished -eq $false -and $firstWin.socketClosed -eq $false -and $firstWin.bytes -eq 0 -and $firstWin.nativeLocalAddress -eq $firstWin.socketLocalAddress -and $firstWin.socketLocalPort -ge 1 -and $firstWin.socketLocalPort -le 65535) 'FRESH_WINDOWS_LISTENER'
    Assert-WwP0 (([datetimeoffset]::UtcNow - [datetimeoffset]$firstWin.startedUtc).TotalSeconds -ge -5 -and ([datetimeoffset]::UtcNow - [datetimeoffset]$firstWin.startedUtc).TotalSeconds -lt 25) 'LISTENER_WITHIN_TCP_WAIT_BUDGET'
    $previousAndroid = Read-WwP0PhoneJson -Kind transfer -AllowMissing
    $wwP0LastAndroid = $previousAndroid
    # Re-read after the USB metadata query. The TCP listener may have expired
    # while reading that report; never send to a stale port just because its
    # earlier snapshot was within the 30 second establishment budget.
    $freshWin = Read-WwP0LocalJson -Path (Join-Path $wwP0Private.reportDirectory 'latest-transfer.json')
    $wwP0LastWindows = $freshWin
    Assert-WwP0 ($freshWin.runId -ceq $firstWin.runId -and $freshWin.startedUtc -ceq $firstWin.startedUtc -and $freshWin.state -eq 'listening' -and $freshWin.cleanupFinished -eq $false -and $freshWin.socketLocalAddress -eq $firstWin.socketLocalAddress -and $freshWin.socketLocalPort -eq $firstWin.socketLocalPort) 'LISTENER_STILL_ACTIVE_BEFORE_SEND'
    Assert-WwP0 (([datetimeoffset]::UtcNow - [datetimeoffset]$freshWin.startedUtc).TotalSeconds -lt 25) 'LISTENER_BUDGET_BEFORE_SEND'
    $wwP0Stage = 'android_to_windows_send'
    $firstAfter = [datetimeoffset]::UtcNow
    $null = Send-P0AndroidLabCommand -SessionNonce $wwP0Private.sessionNonce -Action send -PeerAddress $firstWin.socketLocalAddress -Port ([int]$firstWin.socketLocalPort)
    $first = Wait-WwP0Pair -WindowsRole receiver -AndroidRole sender -ExpectedWindowsId $firstWin.runId -ExpectedWindowsStartedUtc ([datetimeoffset]$firstWin.startedUtc) -PreviousAndroidId ([string](Get-WwP0Field $previousAndroid 'id')) -AfterUtc $firstAfter
    Save-WwP0Direction -Name 'android-to-windows' -Windows $first.Windows -Android $first.Android
    $wwP0Stage = 'android_to_windows_validation'
    $wwP0Directions.Add((Test-WwP0Direction -Direction 'android-to-windows' -Windows $first.Windows -Android $first.Android))
    $wwP0Stage = 'windows_to_android_receive'
    $secondAfter = [datetimeoffset]::UtcNow
    $null = Send-P0AndroidLabCommand -SessionNonce $wwP0Private.sessionNonce -Action receive
    $listenClock = [Diagnostics.Stopwatch]::StartNew(); $phoneListener = $null
    while ($listenClock.ElapsedMilliseconds -lt 10000) {
        $remaining = 10000 - $listenClock.ElapsedMilliseconds
        if ($remaining -le 0) { break }
        $candidate = Read-WwP0PhoneJson -Kind transfer -TimeoutMs ([int][Math]::Min(1500, $remaining)) -AllowMissing
        if ($null -ne $candidate) { $wwP0LastAndroid = $candidate }
        if ($null -ne $candidate -and $candidate.id -ne $first.Android.id -and $candidate.role -eq 'receiver' -and $candidate.state -eq 'listening' -and [datetimeoffset]$candidate.startedAtUtc -ge $secondAfter.AddSeconds(-10)) { $phoneListener = $candidate; break }
        Start-Sleep -Milliseconds 150
    }
    Assert-WwP0 ($null -ne $phoneListener -and $phoneListener.listenerPort -ge 1 -and $phoneListener.listenerPort -le 65535 -and $phoneListener.listenerAddress -eq $first.Windows.nativeRemoteAddress) 'FRESH_ANDROID_LISTENER'
    $wwP0Stage = 'windows_to_android_send'
    $null = Send-P0WindowsLabCommand -ReportDirectory $wwP0Private.reportDirectory -SessionNonce $wwP0Private.sessionNonce -Action send -Port ([int]$phoneListener.listenerPort)
    $second = Wait-WwP0Pair -WindowsRole sender -AndroidRole receiver -ExpectedAndroidId $phoneListener.id -PreviousWindowsId $first.Windows.runId -PreviousAndroidId $first.Android.id -AfterUtc $secondAfter
    Save-WwP0Direction -Name 'windows-to-android' -Windows $second.Windows -Android $second.Android
    $wwP0Stage = 'windows_to_android_validation'
    $wwP0Directions.Add((Test-WwP0Direction -Direction 'windows-to-android' -Windows $second.Windows -Android $second.Android))
    $nativeReleased = $false
    if ($EndAfterSuccess) {
        $wwP0Stage = 'successful_transport_and_native_cleanup'
        $endAfter = [datetimeoffset]::UtcNow
        $null = Send-P0WindowsLabCommand -ReportDirectory $wwP0Private.reportDirectory -SessionNonce $wwP0Private.sessionNonce -Action end
        $null = Send-P0AndroidLabCommand -SessionNonce $wwP0Private.sessionNonce -Action cancel
        $cleanupClock = [Diagnostics.Stopwatch]::StartNew(); $native = $null
        while ($cleanupClock.ElapsedMilliseconds -lt 30000) {
            $files = Get-ChildItem -LiteralPath $wwP0Private.reportDirectory -Filter 'windows-*.json' -File | Where-Object { $_.LastWriteTimeUtc -ge $endAfter.UtcDateTime } | Sort-Object LastWriteTimeUtc -Descending
            if ($files) {
                # Native reports may contain up to 600 one-second AP samples;
                # retain a separate bounded cap above the small transport JSON.
                $candidate = Read-WwP0LocalJson -Path $files[0].FullName -MaximumBytes 2097152 -AllowMissing
                $pairing = Get-WwP0Field $candidate 'pairingSession'
                $matchingEndpoints = @()
                if ($pairing) { $matchingEndpoints = @((Get-WwP0Field $pairing 'endpointPairs') | Where-Object { $null -ne $_ -and $_.localAddress -eq $first.Windows.nativeLocalAddress -and $_.remoteAddress -eq $first.Windows.nativeRemoteAddress }) }
                if ($pairing -and $matchingEndpoints.Count -gt 0 -and (Get-WwP0Field $pairing 'connectedObserved') -eq $true -and (Get-WwP0Field $pairing 'operatorFixturePreauthorized') -eq $true -and (Get-WwP0Field $pairing 'userAcceptedRequest') -eq $false -and (Get-WwP0Field $pairing 'publisherStopConfirmed') -eq $true -and (Get-WwP0Field $pairing 'resourceReleaseConfirmed') -eq $true -and [datetimeoffset]$candidate.startedUtc -le [datetimeoffset]$first.Windows.startedUtc) { $native = $candidate; $nativeReleased = $true; break }
            }
            Start-Sleep -Milliseconds 200
        }
        Assert-WwP0 $nativeReleased 'WINDOWS_NATIVE_OWNED_RESOURCE_RELEASE'
        Write-WwP0SafeJson -Path (Join-Path $wwP0Output 'windows-native-cleanup.json') -Value (Convert-WwP0SafeReport $native)
    }
    $wwP0Stage = 'passed'
    $summary = [pscustomobject]@{Schema='wanwei.p0.usb-transport-acceptance/1';Passed=$true;FinishedUtc=[datetimeoffset]::UtcNow.ToString('O');
        Directions=@($wwP0Directions);Fixture='ramp256-1mib-v1';UsbCopiedFixtureBytes=$false;TransportOnlyHarness=$true;
        WindowsEndRequested=[bool]$EndAfterSuccess;WindowsNativeOwnedResourceReleaseConfirmed=$nativeReleased;
        AndroidNativeGroupCleanup='outside_this_transport_harness';ApAndInternetContinuity='not_evaluated_here'}
    Write-WwP0SafeJson -Path (Join-Path $wwP0Output 'transfer-verification.json') -Value $summary
    $summary
} catch {
    $checkCode = if ($_.Exception.Message -cmatch '^P0_CHECK_[A-Z0-9_]+\z') { $_.Exception.Message } else { 'P0_CHECK_OPERATION_OR_REPORT_FAILED' }
    if ($wwP0Output -and $null -eq $wwP0FailureDiagnostics) {
        try { Save-WwP0FailureReports -Windows $wwP0LastWindows -Android $wwP0LastAndroid } catch { }
    }
    # Cancel only the app's current fixture transport. Leave native/group/UI
    # teardown to the operator on a failed experiment; do not continue direction 2.
    if ($wwP0ControlAuthorized) {
        try { $null = Send-P0AndroidLabCommand -SessionNonce $wwP0Private.sessionNonce -Action cancel } catch { }
        try { $null = Send-P0WindowsLabCommand -ReportDirectory $wwP0Private.reportDirectory -SessionNonce $wwP0Private.sessionNonce -Action cancel } catch { }
    }
    if ($wwP0Output) {
        $failure = [pscustomobject]@{Schema='wanwei.p0.usb-transport-acceptance/1';Passed=$false;Stage=$wwP0Stage;FailureCode=$checkCode;FailureDiagnostics=$wwP0FailureDiagnostics;FinishedUtc=[datetimeoffset]::UtcNow.ToString('O');Directions=@($wwP0Directions);UsbCopiedFixtureBytes=$false;ApAndInternetContinuity='not_evaluated_here'}
        Write-WwP0SafeJson -Path (Join-Path $wwP0Output 'transfer-verification.json') -Value $failure
    }
    throw ('P0 USB transport verification failed at ' + $wwP0Stage + ': ' + $checkCode + '. See the local sanitized evidence.')
}
