using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Foundation;

namespace Wanwei;

internal static class DirectDiscovery
{
    private static DeviceWatcher? uncertainWatcher;
    public static async Task<ScanResult> RunAsync(int seconds, RunPrivacy privacy,
        CancellationToken cancellation, Action<string>? progress)
    {
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!WirelessSessionGate.TryEnter()) throw new InvalidOperationException("Wireless session active or release unconfirmed; restart required.");
        var clock = Stopwatch.StartNew();
        var peers = new Dictionary<string, PeerEvidence>();
        var gate = new object();
        bool enumerationComplete = false, acceptingEvents = true;
        string? error = null;
        string outcome = "failed";
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DeviceWatcher? watcher = null;
        bool stopConfirmed = true, releaseConfirmed = true;
        string finalStatus = "not_created";

        void Guard(Action callback)
        {
            lock (gate)
            {
                if (!acceptingEvents) return;
                try { callback(); }
                catch (Exception ex) { error ??= ErrorCode(ex); }
            }
        }
        TypedEventHandler<DeviceWatcher, DeviceInformation> added = (_, device) => Guard(() =>
        {
            peers[device.Id] = new(privacy.Token("peer", device.Id), device.Pairing.IsPaired, true);
            progress?.Invoke($"系统返回 {peers.Values.Count(p => p.Present)} 个候选条目（尚未确认是万维设备）。");
        });
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> updated = (_, update) => Guard(() =>
        {
            if (peers.TryGetValue(update.Id, out var peer) &&
                update.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var value) && value is bool paired)
                peers[update.Id] = peer with { IsPaired = paired };
        });
        TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> removed = (_, update) => Guard(() =>
        {
            if (peers.TryGetValue(update.Id, out var peer)) peers[update.Id] = peer with { Present = false };
        });
        TypedEventHandler<DeviceWatcher, object> enumerated = (_, _) => Guard(() => enumerationComplete = true);
        TypedEventHandler<DeviceWatcher, object> onStopped = (_, _) => stopped.TrySetResult();
        try
        {
            var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
            watcher = DeviceInformation.CreateWatcher(selector, ["System.Devices.Aep.IsPaired"], DeviceInformationKind.AssociationEndpoint);
            watcher.Added += added;
            watcher.Updated += updated;
            watcher.Removed += removed;
            watcher.EnumerationCompleted += enumerated;
            watcher.Stopped += onStopped;
            cancellation.ThrowIfCancellationRequested();
            watcher.Start();
            stopConfirmed = false;
            progress?.Invoke($"Wi-Fi Direct 原生扫描已启动，最多 {seconds} 秒。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var deadline = Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
            var first = await Task.WhenAny(deadline, stopped.Task).ConfigureAwait(false);
            if (first == stopped.Task) outcome = "stopped_unexpectedly";
            else outcome = cancellation.IsCancellationRequested ? "cancelled" : "completed";
            await timeout.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (Exception ex) { error = ErrorCode(ex); outcome = "failed"; }
        finally
        {
            if (watcher is not null)
            {
                try
                {
                    if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                        watcher.Stop();
                    if (watcher.Status == DeviceWatcherStatus.Stopping)
                        await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                    finalStatus = watcher.Status.ToString();
                    stopConfirmed = watcher.Status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted or DeviceWatcherStatus.Created;
                    if (!stopConfirmed) { outcome = "stop_unconfirmed"; error ??= "WATCHER_STOP_TIMEOUT"; }
                    if (watcher.Status == DeviceWatcherStatus.Aborted) { outcome = "failed"; error ??= "WATCHER_ABORTED"; }
                }
                catch (Exception ex) { error ??= ErrorCode(ex); outcome = "failed"; }
                lock (gate) acceptingEvents = false;
                try { watcher.Added -= added; } catch (Exception ex) { releaseConfirmed = false; error ??= ErrorCode(ex); }
                try { watcher.Updated -= updated; } catch (Exception ex) { releaseConfirmed = false; error ??= ErrorCode(ex); }
                try { watcher.Removed -= removed; } catch (Exception ex) { releaseConfirmed = false; error ??= ErrorCode(ex); }
                try { watcher.EnumerationCompleted -= enumerated; } catch (Exception ex) { releaseConfirmed = false; error ??= ErrorCode(ex); }
                try { watcher.Stopped -= onStopped; } catch (Exception ex) { releaseConfirmed = false; error ??= ErrorCode(ex); }
                if (!stopConfirmed || !releaseConfirmed) { uncertainWatcher = watcher; outcome = "stop_unconfirmed"; stopConfirmed = false; }
            }
            WirelessSessionGate.Release(stopConfirmed && releaseConfirmed);
        }
        lock (gate)
        {
            if (error is not null && outcome == "completed") outcome = "failed";
            return new(outcome, enumerationComplete, finalStatus, stopConfirmed, clock.ElapsedMilliseconds,
                peers.Values.OrderBy(p => p.Token).ToArray(), error);
        }
    }
    public static string ErrorCode(Exception ex) => $"{ex.GetType().Name}:0x{ex.HResult:X8}";
}
