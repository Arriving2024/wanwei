using System.Text;
using System.Text.Json;

namespace Wanwei;

/// <summary>Explicit private lab: credentials arrive only on stdin, never command-line arguments or files.</summary>
internal static class LocalWifiLab
{
    internal static async Task<int> RunAsync(string directory, int seconds, CancellationToken cancellationToken = default)
    {
        if (seconds is < 1 or > 15) throw new ArgumentException("本地 Wi-Fi 实验观察时间为 1–15 秒。");
        string input;
        using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 4096))
        {
            using var inputTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            inputTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            char[] buffer = new char[16_385];
            int used = 0;
            while (used < buffer.Length)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(used), inputTimeout.Token);
                if (read == 0) break;
                used += read;
            }
            if (used == 0 || used == buffer.Length) throw new ArgumentException("本地实验参数无效。");
            input = new string(buffer, 0, used);
            Array.Clear(buffer);
        }
        using var json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 4 });
        var root = json.RootElement;
        var keys = root.EnumerateObject().Select(x => x.Name).ToArray();
        if (keys.Length != 5 || keys.Distinct(StringComparer.Ordinal).Count() != 5 ||
            keys.Except(new[] { "schema", "sessionId", "ssid", "passphrase", "securityType" }).Any() ||
            root.GetProperty("schema").GetString() != "wanwei.local-wifi.lab/1" ||
            !Guid.TryParse(root.GetProperty("sessionId").GetString(), out var id) || id == Guid.Empty)
            throw new ArgumentException("本地实验参数无效。");
        string ssid = root.GetProperty("ssid").GetString() ?? "";
        string passphrase = root.GetProperty("passphrase").GetString() ?? "";
        int security = root.GetProperty("securityType").GetInt32();
        LocalWifiClient? client = null;
        object? attributes = null, restoration = null;
        List<object> samples = [];
        string? errorCode = null;
        uint? nativeError = null;
        bool connected = false, restored = false;
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            client = await LocalWifiClient.ConnectAsync(ssid, passphrase, security,
                allowDisconnectedBaseline: false, cancellationToken: deadline.Token);
            connected = true;
            attributes = client.Attributes;
            for (int i = 0; i < seconds; i++)
            {
                var sample = await client.ReadAttributesAsync(deadline.Token);
                samples.Add(sample);
                if (!sample.Connected || !sample.RequestedSsidMatched || !sample.TemporaryProfileMode)
                    throw new LocalWifiClientException("LOCAL_WIFI_ASSOCIATION_LOST");
                if (i + 1 < seconds) await Task.Delay(1000, deadline.Token);
            }
        }
        catch (LocalWifiClientException error)
        {
            errorCode = error.Code; nativeError = error.NativeError;
            attributes = error.Attributes; restoration = error.Restoration;
            restored = error.Restoration?.Confirmed == true;
        }
        catch (Exception error) { errorCode = error.GetType().Name; }
        finally
        {
            if (client is not null)
            {
                try
                {
                    restoration = await client.RestoreAsync();
                    restored = client.Restoration?.Confirmed == true;
                }
                catch (Exception error) { errorCode ??= "RESTORE_" + error.GetType().Name; }
                finally
                {
                    try { client.Dispose(); }
                    catch (Exception error) { errorCode ??= "DISPOSE_" + error.GetType().Name; restored = false; }
                }
            }
            ssid = ""; passphrase = ""; input = "";
        }
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "windows-local-wifi-" + Guid.NewGuid().ToString("N") + ".json");
        var report = new
        {
            schema = "wanwei.windows.local-wifi-lab/1", startedAtUtc = started, finishedAtUtc = DateTimeOffset.UtcNow,
            androidSessionId = id, connected, originalConnectionRestored = restored, errorCode, nativeError,
            attributes, samples, restoration,
            connectionMethod = "native_wlan_temporary_profile",
            credentialsPersistedByApplication = false, rawSsidOrProfileStoredInReport = false,
            sharedRouterUsed = false, fileTransferStarted = false,
            mloActualConnectionVerified = false, continuousInternetTested = false,
            notes = "Explicit owner-authorized lab. USB carries temporary connection parameters only. Link rates are PHY observations, not file throughput. Native temporary profile is not a saved network profile."
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(path);
        return connected && restored && errorCode is null ? 0 : 2;
    }
}
