// Association lookup follows Microsoft's Windows-universal-samples WiFiDirect
// Scenario1_Advertiser.IsAepPairedAsync (MIT), with strict address validation,
// cancellation and a shared bounded lookup budget. See THIRD_PARTY_NOTICES.md.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace Wanwei;

internal static class AepAssociationPolicy
{
    internal static string? NormalizeDeviceAddress(string? address)
    {
        if (address is null || address.Length != 17 ||
            !Regex.IsMatch(address, "\\A(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}\\z", RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(address, "\\A(?:[0-9a-fA-F]{2}-){5}[0-9a-fA-F]{2}\\z", RegexOptions.CultureInvariant)) return null;
        string compact = address.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (compact is "000000000000" or "ffffffffffff" or "020000000000" || (Convert.ToByte(compact[..2], 16) & 1) != 0) return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => compact.Substring(i * 2, 2)));
    }
    internal static string? Selector(string? address)
    {
        string? normalized = NormalizeDeviceAddress(address);
        return normalized is null ? null : "System.Devices.Aep.DeviceAddress:=\"" + normalized + "\"";
    }
    internal static bool SkipFreshPairing(bool? requestIsPaired, OsAssociationEvidence evidence) =>
        requestIsPaired == true || evidence is { QueryStatus: "matched", DeviceAddressValidated: true, OsAssociationObserved: true };
    internal static string DecisionSource(bool? requestIsPaired, OsAssociationEvidence evidence) =>
        requestIsPaired == true ? "request_reports_system_paired" : SkipFreshPairing(requestIsPaired, evidence)
            ? "matching_existing_system_association" : "fresh_system_pairing_required";
    // The exact AQS already limits the system lookup to the selected request's
    // address. Where the returned device supplies an address too, require that
    // it independently agrees; an absent property retains the sample's behavior.
    internal static bool ReturnedDeviceMatches(string expected, bool addressPropertyPresent, object? address) =>
        !addressPropertyPresent || address is null || address is string text && NormalizeDeviceAddress(text) == expected;
}

internal static class AepAssociationProbe
{
    internal static async Task<OsAssociationEvidence> RunAsync(string currentRequestDeviceId, int maximumMs, CancellationToken cancellation)
    {
        int budgetMs = Math.Clamp(maximumMs, 0, 3000);
        if (budgetMs == 0) return new("no_remaining_negotiation_budget", null, false, false, 0);
        var clock = Stopwatch.StartNew(); bool validated = false, pendingLateRead = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budgetMs);
        OsAssociationEvidence Result(string status, bool? observed) => new(status, observed, validated, pendingLateRead, clock.ElapsedMilliseconds);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            string[] properties = ["System.Devices.Aep.DeviceAddress"];
            var information = await ObserveReadOnlyAsync(DeviceInformation.CreateFromIdAsync(currentRequestDeviceId, properties), deadline.Token,
                () => pendingLateRead = true).ConfigureAwait(false);
            if (information is null) return Result("device_information_unavailable", null);
            if (!information.Properties.TryGetValue(properties[0], out var raw) || raw is not string address)
                return Result("device_address_unavailable", null);
            string? normalized = AepAssociationPolicy.NormalizeDeviceAddress(address);
            string? selector = AepAssociationPolicy.Selector(address);
            if (normalized is null || selector is null) return Result("invalid_device_address", null);
            validated = true;
            deadline.Token.ThrowIfCancellationRequested();
            // Kind.Device is intentional: the request's AEP IsPaired snapshot
            // can be false although Windows still retains a paired device node.
            var devices = await ObserveReadOnlyAsync(DeviceInformation.FindAllAsync(selector, properties, DeviceInformationKind.Device), deadline.Token,
                () => pendingLateRead = true).ConfigureAwait(false);
            if (devices is null) return Result("device_collection_unavailable", null);
            foreach (var candidate in devices)
            {
                bool hasAddress = candidate.Properties.TryGetValue(properties[0], out var candidateAddress);
                if (AepAssociationPolicy.ReturnedDeviceMatches(normalized, hasAddress, candidateAddress)) return Result("matched", true);
            }
            return devices.Count == 0 ? Result("not_observed", false) : Result("returned_address_mismatch", null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Result("cancelled", null); }
        catch (OperationCanceledException) { return Result("timed_out", null); }
        catch { return Result("query_failed", null); }
    }
    private static async Task<T> ObserveReadOnlyAsync<T>(IAsyncOperation<T> operation, CancellationToken cancellation, Action lateReadPending)
    {
        Task<T> task;
        try { task = operation.AsTask(); }
        catch { try { operation.Cancel(); } catch { } throw; }
        try { return await task.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            try { operation.Cancel(); } catch { }
            if (!task.IsCompleted) lateReadPending();
            // These operations only read system metadata; no returned result may
            // change this session's decision after the shared deadline. Observe
            // and close a late operation instead of extending negotiation.
            _ = task.ContinueWith(t => { _ = t.Exception; try { operation.Close(); } catch { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
