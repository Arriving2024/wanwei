using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wanwei;

public sealed class RunPrivacy : IDisposable
{
    // A new, non-exported key per run prevents joining nearby devices across reports.
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public string Token(string category, string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(category + "\0" + value)));
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

public sealed record ApiCheck(string Name, string Status, string Detail);
public sealed record WlanAdapter(string AdapterToken, string Description, string State, string? ApToken, uint? QueryError);
public sealed record NetworkSample(long ElapsedMs, string Status, uint? Error, IReadOnlyList<WlanAdapter> Adapters);
public sealed record PeerEvidence(string Token, bool IsPaired, bool Present);
public sealed record ScanResult(string Outcome, bool InitialEnumerationCompleted, string FinalWatcherStatus,
    bool StopConfirmed, long DurationMs, IReadOnlyList<PeerEvidence> Peers, string? Error);
public sealed record AdvertisementStatusEvidence(long ElapsedMs, string Status, string Error, string Source);
public sealed record AdvertisementResult(string Outcome, bool StartAttempted, bool StartedObserved,
    bool NonLegacyConfigurationConfirmed, string FinalPublisherStatus, bool StopConfirmed, long DurationMs,
    int ConnectionRequestsReceived, int ConnectionRequestsReleased,
    IReadOnlyList<AdvertisementStatusEvidence> StatusEvents, string? Error)
{
    public string PeerVisibility { get; } = "not_verified";
    public string ConnectionPolicy { get; } = "release_without_pairing_or_accepting";
}

public sealed record PairingPrompt(string PeerToken, string Kind, string? Pin, int? RemainingStageMs = null);
public sealed record PairingDecision(bool Accept, string? Pin = null);
public sealed record PairingEvent(long ElapsedMs, string Stage, string Status);
public sealed record PairingPhaseEvidence(string Phase, long StartedElapsedMs, long DeadlineElapsedMs, long? EndedElapsedMs);
public sealed record PairingTimingEvidence(int WaitForRequestLimitMs, int NegotiationLimitMs,
    int ObservationLimitMs, int MaximumRunAfterPublisherStartedMs, long? PublisherStartedElapsedMs,
    long? OverallDeadlineElapsedMs, long? EndedElapsedMs, string? EndReason,
    IReadOnlyList<PairingPhaseEvidence> Phases)
{
    public string Clock { get; } = "monotonic_elapsed_ms_since_pairing_session_creation";
    public string LimitsMeaning { get; } = "startup_and_cleanup_have_separate_bounds;_extra_requests_and_repeated_connected_events_never_extend_deadlines";
}
public sealed record DirectEndpointEvidence(string LocalAddress, string LocalPort, string RemoteAddress, string RemotePort);
public sealed record OsAssociationEvidence(string QueryStatus, bool? OsAssociationObserved, bool DeviceAddressValidated,
    bool LateReadPendingAtReturn, long ElapsedMs)
{
    public string Source { get; } = "current_request_DeviceInformation_Id_to_Aep_DeviceAddress_to_KindDevice_exact_address_lookup";
    public string LateResultPolicy { get; } = "readonly_query_cancelled_and_late_result_ignored_never_changes_pairing_decision";
    public string ApplicationIdentity { get; } = "not_authenticated_by_system_association";
}
public sealed record PairingSessionResult(string Outcome, bool StartAttempted, bool StartedObserved,
    bool NonLegacyConfigurationConfirmed, string FinalPublisherStatus, bool PublisherStopConfirmed,
    bool ResourceReleaseConfirmed, long DurationMs, int RequestsReceived, int RequestsReleased,
    int ExtraRequestsRejected, string? PeerToken, bool? AlreadyPaired, string SystemPairingStatus,
    bool UserAcceptedRequest, bool ConnectedObserved, string FinalConnectionStatus,
    IReadOnlyList<DirectEndpointEvidence> EndpointPairs, IReadOnlyList<PairingEvent> Events, string? Error)
{
    public PairingTimingEvidence? Timing { get; init; }
    public bool? RequestIsPaired => AlreadyPaired;
    public OsAssociationEvidence? OsAssociationCheck { get; init; }
    public string EffectivePairingDecisionSource { get; init; } = "not_evaluated";
    public bool OperatorFixturePreauthorized { get; init; }
    public string AppPairingDecisionSource => OperatorFixturePreauthorized ? "explicit_cli_USB_fixed_fixture_lab" : "visible_UI_confirmation";
    public string ApplicationIdentity { get; } = "not_authenticated";
    public string EndpointSource { get; } = "WiFiDirectDevice.GetConnectionEndpointPairs";
    public string ReleaseMeaning { get; } = "this_app_owned_objects_released;_OS_disconnect_not_proven";
}

