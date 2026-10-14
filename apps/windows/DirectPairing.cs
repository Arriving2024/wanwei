// Publisher/listener, custom PairAsync settings and UI-thread FromIdAsync adapted
// from Microsoft's Windows-universal-samples WiFiDirect C# example (MIT).
// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT License.
// See THIRD_PARTY_NOTICES.md and references/upstream/windows-wifidirect/LICENSE.
using System.Diagnostics;
using System.Net;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;

namespace Wanwei;

public interface IPairingUi
{
    Task<PairingDecision> ConfirmAsync(PairingPrompt prompt, CancellationToken cancellation);
    // The implementation must begin FromIdAsync on its live STA UI dispatcher.
    Task<IAsyncOperation<WiFiDirectDevice>> BeginConnectionAsync(string deviceId, CancellationToken cancellation);
}

internal sealed class DirectPairing
{
    private static readonly List<object> uncertainSessions = [];
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<PairingEvent> events = [];
    private readonly List<Task> deferrals = [];
    private readonly RunPrivacy privacy;
    private readonly IPairingUi ui;
    private readonly Action<string>? progress;
    private readonly CancellationTokenSource lifetime;
    private readonly PairingDeadlines deadlines;
    private readonly System.Threading.Timer deadlineTimer;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource publisherEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource sessionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WiFiDirectAdvertisementPublisher? publisher;
    private WiFiDirectConnectionListener? listener;
    private WiFiDirectDevice? device;
    private Task? requestTask;
    private bool active = true, startAttempted, startedObserved, configured, stopConfirmed = true;
    private bool releaseConfirmed = true, selected, userAccepted, connected, statusSubscribed, requestSubscribed;
    private int received, released, extraRejected;
    private string? peerToken, error;
    private bool? alreadyPaired;
    private string outcome = "waiting_expired", systemPairingStatus = "not_attempted", connectionStatus = "not_created";
    private DirectEndpointEvidence[] endpoints = [];

