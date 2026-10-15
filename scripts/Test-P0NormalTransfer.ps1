#Requires -Version 7.5
[CmdletBinding(DefaultParameterSetName='Verify')]
param(
    [Parameter(Mandatory,ParameterSetName='Verify')][string]$EvidenceRunDirectory,
    [Parameter(Mandatory,ParameterSetName='Verify')][ValidateSet('android-to-windows','windows-to-android')][string]$Direction,
    [Parameter(Mandatory,ParameterSetName='Verify')][datetimeoffset]$AfterUtc,
    [Parameter(Mandatory,ParameterSetName='PolicySelfTest')][switch]$PolicySelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Read-only acceptance of reports produced by the normal, visible application
# flow. No bootstrap, command, UI, app lifecycle, radio, or firewall operation.
# The optional policy self-test has no filesystem/device reads or evidence writes.
$wwNormalBytes = 1048576
$wwNormalHash = 'fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83'

function Get-WwNormalField {
    param($Value,[string]$Name)
    if ($null -eq $Value -or $null -eq $Value.PSObject.Properties[$Name]) { return $null }
    return ,($Value.PSObject.Properties[$Name].Value)
}
function Assert-WwNormal {
    param([bool]$Condition,[string]$Code)
    if (-not $Condition) { throw ('P0_NORMAL_' + $Code) }
}
function Test-WwNormalBool {
    param($Value,[bool]$Expected)
    return $Value -is [bool] -and $Value -eq $Expected
}
function Test-WwNormalInteger {
    param($Value,[long]$Minimum,[long]$Maximum)
    return ($Value -is [int] -or $Value -is [long] -or $Value -is [uint32] -or $Value -is [uint64]) -and $Value -ge $Minimum -and $Value -le $Maximum
}
function Test-WwNormalVersion {
    param($Value)
    $parsed = $null
    return $Value -is [string] -and $Value -cmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z' -and [version]::TryParse($Value,[ref]$parsed) -and $parsed -ge [version]'0.3.2'
}
function Convert-WwNormalTime {
    param($Value)
    $parsed = [datetimeoffset]::MinValue
    Assert-WwNormal ($Value -is [string] -and $Value -cmatch '(Z|[+-][0-9]{2}:[0-9]{2})\z' -and [datetimeoffset]::TryParse($Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$parsed)) 'REPORT_TIMESTAMP'
    return $parsed
}
function Test-WwNormalIpv4 {
    param($Value)
    $parsed = $null
    if ($Value -isnot [string] -or -not [Net.IPAddress]::TryParse($Value,[ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $bytes = $parsed.GetAddressBytes()
    return $Value -ceq $parsed.ToString() -and $bytes[0] -gt 0 -and $bytes[0] -lt 224 -and -not [Net.IPAddress]::IsLoopback($parsed)
}
function Assert-WwNormalSafeDirectory {
    param([string]$Directory)
    $current = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Directory))
    while ($null -ne $current) {
        if ($current.Exists) { Assert-WwNormal (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'DIRECTORY_REPARSE' }
        $current = $current.Parent
    }
}
function ConvertFrom-WwNormalJson {
    param([string]$Raw)
    Assert-WwNormal ([Text.Encoding]::UTF8.GetByteCount($Raw) -gt 0 -and [Text.Encoding]::UTF8.GetByteCount($Raw) -le 262144) 'REPORT_SIZE'
    $options = [Text.Json.JsonDocumentOptions]::new(); $options.MaxDepth = 16
    $document = [Text.Json.JsonDocument]::Parse($Raw,$options)
    try {
        Assert-WwNormal ($document.RootElement.ValueKind -eq [Text.Json.JsonValueKind]::Object) 'REPORT_JSON_OBJECT'
        function Assert-WwNormalJsonKeys([Text.Json.JsonElement]$Node) {
            if ($Node.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Node.EnumerateObject()) {
                    Assert-WwNormal ($seen.Add($property.Name)) 'DUPLICATE_JSON_KEY'
                    Assert-WwNormalJsonKeys $property.Value
                }
            } elseif ($Node.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($child in $Node.EnumerateArray()) { Assert-WwNormalJsonKeys $child }
            }
        }
        Assert-WwNormalJsonKeys $document.RootElement
    } finally { $document.Dispose() }
    return $Raw | ConvertFrom-Json -Depth 20 -DateKind String
}
function Read-WwNormalWindows {
    param([string]$Path)
    Assert-WwNormalSafeDirectory (Split-Path -Parent $Path)
    $item = Get-Item -LiteralPath $Path
    Assert-WwNormal (-not $item.PSIsContainer -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'REPORT_REPARSE'
    Assert-WwNormal ($item.Length -gt 0 -and $item.Length -le 262144) 'REPORT_SIZE'
    $stream = [IO.File]::Open($item.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $buffer = [byte[]]::new(262145); $length = 0
        while ($length -lt $buffer.Length) {
            $count = $stream.Read($buffer,$length,$buffer.Length-$length)
            if ($count -eq 0) { break }; $length += $count
        }
        Assert-WwNormal ($length -gt 0 -and $length -le 262144) 'REPORT_SIZE'
        $raw = [Text.UTF8Encoding]::new($false,$true).GetString($buffer,0,$length)
    } finally { $stream.Dispose() }
    return ConvertFrom-WwNormalJson $raw
}
function Assert-WwNormalAndroidDirectory {
    param([ValidateSet('files','files/reports','files/p0-received')][string]$Relative)
    $kind = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','stat','-c','%F',$Relative) -TimeoutMs 3000
    Assert-WwNormal ($kind.Trim() -ceq 'directory') 'ANDROID_DIRECTORY_REPARSE'
}
function Read-WwNormalAndroid {
    Assert-WwNormalAndroidDirectory 'files'
    Assert-WwNormalAndroidDirectory 'files/reports'
    $relative = 'files/reports/latest-transfer.json'
    $kind = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','stat','-c','%F',$relative) -TimeoutMs 3000
    Assert-WwNormal ($kind.Trim() -ceq 'regular file') 'ANDROID_REPORT_REPARSE'
    $size = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','stat','-c','%s',$relative) -TimeoutMs 3000
    Assert-WwNormal ($size.Trim() -cmatch '\A[1-9][0-9]{0,5}\z' -and [long]$size.Trim() -le 262144) 'ANDROID_REPORT_SIZE'
    # Bound the read even if the app atomically replaces its report after stat.
    $raw = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','head','-c','262145',$relative) -TimeoutMs 3000
    return ConvertFrom-WwNormalJson $raw
}

function Assert-WwNormalPair {
    param([string]$Direction,$Windows,$Android,[datetimeoffset]$AfterUtc,[datetimeoffset]$NowUtc)
    foreach ($field in @('schema','appVersion','startedUtc','runId','role','state','localIdentity','trustedPeerIdentity','authenticatedPeerIdentity','tlsProtocol','portExchange','preauthorizationSource','endpointSource','nativeLocalAddress','nativeRemoteAddress','socketLocalAddress','socketRemoteAddress','sha256','transferId')) {
        Assert-WwNormal ((Get-WwNormalField $Windows $field) -is [string]) 'WINDOWS_REQUIRED_STRING'
    }
    foreach ($field in @('schema','appVersion','startedAtUtc','finishedAtUtc','id','role','state','localIdentity','confirmedPeerIdentity','authenticatedPeerIdentity','trustMethod','tlsProtocol','sessionResumptionPolicy','peerPublicKeyProof','portExchange','nativeLocalAddress','nativeGroupOwnerAddress','socketLocalAddress','socketRemoteAddress','sha256','transferId','fixture','expectedSha256','outcome')) {
        Assert-WwNormal ((Get-WwNormalField $Android $field) -is [string]) 'ANDROID_REQUIRED_STRING'
    }
    Assert-WwNormal ($Windows.schema -ceq 'wanwei.p0.transfer/1' -and $Android.schema -ceq 'wanwei-android-transfer/0.1') 'TRANSFER_SCHEMA'
    Assert-WwNormal ((Test-WwNormalVersion $Windows.appVersion) -and (Test-WwNormalVersion $Android.appVersion)) 'VERSION_0_3_2_OR_LATER'
    $winStarted = Convert-WwNormalTime $Windows.startedUtc; $phoneStarted = Convert-WwNormalTime $Android.startedAtUtc
    $phoneFinished = Convert-WwNormalTime $Android.finishedAtUtc
    Assert-WwNormal ($AfterUtc.Offset -eq [timespan]::Zero -and $AfterUtc -le $NowUtc -and $winStarted -ge $AfterUtc -and $phoneStarted -ge $AfterUtc) 'FRESH_REPORTS'
    Assert-WwNormal ($winStarted -le $NowUtc.AddSeconds(5) -and $phoneStarted -le $NowUtc.AddSeconds(5) -and $phoneFinished -ge $phoneStarted -and $phoneFinished -le $NowUtc.AddSeconds(5)) 'REPORT_CLOCKS'
    Assert-WwNormal ($Windows.runId -cmatch '\A[0-9a-f]{32}\z' -and $Android.id -cmatch '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z') 'REPORT_RUN_IDS'
    $winError = $Windows.PSObject.Properties['errorCode']; $phoneError = $Android.PSObject.Properties['errorCode']
    Assert-WwNormal ($Windows.state -ceq 'complete' -and $Android.state -ceq 'finished' -and ($null -eq $winError -or $null -eq $winError.Value) -and ($null -eq $phoneError -or $null -eq $phoneError.Value)) 'FINAL_SUCCESS_STATES'
    foreach ($pin in @($Windows.localIdentity,$Windows.trustedPeerIdentity,$Windows.authenticatedPeerIdentity,$Android.localIdentity,$Android.confirmedPeerIdentity,$Android.authenticatedPeerIdentity)) {
        Assert-WwNormal ($pin -is [string] -and $pin -cmatch '\A[0-9a-f]{64}\z') 'FULL_SPKI'
    }
    Assert-WwNormal ($Windows.localIdentity -cne $Android.localIdentity -and $Windows.localIdentity -ceq $Android.confirmedPeerIdentity -and $Windows.localIdentity -ceq $Android.authenticatedPeerIdentity -and $Windows.trustedPeerIdentity -ceq $Android.localIdentity -and $Windows.authenticatedPeerIdentity -ceq $Android.localIdentity) 'RECIPROCAL_PINNED_IDENTITIES'
    Assert-WwNormal ((Test-WwNormalBool $Windows.peerTrustExplicitlyConfirmed $true) -and $Android.trustMethod -ceq 'user_confirmed_full_spki_fingerprint_out_of_band') 'EXPLICIT_FULL_PIN_TRUST'
    Assert-WwNormal ($Windows.tlsProtocol -ceq 'Tls13' -and (Test-WwNormalBool $Windows.sessionResumptionAllowed $false) -and $Android.tlsProtocol -ceq 'TLSv1.3' -and (Test-WwNormalBool $Android.applicationIdentityAuthenticated $true) -and (Test-WwNormalBool $Android.tlsHandshakeCompleted $true) -and (Test-WwNormalBool $Android.sessionTicketsDisabledBeforeHandshake $true) -and (Test-WwNormalBool $Android.freshTlsContextUsedOnce $true) -and $Android.sessionResumptionPolicy -ceq 'fresh_SSLContext_once_session_tickets_disabled_no_early_application_data' -and $Android.peerPublicKeyProof -ceq 'platform_TLS13_mutual_certificate_verify') 'FRESH_MUTUAL_TLS13'
    Assert-WwNormal ($Windows.portExchange -ceq 'native_udp_rendezvous_untrusted_hint' -and $Android.portExchange -ceq 'native_unicast_udp_hint_then_pinned_tls13' -and (Test-WwNormalBool $Android.portHintEstablishesIdentity $false)) 'AUTOMATIC_PORT_EXCHANGE_MODES'
    Assert-WwNormal ((Test-WwNormalBool $Windows.operatorFixturePreauthorized $false) -and (Test-WwNormalBool $Android.operatorFixturePreauthorized $false) -and $Windows.preauthorizationSource -ceq 'none' -and $null -ne $Android.PSObject.Properties['preauthorizationSource'] -and $null -eq $Android.preauthorizationSource) 'NO_OPERATOR_PREAUTHORIZATION'
    Assert-WwNormal ($Windows.endpointSource -ceq 'native_WiFiDirectDevice_and_actual_bound_socket' -and (Test-WwNormalBool $Android.directPathVerified $true)) 'NATIVE_PATH_EVIDENCE'
    foreach ($address in @($Windows.nativeLocalAddress,$Windows.nativeRemoteAddress,$Windows.socketLocalAddress,$Windows.socketRemoteAddress,$Android.nativeLocalAddress,$Android.nativeGroupOwnerAddress,$Android.socketLocalAddress,$Android.socketRemoteAddress)) {
        Assert-WwNormal (Test-WwNormalIpv4 $address) 'ACTUAL_NONLOOPBACK_IPV4'
    }
    Assert-WwNormal ($Windows.nativeLocalAddress -ceq $Windows.socketLocalAddress -and $Windows.nativeRemoteAddress -ceq $Windows.socketRemoteAddress -and $Android.nativeLocalAddress -ceq $Android.socketLocalAddress -and $Android.nativeGroupOwnerAddress -ceq $Android.socketRemoteAddress -and $Windows.socketLocalAddress -ceq $Android.socketRemoteAddress -and $Windows.socketRemoteAddress -ceq $Android.socketLocalAddress -and $Windows.socketLocalAddress -cne $Windows.socketRemoteAddress) 'RECIPROCAL_NATIVE_SOCKET_IPS'
    foreach ($port in @($Windows.socketLocalPort,$Windows.socketRemotePort,$Android.socketLocalPort,$Android.socketRemotePort)) { Assert-WwNormal (Test-WwNormalInteger $port 1 65535) 'SOCKET_PORTS' }
    Assert-WwNormal ($Windows.socketLocalPort -eq $Android.socketRemotePort -and $Windows.socketRemotePort -eq $Android.socketLocalPort) 'RECIPROCAL_SOCKET_PORTS'
    Assert-WwNormal ((Test-WwNormalInteger $Windows.bytes $wwNormalBytes $wwNormalBytes) -and (Test-WwNormalInteger $Android.bytesTransferred $wwNormalBytes $wwNormalBytes) -and $Windows.sha256 -ceq $wwNormalHash -and $Android.sha256 -ceq $wwNormalHash -and $Windows.transferId -cmatch '\A[0-9a-f]{32}\z' -and $Windows.transferId -ceq $Android.transferId -and $Android.fixture -ceq 'ramp256-1mib-v1' -and (Test-WwNormalInteger $Android.expectedSizeBytes $wwNormalBytes $wwNormalBytes) -and $Android.expectedSha256 -ceq $wwNormalHash) 'EXACT_FIXTURE_AND_TASK'
    Assert-WwNormal ((Test-WwNormalBool $Windows.socketClosed $true) -and (Test-WwNormalBool $Windows.cleanupFinished $true) -and (Test-WwNormalBool $Windows.temporaryFileCleanupConfirmed $true) -and (Test-WwNormalBool $Android.socketResourcesClosed $true) -and (Test-WwNormalBool $Android.portHintSocketCleanupConfirmed $true)) 'TRANSPORT_AND_CONTROL_CLOSED'
    foreach ($field in @('storageCleanupNeedsReview','foreignStoragePathPreserved')) {
        if ($null -ne $Android.PSObject.Properties[$field]) { Assert-WwNormal (Test-WwNormalBool $Android.PSObject.Properties[$field].Value $false) 'STORAGE_CLEANUP_FLAGS' }
    }
    if ($Direction -ceq 'android-to-windows') {
        Assert-WwNormal ($Windows.role -ceq 'receiver' -and $Android.role -ceq 'sender' -and (Test-WwNormalBool $Windows.receiverUserAccepted $true) -and (Test-WwNormalBool $Android.userAcceptedOffer $false)) 'RECEIVER_UI_CONSENT_ONLY'
        Assert-WwNormal ((Test-WwNormalBool $Windows.rendezvousControlClosed $true) -and (Test-WwNormalBool $Windows.localSaved $true) -and (Test-WwNormalBool $Windows.peerSaveConfirmed $false) -and (Test-WwNormalBool $Android.savedLocally $false) -and (Test-WwNormalBool $Android.peerSaveConfirmed $true) -and $Android.outcome -ceq 'peer_saved_confirmed') 'WINDOWS_SAVE_AND_COMPLETE'
        Assert-WwNormal ($Windows.savedLocalName -is [string] -and $Windows.savedLocalName -cmatch '\A[0-9a-f]{32}\.bin\z') 'WINDOWS_SAFE_SAVED_NAME'
    } else {
        Assert-WwNormal ($Direction -ceq 'windows-to-android' -and $Windows.role -ceq 'sender' -and $Android.role -ceq 'receiver' -and (Test-WwNormalBool $Windows.receiverUserAccepted $false) -and (Test-WwNormalBool $Android.userAcceptedOffer $true)) 'RECEIVER_UI_CONSENT_ONLY'
        Assert-WwNormal ((Test-WwNormalBool $Windows.localSaved $false) -and (Test-WwNormalBool $Windows.peerSaveConfirmed $true) -and (Test-WwNormalBool $Android.savedLocally $true) -and (Test-WwNormalBool $Android.peerSaveConfirmed $false) -and (Test-WwNormalBool $Android.completeSent $true) -and (Test-WwNormalBool $Android.fileSyncSucceeded $true) -and (Test-WwNormalBool $Android.renameSucceeded $true) -and (Test-WwNormalBool $Android.directorySyncSucceeded $true) -and $Android.outcome -ceq 'saved_locally') 'ANDROID_SAVE_AND_COMPLETE'
        Assert-WwNormal ($Android.savedBasename -is [string] -and $Android.savedBasename -cmatch '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.bin\z') 'ANDROID_SAFE_SAVED_NAME'
    }
}

function Get-WwNormalDiskAudit {
    param([string]$Direction,$Windows,$Android)
    if ($Direction -ceq 'android-to-windows') {
        Assert-WwNormal ($Windows.savedLocalName -cmatch '\A[0-9a-f]{32}\.bin\z') 'WINDOWS_SAFE_SAVED_NAME'
        $base = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WanweiLab\P0Received'))
        Assert-WwNormalSafeDirectory $base
        $file = [IO.Path]::GetFullPath((Join-Path $base $Windows.savedLocalName))
        Assert-WwNormal ($file.StartsWith($base + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'WINDOWS_SAVED_PATH'
        $item = Get-Item -LiteralPath $file
        Assert-WwNormal (-not $item.PSIsContainer -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'WINDOWS_SAVED_REPARSE'
        $stream = [IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            Assert-WwNormal ($stream.Length -eq $wwNormalBytes) 'WINDOWS_SAVED_BYTES'
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            Assert-WwNormal ($stream.Length -eq $wwNormalBytes -and $hash -ceq $wwNormalHash) 'WINDOWS_DISK_HASH'
        } finally { $stream.Dispose() }
        return [pscustomobject]@{Receiver='Windows';SizeBytes=$wwNormalBytes;Sha256=$hash;Method='independent_owned_local_file_read'}
    }
    Assert-WwNormal ($Android.savedBasename -cmatch '\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.bin\z') 'ANDROID_SAFE_SAVED_NAME'
    Assert-WwNormalAndroidDirectory 'files'
    Assert-WwNormalAndroidDirectory 'files/p0-received'
    $relative = 'files/p0-received/' + $Android.savedBasename
    $kind = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','stat','-c','%F',$relative) -TimeoutMs 5000
    Assert-WwNormal ($kind.Trim() -ceq 'regular file') 'ANDROID_SAVED_REPARSE'
    $size = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','stat','-c','%s',$relative) -TimeoutMs 5000
    $digest = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','sha256sum',$relative) -TimeoutMs 5000
    Assert-WwNormal ($digest.Trim() -cmatch ('\A([0-9a-f]{64})[ \t]+\*?' + [regex]::Escape($relative) + '\z')) 'ANDROID_DIGEST_OUTPUT'
    $hash = $Matches[1]
    Assert-WwNormal ($size.Trim() -ceq '1048576' -and $hash -ceq $wwNormalHash) 'ANDROID_DISK_HASH'
    return [pscustomobject]@{Receiver='Android';SizeBytes=$wwNormalBytes;Sha256=$hash;Method='USB_read_of_private_saved_file_digest_and_size_only'}
}

function Convert-WwNormalSafeReport {
    param($Report)
    if ($null -eq $Report) { return $null }
    # Explicit allowlists omit identity pins, certificates, capability nonces,
    # saved names, local paths, hardware interface IDs, and arbitrary error text.
    $safe = [ordered]@{}
    $enums = @{
        schema=@('wanwei.p0.transfer/1','wanwei-android-transfer/0.1'); role=@('sender','receiver');
        state=@('idle','listening','resolving_receiver','connecting','tls_authenticating','authenticated','awaiting_receive_confirmation','awaiting_peer_acceptance','waiting_accept','receiving','sending','waiting_saved_confirmation','local_saved','complete','finished','failed','rejected','cancelled','storage_rejected','cleanup_unconfirmed');
        outcome=@('saved_locally','peer_saved_confirmed','failed','rejected'); tlsProtocol=@('Tls13','TLSv1.3');
        portExchange=@('native_udp_rendezvous_untrusted_hint','native_unicast_udp_hint_then_pinned_tls13','manual_out_of_band','manual_out_of_band_lab_parameters');
        errorCode=@('INVALID_MESSAGE','INVALID_FRAME','UNEXPECTED_MESSAGE','IDENTITY_MISMATCH','SIZE_MISMATCH','HASH_MISMATCH','STORAGE_FAILED','PEER_STORAGE_FAILED','CANCELLED','TIMEOUT','DIRECT_PATH_UNVERIFIED','TLS_AUTHENTICATION_FAILED','CONNECTION_FAILED','STORAGE_CLEANUP_UNCONFIRMED','RENDEZVOUS_TIMEOUT','RENDEZVOUS_UNAVAILABLE','RENDEZVOUS_FAILED','RENDEZVOUS_LIMIT_REACHED','RENDEZVOUS_CLEANUP_UNCONFIRMED','PORT_EXCHANGE_CLEANUP_UNCONFIRMED','AUTOMATIC_ROLE_UNSUPPORTED')
    }
    foreach ($name in $enums.Keys) { $value = Get-WwNormalField $Report $name; if ($null -ne $value) { $safe[$name] = if ($value -is [string] -and $value -cin $enums[$name]) { $value } else { 'UNRECOGNIZED' } } }
    $version = Get-WwNormalField $Report 'appVersion'; $safe.appVersion = if (Test-WwNormalVersion $version) { $version } else { 'UNRECOGNIZED_OR_OLD' }
    foreach ($name in @('peerTrustExplicitlyConfirmed','applicationIdentityAuthenticated','tlsHandshakeCompleted','sessionResumptionAllowed','sessionTicketsDisabledBeforeHandshake','freshTlsContextUsedOnce','operatorFixturePreauthorized','receiverUserAccepted','userAcceptedOffer','localSaved','savedLocally','peerSaveConfirmed','completeSent','fileSyncSucceeded','renameSucceeded','directorySyncSucceeded','socketClosed','cleanupFinished','temporaryFileCleanupConfirmed','socketResourcesClosed','rendezvousControlClosed','portHintSocketCleanupConfirmed','portHintEstablishesIdentity','directPathVerified')) {
        $value = Get-WwNormalField $Report $name; if ($value -is [bool]) { $safe[$name] = $value }
    }
    foreach ($name in @('nativeLocalAddress','nativeRemoteAddress','nativeGroupOwnerAddress','socketLocalAddress','socketRemoteAddress')) { $value = Get-WwNormalField $Report $name; if (Test-WwNormalIpv4 $value) { $safe[$name] = $value } }
    foreach ($name in @('socketLocalPort','socketRemotePort')) { $value = Get-WwNormalField $Report $name; if (Test-WwNormalInteger $value 1 65535) { $safe[$name] = $value } }
    foreach ($name in @('bytes','bytesTransferred')) { $value = Get-WwNormalField $Report $name; if (Test-WwNormalInteger $value 0 $wwNormalBytes) { $safe[$name] = $value } }
    $value = Get-WwNormalField $Report 'sha256'; if ($value -is [string] -and $value -ceq $wwNormalHash) { $safe.sha256 = $value }
    foreach ($name in @('startedUtc','startedAtUtc','finishedAtUtc')) { $value = Get-WwNormalField $Report $name; try { if ($null -ne $value) { $safe[$name] = (Convert-WwNormalTime $value).ToUniversalTime().ToString('O') } } catch { } }
    return [pscustomobject]$safe
}
function Write-WwNormalSafeJson {
    param([string]$Path,$Value)
    Assert-WwNormalSafeDirectory (Split-Path -Parent $Path)
    if ([IO.File]::Exists($Path)) { Assert-WwNormal (([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'OUTPUT_REPARSE' }
    $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $stream = [IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 20)); $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temporary,$Path,$true)
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Invoke-WwNormalPolicySelfTest {
    $now = [datetimeoffset]'2026-10-15T12:00:00Z'; $after = $now.AddMinutes(-1)
    $winPin = 'a' * 64; $phonePin = 'b' * 64
    $windows = [pscustomobject]@{schema='wanwei.p0.transfer/1';appVersion='0.3.2';startedUtc=$now.AddSeconds(-30).ToString('O');runId=('1'*32);role='receiver';state='complete';localIdentity=$winPin;trustedPeerIdentity=$phonePin;authenticatedPeerIdentity=$phonePin;peerTrustExplicitlyConfirmed=$true;tlsProtocol='Tls13';sessionResumptionAllowed=$false;portExchange='native_udp_rendezvous_untrusted_hint';operatorFixturePreauthorized=$false;preauthorizationSource='none';endpointSource='native_WiFiDirectDevice_and_actual_bound_socket';nativeLocalAddress='192.0.2.10';nativeRemoteAddress='192.0.2.20';socketLocalAddress='192.0.2.10';socketRemoteAddress='192.0.2.20';socketLocalPort=49152;socketRemotePort=49153;bytes=1048576;sha256=$wwNormalHash;transferId=('2'*32);socketClosed=$true;cleanupFinished=$true;temporaryFileCleanupConfirmed=$true;receiverUserAccepted=$true;rendezvousControlClosed=$true;localSaved=$true;peerSaveConfirmed=$false;savedLocalName=(('3'*32)+'.bin')}
    $android = [pscustomobject]@{schema='wanwei-android-transfer/0.1';appVersion='0.3.2';startedAtUtc=$now.AddSeconds(-20).ToString('O');finishedAtUtc=$now.AddSeconds(-10).ToString('O');id='11111111-1111-1111-1111-111111111111';role='sender';state='finished';localIdentity=$phonePin;confirmedPeerIdentity=$winPin;authenticatedPeerIdentity=$winPin;trustMethod='user_confirmed_full_spki_fingerprint_out_of_band';tlsProtocol='TLSv1.3';applicationIdentityAuthenticated=$true;tlsHandshakeCompleted=$true;sessionTicketsDisabledBeforeHandshake=$true;freshTlsContextUsedOnce=$true;sessionResumptionPolicy='fresh_SSLContext_once_session_tickets_disabled_no_early_application_data';peerPublicKeyProof='platform_TLS13_mutual_certificate_verify';portExchange='native_unicast_udp_hint_then_pinned_tls13';portHintEstablishesIdentity=$false;operatorFixturePreauthorized=$false;preauthorizationSource=$null;directPathVerified=$true;nativeLocalAddress='192.0.2.20';nativeGroupOwnerAddress='192.0.2.10';socketLocalAddress='192.0.2.20';socketRemoteAddress='192.0.2.10';socketLocalPort=49153;socketRemotePort=49152;bytesTransferred=1048576;sha256=$wwNormalHash;transferId=('2'*32);fixture='ramp256-1mib-v1';expectedSizeBytes=1048576;expectedSha256=$wwNormalHash;socketResourcesClosed=$true;portHintSocketCleanupConfirmed=$true;userAcceptedOffer=$false;savedLocally=$false;peerSaveConfirmed=$true;outcome='peer_saved_confirmed'}
    $cases = [Collections.Generic.List[object]]::new()
    $cases.Add(@{Side='Windows';Field='startedUtc';Value=$after.AddSeconds(-1).ToString('O');Code='FRESH_REPORTS'})
    $cases.Add(@{Side='Android';Field='appVersion';Value='0.3.1';Code='VERSION_0_3_2_OR_LATER'})
    $cases.Add(@{Side='Windows';Field='authenticatedPeerIdentity';Value=('c'*64);Code='RECIPROCAL_PINNED_IDENTITIES'})
    $cases.Add(@{Side='Android';Field='freshTlsContextUsedOnce';Value=$false;Code='FRESH_MUTUAL_TLS13'})
    $cases.Add(@{Side='Windows';Field='portExchange';Value='manual_out_of_band';Code='AUTOMATIC_PORT_EXCHANGE_MODES'})
    $cases.Add(@{Side='Android';Field='operatorFixturePreauthorized';Value=$true;Code='NO_OPERATOR_PREAUTHORIZATION'})
    $cases.Add(@{Side='Windows';Field='receiverUserAccepted';Value=$false;Code='RECEIVER_UI_CONSENT_ONLY'})
    $cases.Add(@{Side='Android';Field='userAcceptedOffer';Value=$true;Code='RECEIVER_UI_CONSENT_ONLY'})
    $cases.Add(@{Side='Windows';Field='socketLocalAddress';Value='127.0.0.1';Code='ACTUAL_NONLOOPBACK_IPV4'})
    $cases.Add(@{Side='Android';Field='socketRemotePort';Value=49154;Code='RECIPROCAL_SOCKET_PORTS'})
    $cases.Add(@{Side='Windows';Field='bytes';Value=1048576.0;Code='EXACT_FIXTURE_AND_TASK'})
    $cases.Add(@{Side='Android';Field='transferId';Value=('4'*32);Code='EXACT_FIXTURE_AND_TASK'})
    $cases.Add(@{Side='Windows';Field='rendezvousControlClosed';Value=$false;Code='WINDOWS_SAVE_AND_COMPLETE'})
    $cases.Add(@{Side='Android';Field='peerSaveConfirmed';Value=$false;Code='WINDOWS_SAVE_AND_COMPLETE'})
    $cases.Add(@{Side='Windows';Field='savedLocalName';Value='../escape.bin';Code='WINDOWS_SAFE_SAVED_NAME'})
    $cases.Add(@{Side='Windows';Field='state';Value=@('failed','complete');Code='WINDOWS_REQUIRED_STRING'})
    $cases.Add(@{Side='Android';Field='schema';Value=@('bad','wanwei-android-transfer/0.1');Code='ANDROID_REQUIRED_STRING'})
    $cases.Add(@{Side='Windows';Field='portExchange';Value=@('bad','native_udp_rendezvous_untrusted_hint');Code='WINDOWS_REQUIRED_STRING'})
    $cases.Add(@{Side='Android';Field='trustMethod';Value=123;Code='ANDROID_REQUIRED_STRING'})
    $cases.Add(@{Side='Windows';Field='endpointSource';Value=$true;Code='WINDOWS_REQUIRED_STRING'})
    $cases.Add(@{Side='Android';Field='storageCleanupNeedsReview';Value=@();Code='STORAGE_CLEANUP_FLAGS'})
    $windows = ConvertFrom-WwNormalJson ($windows | ConvertTo-Json -Depth 10)
    $android = ConvertFrom-WwNormalJson ($android | ConvertTo-Json -Depth 10)
    Assert-WwNormal ($windows.startedUtc -is [string] -and $android.startedAtUtc -is [string] -and $android.finishedAtUtc -is [string]) 'POLICY_JSON_TIMESTAMP_TYPES'
    Assert-WwNormalPair 'android-to-windows' $windows $android $after $now
    foreach ($case in $cases) {
        $win = ConvertFrom-WwNormalJson ($windows | ConvertTo-Json -Depth 10); $phone = ConvertFrom-WwNormalJson ($android | ConvertTo-Json -Depth 10)
        $target = if ($case.Side -ceq 'Windows') { $win } else { $phone }
        $target | Add-Member -NotePropertyName $case.Field -NotePropertyValue $case.Value -Force
        $win = ConvertFrom-WwNormalJson ($win | ConvertTo-Json -Depth 10); $phone = ConvertFrom-WwNormalJson ($phone | ConvertTo-Json -Depth 10)
        $failureCode = $null; try { Assert-WwNormalPair 'android-to-windows' $win $phone $after $now } catch { $failureCode = $_.Exception.Message }
        Assert-WwNormal ($failureCode -ceq ('P0_NORMAL_'+$case.Code)) ('POLICY_NEGATIVE_VECTOR_'+$case.Field.ToUpperInvariant())
    }
    $windows.role='sender';$windows.receiverUserAccepted=$false;$windows.localSaved=$false;$windows.peerSaveConfirmed=$true
    $android.role='receiver';$android.userAcceptedOffer=$true;$android.savedLocally=$true;$android.peerSaveConfirmed=$false;$android.outcome='saved_locally'
    foreach ($field in @('completeSent','fileSyncSucceeded','renameSucceeded','directorySyncSucceeded')) { $android | Add-Member -NotePropertyName $field -NotePropertyValue $true }
    $android | Add-Member -NotePropertyName savedBasename -NotePropertyValue '11111111-1111-1111-1111-111111111111.bin'
    $windows = ConvertFrom-WwNormalJson ($windows | ConvertTo-Json -Depth 10); $android = ConvertFrom-WwNormalJson ($android | ConvertTo-Json -Depth 10)
    Assert-WwNormalPair 'windows-to-android' $windows $android $after $now
    $android.renameSucceeded=$false
    $android = ConvertFrom-WwNormalJson ($android | ConvertTo-Json -Depth 10)
    $failureCode = $null; try { Assert-WwNormalPair 'windows-to-android' $windows $android $after $now } catch { $failureCode=$_.Exception.Message }
    Assert-WwNormal ($failureCode -ceq 'P0_NORMAL_ANDROID_SAVE_AND_COMPLETE') 'POLICY_COMMIT_VECTOR'
    $safe = Convert-WwNormalSafeReport $windows | ConvertTo-Json -Depth 10
    Assert-WwNormal (-not $safe.Contains($winPin) -and -not $safe.Contains($phonePin) -and -not $safe.Contains('savedLocalName')) 'POLICY_SANITIZATION'
    $failureCode=$null; try { $null=ConvertFrom-WwNormalJson '{"state":"complete","state":"failed"}' } catch { $failureCode=$_.Exception.Message }; Assert-WwNormal ($failureCode -ceq 'P0_NORMAL_DUPLICATE_JSON_KEY') 'POLICY_DUPLICATE_JSON'
    '27 pure-memory policy checks passed with exact rejection codes. No device access, disk audit, or acceptance evidence was produced.'
}

if ($PolicySelfTest) { Invoke-WwNormalPolicySelfTest; return }
. (Join-Path $PSScriptRoot 'P0-UsbLabTools.ps1')
$wwNormalOutput = $null; $wwNormalWindows = $null; $wwNormalAndroid = $null; $wwNormalStage = 'evidence_directory_validation'
try {
    $workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $run = [IO.Path]::GetFullPath($EvidenceRunDirectory)
    $privateRoots = @((Join-Path $workspace 'evidence'),(Join-Path $workspace 'artifacts'))
    Assert-WwNormal ([bool]($privateRoots | Where-Object { $run.StartsWith([IO.Path]::GetFullPath($_)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) })) 'IGNORED_EVIDENCE_ROOT'
    Assert-WwNormalSafeDirectory $run
    Assert-WwNormal ([IO.Directory]::Exists($run)) 'EXISTING_EVIDENCE_RUN'
    $output = Join-Path $run $Direction
    $summaryPath = Join-Path $output 'normal-transfer-verification.json'
    $relativeSummary = [IO.Path]::GetRelativePath($workspace,$summaryPath)
    & git -C $workspace check-ignore --quiet -- $relativeSummary
    Assert-WwNormal ($LASTEXITCODE -eq 0) 'EVIDENCE_OUTPUT_NOT_IGNORED'
    Assert-WwNormalSafeDirectory $output
    [void][IO.Directory]::CreateDirectory($output)
    Assert-WwNormalSafeDirectory $output
    $wwNormalOutput = $output
    # Replace any previous result with a failure/in-progress marker before reads.
    Write-WwNormalSafeJson $summaryPath ([pscustomobject]@{Schema='wanwei.p0.normal-ui-acceptance/1';Passed=$false;Stage='reading_reports';HumanOperatorProven=$false})
    Assert-WwNormal ($AfterUtc.Offset -eq [timespan]::Zero -and $AfterUtc -ge [datetimeoffset]::UnixEpoch -and $AfterUtc -le [datetimeoffset]::UtcNow) 'AFTER_UTC'
    $wwNormalStage='read_windows_report'
    $wwNormalWindows = Read-WwNormalWindows (Join-Path $run 'windows/latest-transfer.json')
    $wwNormalStage='read_android_report'
    $wwNormalAndroid = Read-WwNormalAndroid
    $wwNormalStage='report_policy'
    Assert-WwNormalPair $Direction $wwNormalWindows $wwNormalAndroid $AfterUtc ([datetimeoffset]::UtcNow)
    $wwNormalStage='independent_saved_file_digest'
    $disk = Get-WwNormalDiskAudit $Direction $wwNormalWindows $wwNormalAndroid
    Write-WwNormalSafeJson (Join-Path $output 'windows-transfer.json') (Convert-WwNormalSafeReport $wwNormalWindows)
    Write-WwNormalSafeJson (Join-Path $output 'android-transfer.json') (Convert-WwNormalSafeReport $wwNormalAndroid)
    $summary = [pscustomobject]@{Schema='wanwei.p0.normal-ui-acceptance/1';Passed=$true;Direction=$Direction;AfterUtc=$AfterUtc.ToString('O');AfterUtcMeaning='before_this_direction_current_attempt_click';AllowedStartSkewSeconds=0;MaximumFutureClockLeadSeconds=5;TransferId=$wwNormalWindows.transferId;FinishedUtc=[datetimeoffset]::UtcNow.ToString('O');Bytes=$wwNormalBytes;Sha256=$wwNormalHash;IdentityReciprocityVerified=$true;MutualTls13Verified=$true;FreshSessionPolicyVerified=$true;NativeSocketEndpointsReciprocal=$true;SavedAndCompleteConfirmed=$true;TransportAndControlClosed=$true;AutomaticPortExchangeRecorded=$true;OperatorFixturePreauthorization=$false;UiConsentRecorded=$true;HumanOperatorProven=$false;DiskAudit=$disk;UsbCopiedFixtureBytes=$false;DebugUsbConnectionStillPresent=$true;PhysicalCableRemovalVerified=$false;UsbUse='read_only_report_metadata_and_saved_file_digest';OrdinaryUiReportsOnly=$true;NativeGroupCleanup='not_evaluated_here';ApAndInternetContinuity='not_evaluated_here'}
    Write-WwNormalSafeJson $summaryPath $summary
    $summary
} catch {
    $code = if ($_.Exception.Message -cmatch '\AP0_NORMAL_[A-Z0-9_]+\z') { $_.Exception.Message } else { 'P0_NORMAL_OPERATION_OR_REPORT_FAILED' }
    if ($wwNormalOutput) {
        try {
            if ($null -ne $wwNormalWindows) { Write-WwNormalSafeJson (Join-Path $wwNormalOutput 'windows-transfer.json') (Convert-WwNormalSafeReport $wwNormalWindows) }
            if ($null -ne $wwNormalAndroid) { Write-WwNormalSafeJson (Join-Path $wwNormalOutput 'android-transfer.json') (Convert-WwNormalSafeReport $wwNormalAndroid) }
            Write-WwNormalSafeJson (Join-Path $wwNormalOutput 'normal-transfer-verification.json') ([pscustomobject]@{Schema='wanwei.p0.normal-ui-acceptance/1';Passed=$false;Direction=$Direction;Stage=$wwNormalStage;FailureCode=$code;FinishedUtc=[datetimeoffset]::UtcNow.ToString('O');HumanOperatorProven=$false;UiConsentRecorded=$false;UsbCopiedFixtureBytes=$false;PhysicalCableRemovalVerified=$false;NoConnectionControlPerformed=$true;NativeGroupCleanup='not_evaluated_here';ApAndInternetContinuity='not_evaluated_here'})
        } catch { }
    }
    throw ('Normal UI report verification failed: '+$code+'. No connection or device state was changed.')
}
