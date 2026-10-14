// Publisher/listener setup and declining requests adapted from Microsoft's
// Windows-universal-samples, Samples/WiFiDirect/cs/Scenario1_Advertiser.xaml.cs.
// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT License.
// See THIRD_PARTY_NOTICES.md and references/upstream/windows-wifidirect/LICENSE.
using System.Diagnostics;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;

namespace Wanwei;

internal static class DirectAdvertisement
{
    // Retain the rejecting listener until process exit if the OS never confirms stop.
    private static (WiFiDirectAdvertisementPublisher, WiFiDirectConnectionListener)? uncertainSession;
    internal static bool RestartRequired => WirelessSessionGate.RestartRequired;

    public static async Task<AdvertisementResult> RunAsync(int seconds, CancellationToken cancellation,
        Action<string>? progress)
    {
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!WirelessSessionGate.TryEnter())
            throw new InvalidOperationException("Advertisement already active or stop unconfirmed; restart required.");
        var clock = Stopwatch.StartNew();
        var gate = new object();
        var events = new List<AdvertisementStatusEvidence>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WiFiDirectAdvertisementPublisher? publisher = null;
        WiFiDirectConnectionListener? listener = null;
        bool acceptingEvents = true, startAttempted = false, startedObserved = false, abortedObserved = false;
        bool stopConfirmed = true, configured = false, statusSubscribed = false, listenerSubscribed = false;
        int received = 0, released = 0;
        string outcome = "failed", finalStatus = "not_created";
        string? error = null;

        void Record(WiFiDirectAdvertisementPublisherStatus status, WiFiDirectError nativeError, string source)
        {
            lock (gate)
            {
                if (!acceptingEvents) return;
                if (events.Count < 32) events.Add(new(clock.ElapsedMilliseconds, status.ToString(), nativeError.ToString(), source));
                if (nativeError != WiFiDirectError.Success) error ??= "WIFI_DIRECT_" + nativeError;
                if (status == WiFiDirectAdvertisementPublisherStatus.Started)
                {
                    startedObserved = true;
                    started.TrySetResult();
                }
                if (status == WiFiDirectAdvertisementPublisherStatus.Aborted)
                {
                    abortedObserved = true;
                    error ??= "PUBLISHER_ABORTED";
                }
                if (status is WiFiDirectAdvertisementPublisherStatus.Stopped or WiFiDirectAdvertisementPublisherStatus.Aborted)
                    ended.TrySetResult();
            }
        }
        TypedEventHandler<WiFiDirectAdvertisementPublisher, WiFiDirectAdvertisementPublisherStatusChangedEventArgs> changed =
            (_, args) => Record(args.Status, args.Error, "event");
        TypedEventHandler<WiFiDirectConnectionListener, WiFiDirectConnectionRequestedEventArgs> requested = (_, args) =>
        {
            // Never read DeviceInformation, pair, accept, or open a connection.
            // Also reject a queued request after reporting has ended.
            bool didRelease = false;
            string? requestError = null;
            try
            {
                using (args.GetConnectionRequest()) { }
                didRelease = true;
            }
            catch (Exception ex) { requestError = DirectDiscovery.ErrorCode(ex); }
            lock (gate)
            {
                if (!acceptingEvents) return;
                received++;
                if (didRelease) released++;
                error ??= requestError;
            }
        };
        try
        {
            cancellation.ThrowIfCancellationRequested();
            publisher = new();
            publisher.Advertisement.LegacySettings.IsEnabled = false;
            publisher.Advertisement.IsAutonomousGroupOwnerEnabled = false;
            publisher.Advertisement.ListenStateDiscoverability = WiFiDirectAdvertisementListenStateDiscoverability.Normal;
            configured = !publisher.Advertisement.LegacySettings.IsEnabled && !publisher.Advertisement.IsAutonomousGroupOwnerEnabled;
            if (!configured) throw new InvalidOperationException("Non-legacy advertisement configuration was not retained.");
            publisher.StatusChanged += changed;
            statusSubscribed = true;
            listener = new();
            listener.ConnectionRequested += requested;
            listenerSubscribed = true;
            cancellation.ThrowIfCancellationRequested();
            startAttempted = true;
            stopConfirmed = false;
            publisher.Start();
            Record(publisher.Status, WiFiDirectError.Success, "after_start_read");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var startupTimeout = Task.Delay(TimeSpan.FromSeconds(5), deadline.Token);
            await Task.WhenAny(started.Task, ended.Task, startupTimeout).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (ended.Task.IsCompleted)
                outcome = "stopped_unexpectedly";
            else if (!started.Task.IsCompleted)
            {
                error ??= "PUBLISHER_START_TIMEOUT";
                outcome = "failed";
            }
            else
            {
                progress?.Invoke($"Windows 已报告广播启动，最多 {seconds} 秒；尚未确认其他设备可见。连接请求将被拒绝。");
                var duration = Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
                await Task.WhenAny(duration, ended.Task).ConfigureAwait(false);
                outcome = cancellation.IsCancellationRequested ? "cancelled" : ended.Task.IsCompleted ? "stopped_unexpectedly" : "completed";
            }
            await deadline.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (Exception ex) { error ??= DirectDiscovery.ErrorCode(ex); outcome = "failed"; }
        finally
        {
            if (publisher is not null)
            {
                try
                {
                    if (startAttempted && publisher.Status is not (WiFiDirectAdvertisementPublisherStatus.Stopped or WiFiDirectAdvertisementPublisherStatus.Aborted))
                        publisher.Stop();
                    if (startAttempted && publisher.Status is not (WiFiDirectAdvertisementPublisherStatus.Stopped or WiFiDirectAdvertisementPublisherStatus.Aborted))
                        await Task.WhenAny(ended.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    var status = publisher.Status;
                    Record(status, WiFiDirectError.Success, "after_stop_read");
                    finalStatus = status.ToString();
                    stopConfirmed = IsStopConfirmed(startAttempted, finalStatus);
                }
                catch (Exception ex) { error ??= DirectDiscovery.ErrorCode(ex); stopConfirmed = !startAttempted; }
            }
            lock (gate) acceptingEvents = false;
            if (stopConfirmed)
            {
                try
                {
                    if (publisher is not null && statusSubscribed) publisher.StatusChanged -= changed;
                    if (listener is not null && listenerSubscribed) listener.ConnectionRequested -= requested;
                    WirelessSessionGate.Release(true);
                }
                catch (Exception ex)
                {
                    // Stop may be confirmed but resource release failed: fail closed.
                    error ??= DirectDiscovery.ErrorCode(ex);
                    outcome = "failed";
                }
            }
            else if (publisher is not null && listener is not null)
                uncertainSession = (publisher, listener);
        }
        lock (gate)
        {
            outcome = FinalOutcome(outcome, startedObserved, abortedObserved, stopConfirmed, error);
            if (!stopConfirmed) error ??= "PUBLISHER_STOP_UNCONFIRMED";
            return new(outcome, startAttempted, startedObserved, configured, finalStatus, stopConfirmed,
                clock.ElapsedMilliseconds, received, released, events.ToArray(), error);
        }
    }

    internal static bool IsStopConfirmed(bool startAttempted, string status) =>
        status is "Stopped" or "Aborted" || (!startAttempted && status == "Created");

    internal static string FinalOutcome(string outcome, bool started, bool aborted, bool stopped, string? error) =>
        !stopped ? "stop_unconfirmed" : aborted ? "failed" :
        outcome == "completed" && (!started || error is not null) ? "failed" : outcome;
}
