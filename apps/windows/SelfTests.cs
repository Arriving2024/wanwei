using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;

namespace Wanwei;

internal static class SelfTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
        NetworkSample Sample(string? ap, string state = "connected", string status = "ok") =>
            new(0, status, null, [new("adapterA", "test", state, ap, ap is null ? 5u : null)]);
        Check("WLAN_INTERFACE_INFO layout", Marshal.SizeOf<NativeWlan.InterfaceInfo>() == 532);
        Check("WLAN_ASSOCIATION_ATTRIBUTES layout", Marshal.SizeOf<NativeWlan.Association>() == 68);
        Check("WLAN_CONNECTION_ATTRIBUTES layout", Marshal.SizeOf<NativeWlan.Connection>() == 604);
        Check("BSSID offset", Marshal.OffsetOf<NativeWlan.Association>(nameof(NativeWlan.Association.Bssid)).ToInt32() == 40);
        Check("netsh UTF8 on Chinese Windows", ProbeRunner.DecodeNetsh(Encoding.UTF8.GetBytes("无线：支持"), 936) == "无线：支持");
        Check("netsh OEM936 fallback", ProbeRunner.DecodeNetsh(Encoding.GetEncoding(936).GetBytes("无线：支持"), 936) == "无线：支持");
        Check("one sample is inconclusive", Continuity.Evaluate([Sample("ap")]) == "unknown");
        Check("same observed AP only", Continuity.Evaluate([Sample("ap"), Sample("ap")]) == "same_at_observed_samples");
        Check("AP changed", Continuity.Evaluate([Sample("ap"), Sample("other")]) == "changed_or_missing");
        Check("intermediate disconnect retained", Continuity.Evaluate([Sample("ap"), Sample(null, "disconnected"), Sample("ap")]) == "changed_or_missing");
        Check("permission denied never passes", Continuity.Evaluate([Sample(null), Sample(null)]) == "unknown");
        Check("sample failure never passes", Continuity.Evaluate([Sample("ap"), Sample("ap", status: "failed")]) == "unknown");
        Check("unknown cannot hide later disconnect", Continuity.Evaluate([Sample("ap"), Sample(null), Sample(null, "disconnected")]) == "changed_or_missing");
        Check("failed sample cannot hide later AP change", Continuity.Evaluate([Sample("ap"), Sample(null, status: "failed"), Sample("other")]) == "changed_or_missing");
        Check("missing adapter fails", Continuity.Evaluate([Sample("ap"), new(1, "ok", null, [])]) == "changed_or_missing");
        Check("no baseline WLAN never passes", Continuity.Evaluate([new(0, "ok", null, []), new(1, "ok", null, [])]) == "unknown");
        using var first = new RunPrivacy(); using var second = new RunPrivacy();
        Check("stable token within run", first.Token("ap", "secret") == first.Token("ap", "secret"));
        Check("unlinkable token across runs", first.Token("ap", "secret") != second.Token("ap", "secret"));
        Check("separate token domains", first.Token("ap", "same") != first.Token("peer", "same"));
        Check("token contains no raw value", !first.Token("ap", "private-network").Contains("private-network"));
        Check("valid scan option", Options.Parse(["--discover", "--seconds", "2"]).Seconds == 2);
        bool Reject(string[] args) { try { Options.Parse(args); return false; } catch (ArgumentException) { return true; } }
        Check("zero scan rejected", Reject(["--discover", "--seconds", "0"]));
        Check("unbounded scan rejected", Reject(["--discover", "--seconds", "999999"]));
        Check("conflicting modes rejected", Reject(["--probe", "--discover"]));
        Check("missing value rejected", Reject(["--out"]));
        Check("valid bounded advertise option", Options.Parse(["--advertise", "--seconds", "5"]).Mode == "advertise");
        Check("advertise and discovery cannot overlap", Reject(["--advertise", "--discover"]));
        Check("zero advertise rejected", Reject(["--advertise", "--seconds", "0"]));
        Check("advertise cancellation parsed", Options.Parse(["--advertise", "--cancel-after-ms", "1800"]).CancelAfterMs == 1800);
        Check("publisher Created after start is not stopped", !DirectAdvertisement.IsStopConfirmed(true, "Created"));
        Check("publisher Started is not stopped", !DirectAdvertisement.IsStopConfirmed(true, "Started"));
        Check("publisher untouched Created is safe", DirectAdvertisement.IsStopConfirmed(false, "Created"));
        Check("publisher Stopped is confirmed", DirectAdvertisement.IsStopConfirmed(true, "Stopped"));
        Check("publisher Aborted is inactive", DirectAdvertisement.IsStopConfirmed(true, "Aborted"));
        Check("publisher abort remains failure after cancel", DirectAdvertisement.FinalOutcome("cancelled", true, true, true, null) == "failed");
        Check("publisher stop timeout dominates cancel", DirectAdvertisement.FinalOutcome("cancelled", true, false, false, null) == "stop_unconfirmed");
        Check("publisher needs Started to complete", DirectAdvertisement.FinalOutcome("completed", false, false, true, null) == "failed");
        Check("publisher error cannot claim completed", DirectAdvertisement.FinalOutcome("completed", true, false, true, "ERROR") == "failed");
        Check("publisher bounded success remains local", DirectAdvertisement.FinalOutcome("completed", true, false, true, null) == "completed");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var result = DirectAdvertisement.RunAsync(5, cancelled.Token, null).GetAwaiter().GetResult();
            Check("cancel before advertise touches no publisher", result is { Outcome: "cancelled", StartAttempted: false, StartedObserved: false, StopConfirmed: true, FinalPublisherStatus: "not_created" });
        }
        var json = ReportStore.Serialize(new LabReport());
        Check("pairing mode smoke option is bounded and UI-based", Options.Parse(["--ui-pairing-smoke", "preview.png", "--seconds", "5"]).Mode == "ui-pairing-smoke");
        Check("pairing smoke cannot overlap advertise", Reject(["--ui-pairing-smoke", "preview.png", "--advertise"]));
        Check("pairing accepts Paired status", DirectPairing.IsPairSuccess("Paired"));
        Check("pairing existing OS pair is success only for OS phase", DirectPairing.IsPairSuccess("AlreadyPaired"));
        Check("pairing rejected OS status cannot connect", !DirectPairing.IsPairSuccess("RejectedByHandler"));
        Check("unsupported credential mode rejected", !DirectPairing.SupportedKind(DevicePairingKinds.ProvidePasswordCredential));
        Check("PIN match handled explicitly", DirectPairing.SupportedKind(DevicePairingKinds.ConfirmPinMatch));
        Check("4 and 8 digit PIN allowed", DirectPairing.ValidPin("1234") && DirectPairing.ValidPin("12345678"));
        Check("missing and malformed PIN rejected", !DirectPairing.ValidPin(null) && !DirectPairing.ValidPin("123456") && !DirectPairing.ValidPin("1234abcd"));
        Check("same-family distinct API IP endpoints retained", DirectPairing.ValidEndpoint(new("192.168.49.1", "", "192.168.49.2", "")));
        Check("loopback does not count as direct endpoint", !DirectPairing.ValidEndpoint(new("127.0.0.1", "", "192.168.49.2", "")));
        Check("missing or unspecified endpoint rejected", !DirectPairing.ValidEndpoint(new("", "", "192.168.49.2", "")) && !DirectPairing.ValidEndpoint(new("0.0.0.0", "", "192.168.49.2", "")));
        Check("identical endpoint rejected", !DirectPairing.ValidEndpoint(new("192.168.49.1", "", "192.168.49.1", "")));
        Check("pending native connection becomes timeout", DirectPairing.FinalOutcome("connection_pending", false, true, true, null) == "stage_timeout");
        Check("resource release uncertainty dominates cancel", DirectPairing.FinalOutcome("connected_observed", true, true, false, null) == "release_unconfirmed");
        Check("publisher stop uncertainty blocks completion", DirectPairing.FinalOutcome("connected_observed", false, false, true, null) == "release_unconfirmed");
        Check("no-request expiry is not a connection claim", DirectPairing.FinalOutcome("waiting_expired", false, true, true, null) == "waiting_expired");
        Check("successful connection with error is not success", DirectPairing.FinalOutcome("connected_observed", false, true, true, "ERROR") == "failed");
        var lateRequest = new PairingDeadlines(60);
        Check("phase deadline starts only once", lateRequest.Start(0) && lateRequest.DeadlineElapsedMs == 60_000);
        Check("first request receives its own full negotiation budget", lateRequest.TrySelectRequest(59_000) &&
            lateRequest.Phase == "negotiating" && lateRequest.DeadlineElapsedMs == 104_000);
        Check("old wait timer cannot expire negotiation", !lateRequest.Expire(60_000) && lateRequest.Phase == "negotiating");
        Check("late publisher Started cannot reset request deadline", !lateRequest.Start(60_100) &&
            lateRequest.DeadlineElapsedMs == 104_000);
        Check("additional requests do not renew negotiation", !lateRequest.TrySelectRequest(75_000) &&
            lateRequest.DeadlineElapsedMs == 104_000);
        Check("connection before negotiation cutoff begins independent observation", lateRequest.TryObserveConnection(103_999) &&
            lateRequest.Phase == "observing" && lateRequest.DeadlineElapsedMs == 123_999);
        Check("old negotiation timer cannot expire observation", !lateRequest.Expire(104_000) && lateRequest.Phase == "observing");
        Check("repeated Connected never renews observation", !lateRequest.TryObserveConnection(120_000) &&
            lateRequest.DeadlineElapsedMs == 123_999);
        Check("extra request during observation never renews it", !lateRequest.TrySelectRequest(120_001) &&
            lateRequest.DeadlineElapsedMs == 123_999);
        Check("observation expires at its exact deadline", lateRequest.Expire(123_999) &&
            lateRequest.EndReason == "observation_window_completed");
        Check("expired phase cannot be revived by any late event", !lateRequest.Start(124_000) &&
            !lateRequest.TrySelectRequest(124_000) && !lateRequest.TryObserveConnection(124_000));
        Check("late cleanup cannot overwrite phase expiry reason", !lateRequest.Stop(125_000, "cleanup") &&
            lateRequest.EndReason == "observation_window_completed");
        var timing = lateRequest.Snapshot();
        Check("report retains all absolute phase deadlines", timing.Phases.Count == 3 &&
            timing.Phases[0] is { Phase: "waiting_for_request", StartedElapsedMs: 0, DeadlineElapsedMs: 60_000, EndedElapsedMs: 59_000 } &&
            timing.Phases[1] is { Phase: "negotiating", StartedElapsedMs: 59_000, DeadlineElapsedMs: 104_000, EndedElapsedMs: 103_999 } &&
            timing.Phases[2] is { Phase: "observing", StartedElapsedMs: 103_999, DeadlineElapsedMs: 123_999, EndedElapsedMs: 123_999 });
        Check("overall deadline is anchored to publisher start", timing.MaximumRunAfterPublisherStartedMs == 125_000 &&
            timing.PublisherStartedElapsedMs == 0 && timing.OverallDeadlineElapsedMs == 125_000 && timing.EndedElapsedMs == 123_999);
        var waitBoundary = new PairingDeadlines(60);
        waitBoundary.Start(4_000);
        Check("request at exact wait cutoff is rejected before timer dispatch", !waitBoundary.TrySelectRequest(64_000));
        Check("no-request expiry has an explicit reason", waitBoundary.Expire(64_000) && waitBoundary.EndReason == "wait_for_request_timeout");
        var negotiationBoundary = new PairingDeadlines(60);
        negotiationBoundary.Start(0);
        negotiationBoundary.TrySelectRequest(10_000);
        Check("connection at exact negotiation cutoff cannot start observation", !negotiationBoundary.TryObserveConnection(55_000));
        Check("late native connection leaves negotiation as timed out", negotiationBoundary.Expire(55_000) &&
            negotiationBoundary.EndReason == "negotiation_timeout" && !negotiationBoundary.TryObserveConnection(55_001));
        var requestBeforeStartedCallback = new PairingDeadlines(60);
        // Integration first observes publisher.Status==Started while accepting the request;
        // the queued Started event is then delivered later.
        requestBeforeStartedCallback.Start(12);
        requestBeforeStartedCallback.TrySelectRequest(13);
        Check("request-before-Started-callback retains negotiation deadline", !requestBeforeStartedCallback.Start(20) &&
            requestBeforeStartedCallback.Phase == "negotiating" && requestBeforeStartedCallback.DeadlineElapsedMs == 45_013);
        var cancelledBeforeStart = new PairingDeadlines(60);
        cancelledBeforeStart.Stop(0, "cancelled_by_user");
        Check("pre-cancel blocks publisher request and connection transitions", !cancelledBeforeStart.Start(1) &&
            !cancelledBeforeStart.TrySelectRequest(2) && !cancelledBeforeStart.TryObserveConnection(3));
        foreach (var phase in new[] { "waiting_for_request", "negotiating", "observing" })
        {
            var cancelledPhase = new PairingDeadlines(60);
            cancelledPhase.Start(0);
            if (phase != "waiting_for_request") cancelledPhase.TrySelectRequest(10);
            if (phase == "observing") cancelledPhase.TryObserveConnection(20);
            Check("cancel terminates " + phase, cancelledPhase.Stop(30, "cancelled_by_user") &&
                !cancelledPhase.Running && cancelledPhase.EndReason == "cancelled_by_user");
            Check("late events cannot undo cancellation in " + phase, !cancelledPhase.Start(40) &&
                !cancelledPhase.TrySelectRequest(40) && !cancelledPhase.TryObserveConnection(40) &&
                !cancelledPhase.Expire(200_000) && cancelledPhase.EndReason == "cancelled_by_user");
        }
        foreach (int waitSeconds in new[] { 1, 5, 60 })
        {
            var maximum = new PairingDeadlines(waitSeconds);
            const long startTime = 5_000;
            maximum.Start(startTime);
            maximum.TrySelectRequest(startTime + waitSeconds * 1_000 - 1);
            maximum.TryObserveConnection(maximum.DeadlineElapsedMs!.Value - 1);
            Check($"{waitSeconds}s waiting option retains a bounded total", maximum.DeadlineElapsedMs <= maximum.OverallDeadlineElapsedMs &&
                maximum.OverallDeadlineElapsedMs == startTime + waitSeconds * 1_000 + 45_000 + 20_000);
        }
        Check("wireless session first owner acquires", WirelessSessionGate.TryEnter());
        Check("second wireless session cannot overlap", !WirelessSessionGate.TryEnter());
        WirelessSessionGate.Release(false);
        Check("uncertain release prohibits reentry", WirelessSessionGate.RestartRequired && !WirelessSessionGate.TryEnter());
        WirelessSessionGate.Release(true);
        Check("confirmed release permits new session", WirelessSessionGate.TryEnter());
        WirelessSessionGate.Release(true);
        using (var cancelledPairing = new CancellationTokenSource())
        {
            cancelledPairing.Cancel();
            var result = DirectPairing.RunAsync(5, first, new RejectingUi(), cancelledPairing.Token, null).GetAwaiter().GetResult();
            Check("cancel before pairing touches no publisher or connection", result is { Outcome: "cancelled", StartAttempted: false,
                ConnectedObserved: false, PublisherStopConfirmed: true, ResourceReleaseConfirmed: true, FinalPublisherStatus: "not_created" });
            Check("pre-cancel pairing releases common session guard", !WirelessSessionGate.RestartRequired);
        }
        Check("report does not claim app authentication", json.Contains("System pairing does not authenticate"));
        Check("report cannot claim transfer", json.Contains("\"transfer\": \"not_implemented\""));
        Check("report cannot claim internet continuity", json.Contains("\"internetContinuity\": \"not_tested\""));
        Check("report cannot claim iOS interoperability", json.Contains("\"iosWindowsInterop\": \"unverified\""));
        Console.WriteLine($"{passed} local logic checks passed. No radio interoperability was tested by these checks.");
        return 0;
    }

    private sealed class RejectingUi : IPairingUi
    {
        public Task<PairingDecision> ConfirmAsync(PairingPrompt prompt, CancellationToken cancellation) => Task.FromResult(new PairingDecision(false));
        public Task<IAsyncOperation<WiFiDirectDevice>> BeginConnectionAsync(string id, CancellationToken cancellation) => throw new InvalidOperationException("A rejected request must never open a connection.");
    }
}
