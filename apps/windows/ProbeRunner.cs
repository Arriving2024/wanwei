using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Foundation.Metadata;
using Windows.Devices.WiFiDirect;

namespace Wanwei;

public static class ProbeRunner
{
    [DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    public static async Task<LabReport> RunAsync(int? discoverySeconds, CancellationToken cancellation,
        Action<string>? progress = null, int? advertisementSeconds = null,
        int? pairingSeconds = null, IPairingUi? pairingUi = null)
    {
        if (new[] { discoverySeconds, advertisementSeconds, pairingSeconds }.Count(s => s is not null) > 1)
            throw new ArgumentException("Wireless operation modes cannot overlap in a probe run.");
        if (pairingSeconds is not null && pairingUi is null) throw new ArgumentException("Pairing requires a live UI dispatcher.");
        if (WirelessSessionGate.RestartRequired) throw new InvalidOperationException("Wireless release unconfirmed; restart required.");
        using var privacy = new RunPrivacy();
        var report = new LabReport { PackageIdentity = NativeWlan.PackageStatus() };
        var clock = Stopwatch.StartNew();
        report.NetworkSamples.Add(NativeWlan.Snapshot(privacy, clock.ElapsedMilliseconds));
        foreach (string name in new[] {
            "Windows.Devices.WiFiDirect.WiFiDirectDevice",
            "Windows.Devices.WiFiDirect.WiFiDirectAdvertisementPublisher",
            "Windows.Devices.WiFiDirect.WiFiDirectConnectionListener" })
        {
            try
            {
                bool present = ApiInformation.IsTypePresent(name);
                report.ApiChecks.Add(new(name, present ? "present" : "absent", "Metadata only; hardware usability not established."));
            }
            catch (Exception ex) { report.ApiChecks.Add(new(name, "error", DirectDiscovery.ErrorCode(ex))); }
        }
        try
        {
            var selector = WiFiDirectDevice.GetDeviceSelector(WiFiDirectDeviceSelectorType.AssociationEndpoint);
            report.ApiChecks.Add(new("WiFiDirectDevice.GetDeviceSelector(AssociationEndpoint)",
                string.IsNullOrWhiteSpace(selector) ? "empty" : "call_succeeded", "Actual native API call; not a connection test."));
        }
        catch (Exception ex) { report.ApiChecks.Add(new("WiFiDirectDevice.GetDeviceSelector", "error", DirectDiscovery.ErrorCode(ex))); }
        progress?.Invoke("原生接口检查完成，读取驱动能力报告。");
        report.DriverEvidence = await ReadDriverEvidence(cancellation).ConfigureAwait(false);
        if ((discoverySeconds is not null || advertisementSeconds is not null || pairingSeconds is not null) && !cancellation.IsCancellationRequested)
        {
            // All snapshots in one report use the same ephemeral pseudonym key.
            // Samples are observations, never a proof of uninterrupted connectivity.
            using var sampling = new CancellationTokenSource();
            var sampleTask = Task.Run(async () => {
                try
                {
                    while (true)
                    {
                        await Task.Delay(1000, sampling.Token).ConfigureAwait(false);
                        report.NetworkSamples.Add(NativeWlan.Snapshot(privacy, clock.ElapsedMilliseconds));
                    }
                }
                catch (OperationCanceledException) { }
            });
            try
            {
                if (discoverySeconds is not null)
                    report.Discovery = await DirectDiscovery.RunAsync(discoverySeconds.Value, privacy, cancellation, progress).ConfigureAwait(false);
                else if (advertisementSeconds is not null)
                    report.Advertisement = await DirectAdvertisement.RunAsync(advertisementSeconds!.Value, cancellation, progress).ConfigureAwait(false);
                else
                    report.PairingSession = await DirectPairing.RunAsync(pairingSeconds!.Value, privacy, pairingUi!, cancellation, progress).ConfigureAwait(false);
            }
            finally
            {
                await sampling.CancelAsync().ConfigureAwait(false);
                await sampleTask.ConfigureAwait(false);
            }
        }
        else if (discoverySeconds is not null)
            report.Discovery = new("cancelled", false, "not_created", true, 0, [], null);
        else if (advertisementSeconds is not null)
            report.Advertisement = new("cancelled", false, false, false, "not_created", true, 0, 0, 0, [], null);
        else if (pairingSeconds is not null)
            report.PairingSession = new("cancelled", false, false, false, "not_created", true, true, 0, 0, 0, 0,
                null, null, "not_attempted", false, false, "not_created", [], [], null);
        report.NetworkSamples.Add(NativeWlan.Snapshot(privacy, clock.ElapsedMilliseconds));
        report.SampledApContinuity = Continuity.Evaluate(report.NetworkSamples);
        report.OperationOutcome =
            report.Discovery?.Outcome is "failed" or "stop_unconfirmed" or "stopped_unexpectedly" ||
            report.Advertisement?.Outcome is "failed" or "stop_unconfirmed" or "stopped_unexpectedly" ||
            report.PairingSession?.Outcome is "failed" or "release_unconfirmed" or "pairing_failed" or "connection_failed" or "stage_timeout" or "connected_without_endpoints" ? "failed" :
            cancellation.IsCancellationRequested ? "cancelled" : "completed";
        progress?.Invoke("本次操作结束。原 Wi-Fi 样本不代表互联网连续可用；本版本尚未实现文件传输。");
        return report;
    }

    private static async Task<string> ReadDriverEvidence(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var process = new Process();
        try
        {
        process.StartInfo = new() {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"),
            Arguments = "wlan show wirelesscapabilities", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
            process.Start();
            using var outputBytes = new MemoryStream();
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(outputBytes, deadline.Token);
            var errorTask = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) return $"netsh_failed_exit_{process.ExitCode}";
            var output = DecodeNetsh(outputBytes.ToArray(), (int)GetOEMCP());
            // This netsh command contains capabilities, not saved profiles or credentials.
            return Regex.Replace(output, @"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", "[interface-id-redacted]");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            return cancellation.IsCancellationRequested ? "cancelled" : "netsh_timeout";
        }
        catch (Exception ex) { return DirectDiscovery.ErrorCode(ex); }
    }
    internal static string DecodeNetsh(byte[] bytes, int fallbackCodePage)
    {
        // netsh can inherit a UTF-8 console, even on a system whose OEM code page is 936.
        // Decode raw bytes instead of assuming the user's locale determines process output.
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(fallbackCodePage, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetString(bytes);
        }
    }
}