public sealed class LabReport
{
    public string Schema { get; } = "wanwei.windows.probe/1";
    public string AppVersion { get; } = "0.3.2";
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string Os { get; } = Environment.OSVersion.VersionString;
    public string PackageIdentity { get; set; } = "unknown";
    public string OperationOutcome { get; set; } = "completed";
    public List<ApiCheck> ApiChecks { get; } = [];
    public string DriverEvidence { get; set; } = "not_collected";
    public List<NetworkSample> NetworkSamples { get; } = [];
    public ScanResult? Discovery { get; set; }
    public AdvertisementResult? Advertisement { get; set; }
    public PairingSessionResult? PairingSession { get; set; }
    public string SampledApContinuity { get; set; } = "not_tested";
    public string Pairing => PairingSession?.SystemPairingStatus ?? "not_tested";
    public string DirectDataPath { get; } = "not_tested";
    public string Transfer { get; } = "separate_opt_in_P0_report";
    public string InternetContinuity { get; } = "not_tested";
    public string IosWindowsInterop { get; } = "unverified";
    public string[] Limits { get; } = [
        "API presence and driver flags do not prove hardware usability or interoperability.",
        "Discovery entries may be cached or unrelated devices; they are not verified Wanwei peers.",
        "Publisher Started is local OS status, not proof that any other device discovered this computer.",
        "One-second AP samples cannot exclude interruptions between samples, and do not test Internet continuity.",
        "System pairing does not authenticate a Wanwei app or establish permanent app trust.",
        "The OS may retain pairing, including after cancellation; this app never removes existing OS pairing.",
        "Endpoint pairs and a connected status are direct API evidence, not a successful data transfer.",
        "Releasing this app's connection object does not prove the OS or other applications disconnected.",
        "Only the opt-in P0 experiment opens a socket on a verified native Direct address; see its separate transfer report. No LAN fallback."
    ];
}

public static class ReportStore
{
    public static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public static string Serialize(LabReport report) => JsonSerializer.Serialize(report, Json);
    public static string Save(LabReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.GetFullPath(Path.Combine(directory, $"windows-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{report.RunId}.json"));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, report, Json);
        stream.Flush(true);
        return path;
    }
}

public static class Continuity
{
    public static string Evaluate(IReadOnlyList<NetworkSample> samples)
    {
        if (samples.Count < 2 || samples[0].Status != "ok") return "unknown";
        var baseline = samples[0].Adapters.Where(a => a.State == "connected").ToArray();
        if (baseline.Length == 0) return "unknown";
        bool incomplete = baseline.Any(a => a.ApToken is null);
        foreach (var sample in samples.Skip(1))
        {
            if (sample.Status != "ok") { incomplete = true; continue; }
            foreach (var original in baseline)
            {
                var current = sample.Adapters.FirstOrDefault(a => a.AdapterToken == original.AdapterToken);
                if (current is null || current.State != "connected") return "changed_or_missing";
                if (current.ApToken is null || original.ApToken is null) { incomplete = true; continue; }
                if (current.ApToken != original.ApToken) return "changed_or_missing";
            }
        }
        return incomplete ? "unknown" : "same_at_observed_samples";
    }
}