    private DirectPairing(int waitSeconds, RunPrivacy privacy, IPairingUi ui, CancellationToken cancellation, Action<string>? progress)
    {
        this.privacy = privacy; this.ui = ui; this.progress = progress;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadlines = new(waitSeconds);
        deadlineTimer = new(_ => DeadlineElapsed(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal static async Task<PairingSessionResult> RunAsync(int seconds, RunPrivacy privacy, IPairingUi ui,
        CancellationToken cancellation, Action<string>? progress)
    {
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        ArgumentNullException.ThrowIfNull(ui);
        if (!WirelessSessionGate.TryEnter()) throw new InvalidOperationException("Wireless session active or release unconfirmed; restart required.");
        var session = new DirectPairing(seconds, privacy, ui, cancellation, progress);
        return await session.RunSessionAsync(seconds, cancellation).ConfigureAwait(false);
    }

    private void Record(string stage, string status)
    {
        lock (gate) if (active && events.Count < 128) events.Add(new(clock.ElapsedMilliseconds, stage, status));
    }
    private void Fail(Exception ex)
    {
        lock (gate) { error ??= DirectDiscovery.ErrorCode(ex); outcome = "failed"; sessionEnded.TrySetResult(); }
    }
    private void PublisherStarted()
    {
        lock (gate)
        {
            startedObserved = true;
            if (active && !lifetime.IsCancellationRequested && deadlines.Start(clock.ElapsedMilliseconds))
            {
                RecordDeadlineLocked();
                progress?.Invoke($"等待手机发起连接，最多 {deadlines.WaitLimitMs / 1_000} 秒。收到首请求后，两个确认和系统协商另计 45 秒；连接成功后观察 20 秒。");
            }
            // A request can arrive before the Started callback is dispatched. Start() is
            // one-shot: the later callback cannot move negotiation back to waiting.
            started.TrySetResult();
        }
    }
    private void RecordDeadlineLocked()
    {
        Record("deadline", $"{deadlines.Phase}:deadline_elapsed_ms={deadlines.DeadlineElapsedMs}");
        ArmDeadlineLocked();
    }
    private void ArmDeadlineLocked()
    {
        if (deadlines.Running)
            deadlineTimer.Change(TimeSpan.FromMilliseconds(Math.Max(1, deadlines.DeadlineElapsedMs!.Value - clock.ElapsedMilliseconds)), Timeout.InfiniteTimeSpan);
    }
    private bool ExpireDeadlineLocked()
    {
        if (!deadlines.Expire(clock.ElapsedMilliseconds)) return false;
        Record("deadline", deadlines.EndReason!);
        if (deadlines.EndReason == "negotiation_timeout") outcome = "stage_timeout";
        sessionEnded.TrySetResult();
        return true;
    }
    private void DeadlineElapsed()
    {
        lock (gate)
        {
            if (!active || lifetime.IsCancellationRequested || !deadlines.Running) return;
            // A queued timer for an earlier phase can run after a transition. Always
            // consult the current absolute deadline rather than expiring that old phase.
            if (!ExpireDeadlineLocked()) ArmDeadlineLocked();
        }
    }
    private PairingPrompt Prompt(string kind, string? pin)
    {
        lock (gate) return new(peerToken ?? "unknown", kind, pin,
            (int)Math.Max(0, (deadlines.DeadlineElapsedMs ?? clock.ElapsedMilliseconds) - clock.ElapsedMilliseconds));
    }
    private bool MayNegotiateLocked()
    {
        if (!active || lifetime.IsCancellationRequested || sessionEnded.Task.IsCompleted) return false;
        if (ExpireDeadlineLocked()) return false;
        return deadlines.Phase == "negotiating";
    }
    private void EndRejectedPrompt(string status)
    {
        lock (gate)
        {
            outcome = "rejected";
            Record("pairing_prompt", status);
            sessionEnded.TrySetResult();
        }
    }
    private void PublisherChanged(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        Record("publisher", args.Status + ":" + args.Error);
        if (args.Error != WiFiDirectError.Success) lock (gate) error ??= "PUBLISHER_" + args.Error;
        if (args.Status == WiFiDirectAdvertisementPublisherStatus.Started) PublisherStarted();
        if (args.Status is WiFiDirectAdvertisementPublisherStatus.Aborted or WiFiDirectAdvertisementPublisherStatus.Stopped)
        {
            if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
                lock (gate) { error ??= "PUBLISHER_ABORTED_" + args.Error; outcome = "failed"; }
            publisherEnded.TrySetResult();
        }
    }

    // GetConnectionRequest has no deferral API: take ownership immediately.
    // DevicePairingRequested below does have a deferral, completed on all paths.
    private void ConnectionRequested(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
    {
        WiFiDirectConnectionRequest? request = null;
        try
        {
            request = args.GetConnectionRequest();
            lock (gate)
            {
                if (active) received++;
                if (deadlines.Phase == "not_started" && publisher?.Status == WiFiDirectAdvertisementPublisherStatus.Started)
                    PublisherStarted();
                if (!active || lifetime.IsCancellationRequested || sessionEnded.Task.IsCompleted || selected ||
                    !deadlines.TrySelectRequest(clock.ElapsedMilliseconds))
                {
                    request.Dispose();
                    if (active) { released++; extraRejected++; Record("request", "extra_or_late_rejected"); }
                    ExpireDeadlineLocked();
                    return;
                }
                selected = true;
                outcome = "request_pending";
                RecordDeadlineLocked();
                progress?.Invoke("已收到首个连接请求。现在两次确认、系统配对及建立连接共用 45 秒；额外请求不会延长。请留意接下来的系统配对确认。");
                requestTask = HandleRequestAsync(request);
            }
        }
        catch (Exception ex)
        {
            try { request?.Dispose(); } catch { releaseConfirmed = false; }
            Fail(ex);
        }
    }

    private async Task HandleRequestAsync(WiFiDirectConnectionRequest request)
    {
        DeviceInformationCustomPairing? custom = null;
        bool pairingSubscribed = false;
        try
        {
            var information = request.DeviceInformation;
            peerToken = privacy.Token("pairing_peer", information.Id);
            alreadyPaired = information.Pairing?.IsPaired;
            Record("request", "user_confirmation_required");
            var decision = await ui.ConfirmAsync(Prompt("ConnectionRequest", null), lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!decision.Accept) { outcome = "rejected"; Record("request", "user_rejected"); return; }
            lock (gate) if (!MayNegotiateLocked()) return;
            userAccepted = true;
            Record("request", "user_accepted");
            if (alreadyPaired == true) systemPairingStatus = "AlreadyPaired";
            else
            {
                if (information.Pairing is null) { error = "PAIRING_INTERFACE_MISSING"; outcome = "failed"; return; }
                custom = information.Pairing.Custom;
                custom.PairingRequested += PairingRequested;
                pairingSubscribed = true;
                var settings = new WiFiDirectConnectionParameters {
                    PreferredPairingProcedure = WiFiDirectPairingProcedure.GroupOwnerNegotiation
                };
                var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin |
                    DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch;
                IAsyncOperation<DevicePairingResult> pairingOperation;
                lock (gate)
                {
                    if (!MayNegotiateLocked()) return;
                    Record("pairing", "PairAsync_started");
                    outcome = "pairing_pending";
                    systemPairingStatus = "pending";
                    pairingOperation = custom.PairAsync(kinds, DevicePairingProtectionLevel.Default, settings);
                }
                var result = await ObserveAsync(pairingOperation, lifetime.Token).ConfigureAwait(false);
                systemPairingStatus = result.Status.ToString();
                Record("pairing", systemPairingStatus);
                lock (gate) if (!MayNegotiateLocked()) return;
                if (!IsPairSuccess(systemPairingStatus)) { outcome = "pairing_failed"; return; }
            }
            lifetime.Token.ThrowIfCancellationRequested();
            lock (gate) if (!MayNegotiateLocked()) return;
            outcome = "connection_pending";
            Record("connection", "FromIdAsync_requested_on_UI_thread");
            var operation = await ui.BeginConnectionAsync(information.Id, lifetime.Token).ConfigureAwait(false);
            var createdDevice = await ObserveAsync(operation, lifetime.Token, late => late?.Dispose()).ConfigureAwait(false);
            if (createdDevice is null) { error = "FROM_ID_RETURNED_NULL"; outcome = "connection_failed"; return; }
            bool adopted;
            lock (gate)
            {
                adopted = MayNegotiateLocked();
                if (adopted) device = createdDevice;
            }
            if (!adopted)
            {
                // A completed WinRT task may return even after token cancellation.
                // Never hand a late object to a teardown that already inspected device.
                try { createdDevice.Dispose(); Record("connection", "late_device_disposed_before_adoption"); }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
                return;
            }
            lifetime.Token.ThrowIfCancellationRequested();
            createdDevice.ConnectionStatusChanged += ConnectionChanged;
            CaptureConnection(createdDevice);
            if (sessionEnded.Task.IsCompleted || lifetime.IsCancellationRequested) return;
            if (createdDevice.ConnectionStatus != WiFiDirectConnectionStatus.Connected)
            {
                error = "DEVICE_NOT_CONNECTED"; outcome = "connection_failed"; return;
            }
            connected = true;
            outcome = endpoints.Length == 0 ? "connected_without_endpoints" : "connected_observed";
            progress?.Invoke(endpoints.Length == 0
                ? "系统报告 Direct 已连接，实际端点尚未取得；此版本不传文件。"
                : "系统报告 Direct 已连接并取得实际端点。已进入独立 20 秒观察；此版本不传文件。");
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { Record("request", "cancel_or_deadline"); }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            if (custom is not null && pairingSubscribed)
            {
                try { custom.PairingRequested -= PairingRequested; }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
            }
            try { request.Dispose(); lock (gate) if (active) released++; }
            catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
            // Rejection, native failure, or disconnection can finish immediately;
            // there is no reason to consume the remainder of the current phase.
            sessionEnded.TrySetResult();
        }
    }

    private void PairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
    {
        Deferral deferral;
        try { deferral = args.GetDeferral(); }
        catch (Exception ex) { Fail(ex); return; }
        lock (gate) deferrals.Add(RespondAsync());
        async Task RespondAsync()
        {
            try
            {
                if (lifetime.IsCancellationRequested || !active) return;
                Record("pairing_prompt", args.PairingKind.ToString());
                if (!SupportedKind(args.PairingKind)) { EndRejectedPrompt("unsupported_rejected"); return; }
                if (args.PairingKind is DevicePairingKinds.DisplayPin or DevicePairingKinds.ConfirmPinMatch &&
                    string.IsNullOrWhiteSpace(args.Pin)) { EndRejectedPrompt("missing_pin_rejected"); return; }
                var decision = await ui.ConfirmAsync(Prompt(args.PairingKind.ToString(), args.Pin), lifetime.Token).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                if (!decision.Accept) { EndRejectedPrompt("user_rejected"); return; }
                lock (gate)
                {
                    if (!MayNegotiateLocked()) return;
                    if (args.PairingKind == DevicePairingKinds.ProvidePin)
                    {
                        if (!ValidPin(decision.Pin)) { EndRejectedPrompt("invalid_pin_rejected"); return; }
                        args.Accept(decision.Pin!);
                    }
                    else args.Accept();
                    Record("pairing_prompt", "user_accepted");
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { Record("pairing_prompt", "cancel_or_deadline"); }
            catch (Exception ex) { Fail(ex); }
            finally
            {
                try { deferral.Complete(); }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
            }
        }
    }

    private void ConnectionChanged(WiFiDirectDevice sender, object args)
    {
        try { CaptureConnection(sender); }
        catch (Exception ex) { Fail(ex); }
    }
    private void CaptureConnection(WiFiDirectDevice sender)
    {
        lock (gate)
        {
            if (!active) return;
            connectionStatus = sender.ConnectionStatus.ToString();
            Record("connection_status", connectionStatus);
            if (sender.ConnectionStatus == WiFiDirectConnectionStatus.Connected)
            {
                if (!connected)
                {
                    if (lifetime.IsCancellationRequested || sessionEnded.Task.IsCompleted ||
                        !deadlines.TryObserveConnection(clock.ElapsedMilliseconds))
                    {
                        Record("connection", "late_connected_not_retained");
                        ExpireDeadlineLocked();
                        sessionEnded.TrySetResult();
                        return;
                    }
                    RecordDeadlineLocked();
                    progress?.Invoke("实际连接已观察到，进入独立 20 秒观察。重复连接通知不会延长本阶段。");
                }
                connected = true;
                endpoints = sender.GetConnectionEndpointPairs().Select(p => new DirectEndpointEvidence(
                    p.LocalHostName?.CanonicalName ?? "", p.LocalServiceName ?? "",
                    p.RemoteHostName?.CanonicalName ?? "", p.RemoteServiceName ?? ""))
                    .Where(ValidEndpoint).Take(16).ToArray();
                Record("endpoints", endpoints.Length.ToString());
            }
            else if (connected) { outcome = "disconnected_observed"; progress?.Invoke("系统报告本次 Direct 连接已断开。"); sessionEnded.TrySetResult(); }
        }
    }

    // Do not rely on AsTask(token) hiding a late native result. Cancel the actual
    // WinRT operation, wait for settlement, and dispose any late connection.
    private async Task<T> ObserveAsync<T>(IAsyncOperation<T> operation, CancellationToken cancellation, Action<T>? releaseLate = null)
    {
        Task<T> task;
        try { task = operation.AsTask(); }
        catch
        {
            // Projection setup itself failed; the native operation still exists.
            try { operation.Cancel(); } catch { }
            releaseConfirmed = false;
            lock (uncertainSessions) uncertainSessions.Add(operation);
            throw;
        }
        try { return await task.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            try { operation.Cancel(); } catch { releaseConfirmed = false; }
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) == task)
            {
                try { releaseLate?.Invoke(await task.ConfigureAwait(false)); }
                catch (OperationCanceledException) { }
                catch (Exception) when (task.IsFaulted) { }
                catch { releaseConfirmed = false; }
            }
            else
            {
                releaseConfirmed = false;
                error ??= "NATIVE_OPERATION_CANCEL_UNCONFIRMED";
                lock (uncertainSessions) uncertainSessions.Add(operation);
                _ = task.ContinueWith(t => {
                    if (t.IsCompletedSuccessfully) { try { releaseLate?.Invoke(t.Result); } catch { } }
                    else _ = t.Exception;
                }, TaskScheduler.Default);
            }
            throw;
        }
    }

    private async Task<PairingSessionResult> RunSessionAsync(int seconds, CancellationToken cancellation)
    {
        string finalPublisher = "not_created";
        try
        {
            cancellation.ThrowIfCancellationRequested();
            publisher = new();
            publisher.Advertisement.LegacySettings.IsEnabled = false;
            publisher.Advertisement.IsAutonomousGroupOwnerEnabled = false;
            publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            configured = !publisher.Advertisement.LegacySettings.IsEnabled && !publisher.Advertisement.IsAutonomousGroupOwnerEnabled;
            if (!configured) throw new InvalidOperationException("Non-legacy configuration not retained.");
            publisher.StatusChanged += PublisherChanged; statusSubscribed = true;
            listener = new(); listener.ConnectionRequested += ConnectionRequested; requestSubscribed = true;
            cancellation.ThrowIfCancellationRequested();
            startAttempted = true; stopConfirmed = false;
            publisher.Start();
            if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Started) PublisherStarted();
            using var startDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            startDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await Task.WhenAny(started.Task, publisherEnded.Task, sessionEnded.Task).WaitAsync(startDeadline.Token).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (publisherEnded.Task.IsCompleted) { outcome = "failed"; error ??= "PUBLISHER_ENDED_DURING_START"; }
            else if (!sessionEnded.Task.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token), publisherEnded.Task, sessionEnded.Task).ConfigureAwait(false);
                if (publisherEnded.Task.IsCompleted && !lifetime.IsCancellationRequested) { outcome = "failed"; error ??= "PUBLISHER_ENDED_UNEXPECTEDLY"; }
            }
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) { outcome = "failed"; error ??= "PUBLISHER_START_TIMEOUT"; }
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            lock (gate)
            {
                deadlines.Stop(clock.ElapsedMilliseconds, cancellation.IsCancellationRequested ? "cancelled_by_user" : outcome);
                deadlineTimer.Dispose();
            }
            await lifetime.CancelAsync().ConfigureAwait(false);
            // Mark closed before resource teardown: queued requests are declined.
            Task? current;
            lock (gate) current = requestTask;
            if (current is not null && await Task.WhenAny(current, Task.Delay(TimeSpan.FromSeconds(6))).ConfigureAwait(false) != current)
            { releaseConfirmed = false; error ??= "REQUEST_CLEANUP_TIMEOUT"; }
            Task[] prompts; lock (gate) prompts = deferrals.ToArray();
            var promptsDone = Task.WhenAll(prompts);
            if (await Task.WhenAny(promptsDone, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != promptsDone)
            { releaseConfirmed = false; error ??= "PAIRING_DEFERRAL_CLEANUP_TIMEOUT"; }
            if (device is not null)
            {
                // Diagnostic reads must never prevent independent release attempts.
                try { CaptureConnection(device); }
                catch (Exception ex) { Fail(ex); }
                try { device.ConnectionStatusChanged -= ConnectionChanged; }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
                try { device.Dispose(); Record("connection", "owned_device_disposed"); }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
            }
            if (publisher is not null)
            {
                try
                {
                    if (startAttempted && !DirectAdvertisement.IsStopConfirmed(true, publisher.Status.ToString())) publisher.Stop();
                    if (startAttempted && !DirectAdvertisement.IsStopConfirmed(true, publisher.Status.ToString()))
                        await Task.WhenAny(publisherEnded.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    finalPublisher = publisher.Status.ToString();
                    stopConfirmed = DirectAdvertisement.IsStopConfirmed(startAttempted, finalPublisher);
                    Record("publisher_final", finalPublisher);
                }
                catch (Exception ex) { stopConfirmed = !startAttempted; Fail(ex); }
            }
            lock (gate) active = false;
            if (stopConfirmed && releaseConfirmed)
            {
                try { if (listener is not null && requestSubscribed) listener.ConnectionRequested -= ConnectionRequested; }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
                try { if (publisher is not null && statusSubscribed) publisher.StatusChanged -= PublisherChanged; }
                catch (Exception ex) { releaseConfirmed = false; Fail(ex); }
            }
            if (stopConfirmed && releaseConfirmed) lifetime.Dispose();
            else lock (uncertainSessions) uncertainSessions.Add(this);
            WirelessSessionGate.Release(stopConfirmed && releaseConfirmed);
        }
        lock (gate)
        {
            outcome = FinalOutcome(outcome, cancellation.IsCancellationRequested, stopConfirmed, releaseConfirmed, error);
            return new(outcome, startAttempted, startedObserved, configured, finalPublisher, stopConfirmed,
                releaseConfirmed, clock.ElapsedMilliseconds, received, released, extraRejected, peerToken,
                alreadyPaired, systemPairingStatus, userAccepted, connected, connectionStatus, endpoints, events.ToArray(), error)
                { Timing = deadlines.Snapshot() };
        }
    }

    internal static bool IsPairSuccess(string status) => status is "Paired" or "AlreadyPaired";
    internal static bool SupportedKind(DevicePairingKinds kind) => kind is DevicePairingKinds.ConfirmOnly or
        DevicePairingKinds.DisplayPin or DevicePairingKinds.ProvidePin or DevicePairingKinds.ConfirmPinMatch;
    internal static bool ValidPin(string? pin) => pin is { Length: 4 or 8 } && pin.All(char.IsAsciiDigit);
    internal static bool ValidEndpoint(DirectEndpointEvidence endpoint) =>
        IPAddress.TryParse(endpoint.LocalAddress, out var local) && IPAddress.TryParse(endpoint.RemoteAddress, out var remote) &&
        local.AddressFamily == remote.AddressFamily && !local.Equals(remote) &&
        !IPAddress.IsLoopback(local) && !IPAddress.IsLoopback(remote) && !local.Equals(IPAddress.Any) && !remote.Equals(IPAddress.Any) &&
        !local.Equals(IPAddress.IPv6Any) && !remote.Equals(IPAddress.IPv6Any);
    internal static string FinalOutcome(string outcome, bool cancelled, bool stopped, bool released, string? error) =>
        !stopped || !released ? "release_unconfirmed" : error is not null ? "failed" : cancelled ? "cancelled" :
        outcome is "request_pending" or "pairing_pending" or "connection_pending" ? "stage_timeout" : outcome;
}
