param(
    [Parameter(Mandatory = $true)][string]$WindowsReport,
    [Parameter(Mandatory = $true)][string]$AndroidReport
)
$ErrorActionPreference = 'Stop'

function Read-WanweiReport([string]$Path, [string]$Schema) {
    $file = Get-Item -LiteralPath $Path
    if ($file.PSIsContainer -or $file.Length -gt 2000000) { throw 'Expected a diagnostic JSON file below 2 MB.' }
    $report = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.schema -ne $Schema) { throw 'Unexpected report schema.' }
    return $report
}
function Convert-WanweiUtc($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    return ([datetimeoffset]$Value).ToUniversalTime().ToString('o')
}

# Read existing reports only. Neither device is contacted or changed.
# These are separate observations, not proof of peer identity or data transfer.
$windows = Read-WanweiReport $WindowsReport 'wanwei.windows.probe/1'
$android = Read-WanweiReport $AndroidReport 'wanwei.android.p0.v1'
if ($null -eq $windows.pairingSession -or $android.kind -ne 'pairing' -or $null -eq $android.pairing) {
    throw 'Both reports must contain a pairing attempt.'
}
$w = $windows.pairingSession
$a = $android.pairing
$endpointGroup = $a.endpointGroup
if ($null -eq $endpointGroup) { $endpointGroup = $a.establishedGroup }

[pscustomobject]@{
    Schema = 'wanwei.pairing.observation-summary.v1'
    Windows = [pscustomobject]@{
        Source = (Get-Item -LiteralPath $WindowsReport).FullName
        Version = $windows.appVersion
        TestStartedAtUtc = Convert-WanweiUtc $windows.startedUtc
        Outcome = $w.outcome
        DurationMs = $w.durationMs
        Timing = $w.timing
        UserAcceptedRequest = $w.userAcceptedRequest
        SystemPairingStatus = $w.systemPairingStatus
        ConnectedObserved = $w.connectedObserved
        EndpointPairs = @($w.endpointPairs)
        RequestsReceived = $w.requestsReceived
        RequestsReleased = $w.requestsReleased
        PublisherStopConfirmed = $w.publisherStopConfirmed
        ResourceReleaseConfirmed = $w.resourceReleaseConfirmed
        OriginalApObservation = $windows.sampledApContinuity
        Events = @($w.events)
        Error = $w.error
    }
    Android = [pscustomobject]@{
        Source = (Get-Item -LiteralPath $AndroidReport).FullName
        Version = $android.appVersion
        TestStartedAtUtc = Convert-WanweiUtc $a.startedAtUtc
        TestFinishedAtUtc = Convert-WanweiUtc $a.finishedAtUtc
        ReportGeneratedAtUtc = Convert-WanweiUtc $android.generatedAtUtc
        State = $a.state
        FinishReason = $a.finishReason
        SelectedTargetInFreshList = $a.selectedTargetInFreshList
        ConnectRequestIssued = $a.connectRequestIssued
        ConnectRequestAccepted = $a.connectRequestAccepted
        ActualConnectionObserved = $a.actualConnectionObserved
        ActualP2pEndpointAvailable = $a.actualP2pEndpointAvailable
        EstablishedConnection = $a.establishedConnection
        EndpointGroup = $endpointGroup
        Cleanup = $a.cleanup
        RetryLocked = $a.retryLocked
        OriginalApObservation = $a.wifiObservation.originalWifiObservation
        BaselineWifiNetworkCount = $a.wifiObservation.baselineNetworkCount
        ComparableApSamples = $a.wifiObservation.comparableApSamples
        UnknownApSamples = $a.wifiObservation.unknownApSamples
        ChangedApSamples = $a.wifiObservation.changedApSamples
        Events = @($a.events)
    }
    Limits = @(
        'No session identity correlation is established by this summary; check timestamps and real endpoints.'
        'Source folder or file modification times are not test start times.'
        'API acceptance is not a connection; OS pairing is not application identity authentication.'
        'Matching endpoints are not a successful file transfer or proof of uninterrupted Internet.'
    )
}
