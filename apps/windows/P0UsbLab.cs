using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Wanwei;

// Explicit developer-only fixture authorization supplied through the local USB
// experiment workflow. The normal GUI never reads these files or trusts them.
internal sealed record P0UsbLabBootstrap(string SessionNonce, string PeerPin, string? ExpectedPhoneP2pMac)
{
    internal static P0UsbLabBootstrap Load(string path) => Parse(P0LabJson.ReadFile(path));
    internal void ConsumeAuthorization(string reportDirectory)
    {
        P0Storage.EnsureSafeDirectory(reportDirectory);
        string correlation = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(SessionNonce)));
        using var file = new FileStream(Path.Combine(reportDirectory, "p0-lab-bootstrap-" + correlation + ".used"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(Encoding.UTF8.GetBytes("fixed_1MiB_USB_lab_authorization_consumed\n")); file.Flush(true);
    }
    internal static P0UsbLabBootstrap Parse(byte[] bytes)
    {
        var fields = P0LabJson.Object(bytes);
        P0LabJson.Keys(fields, ["sessionNonce", "peerPin", "fixtureConsent", "fixture", "role"], ["expectedPhoneP2pMac"]);
        string nonce = P0LabJson.Text(fields, "sessionNonce"), pin = P0LabJson.Text(fields, "peerPin");
        if (!P0Protocol.IsTransferId(nonce) || !P0Protocol.IsFingerprint(pin) || fields["fixtureConsent"].ValueKind != JsonValueKind.True ||
            P0LabJson.Text(fields, "fixture") != P0Protocol.Fixture || P0LabJson.Text(fields, "role") != "receiver") throw new ArgumentException("USB_LAB_BOOTSTRAP_REJECTED");
        string? mac = null;
        if (fields.ContainsKey("expectedPhoneP2pMac"))
        {
            mac = NormalizeMac(P0LabJson.Text(fields, "expectedPhoneP2pMac"));
            if (mac is null) throw new ArgumentException("USB_LAB_PEER_MAC_REJECTED");
        }
        return new(nonce, pin, mac);
    }
    internal static string? NormalizeMac(string value)
    {
        if (!Regex.IsMatch(value, "^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$", RegexOptions.CultureInvariant)) return null;
        return value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    }
    internal bool AllowsNativePeer(string deviceId, string? deviceAddress)
    {
        if (ExpectedPhoneP2pMac is null) return true;
        if (deviceAddress is not null && NormalizeMac(deviceAddress) == ExpectedPhoneP2pMac) return true;
        // Match bounded MAC tokens, never a substring obtained by stripping all
        // punctuation from a GUID or an arbitrary device identifier.
        foreach (Match match in Regex.Matches(deviceId, "(?<![0-9a-fA-F])[0-9a-fA-F]{2}(?:[:-][0-9a-fA-F]{2}){5}(?![0-9a-fA-F])", RegexOptions.CultureInvariant))
            if (NormalizeMac(match.Value) == ExpectedPhoneP2pMac) return true;
        return false;
    }
}

internal sealed record P0UsbLabCommand(string SessionNonce, string CommandId, string Action, int Port)
{
    internal static P0UsbLabCommand Parse(byte[] bytes, string expectedNonce)
    {
        var fields = P0LabJson.Object(bytes);
        string action = P0LabJson.Text(fields, "action");
        if (action is not ("receive" or "send" or "cancel" or "end")) throw new ArgumentException("USB_LAB_ACTION_REJECTED");
        P0LabJson.Keys(fields, action == "send" ? ["sessionNonce", "commandId", "action", "port"] : ["sessionNonce", "commandId", "action"], []);
        string nonce = P0LabJson.Text(fields, "sessionNonce"), commandId = P0LabJson.Text(fields, "commandId");
        if (!P0Protocol.IsTransferId(nonce) || nonce != expectedNonce || !P0Protocol.IsTransferId(commandId)) throw new ArgumentException("USB_LAB_NONCE_REJECTED");
        int port = 0;
        if (action == "send")
        {
            var field = fields["port"];
            if (field.ValueKind != JsonValueKind.Number || !Regex.IsMatch(field.GetRawText(), "^[1-9][0-9]*$", RegexOptions.CultureInvariant) || !field.TryGetInt32(out port) || port is < 1 or > 65535) throw new ArgumentException("USB_LAB_PORT_REJECTED");
        }
        return new(nonce, commandId, action, port);
    }
}

internal static class P0LabJson
{
    internal static byte[] ReadFile(string path)
    {
        P0Storage.RejectReparse(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 16384) throw new ArgumentException("USB_LAB_FILE_SIZE_REJECTED");
        byte[] bytes = new byte[file.Length]; file.ReadExactly(bytes); return bytes;
    }
    internal static Dictionary<string, JsonElement> Object(byte[] bytes)
    {
        try
        {
            if (bytes.Length is < 1 or > 16384 || (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)) throw new ArgumentException("USB_LAB_JSON_REJECTED");
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("USB_LAB_JSON_REJECTED");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in json.RootElement.EnumerateObject()) if (!fields.TryAdd(entry.Name, entry.Value.Clone())) throw new ArgumentException("USB_LAB_DUPLICATE_FIELD");
            return fields;
        }
        catch (JsonException) { throw new ArgumentException("USB_LAB_JSON_REJECTED"); }
        catch (DecoderFallbackException) { throw new ArgumentException("USB_LAB_JSON_REJECTED"); }
    }
    internal static void Keys(Dictionary<string, JsonElement> fields, string[] required, string[] optional)
    {
        if (required.Any(k => !fields.ContainsKey(k)) || fields.Keys.Any(k => !required.Contains(k) && !optional.Contains(k))) throw new ArgumentException("USB_LAB_FIELDS_REJECTED");
    }
    internal static string Text(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String) throw new ArgumentException("USB_LAB_FIELD_TYPE_REJECTED");
        return value.GetString()!;
    }
}

internal sealed class P0UsbLabController(P0UsbLabBootstrap bootstrap, P0Identity identity, string reportDirectory, Action<string> progress)
{
    private readonly HashSet<string> commands = new(StringComparer.Ordinal);
    private int transfers;
    private string state = "waiting_for_direct", lastCommand = "initial_receive", lastResult = "none";
    private bool releaseConfirmed = true;
    private P0TransferStatus? latest;
    private void Publish()
    {
        string json = JsonSerializer.Serialize(new { schema = "wanwei.p0.usb-lab/1", sessionToken = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(bootstrap.SessionNonce))),
            state, commandId = lastCommand, result = lastResult, transfersStarted = transfers, maximumTransfers = 2,
            operatorFixturePreauthorized = true, preauthorizationSource = "explicit_cli_local_USB_bootstrap",
            normalGuiPolicyChanged = false, releaseConfirmed }, ReportStore.Json);
        string temporary = Path.Combine(reportDirectory, "p0-lab-session.json.tmp");
        File.WriteAllText(temporary, json, new UTF8Encoding(false)); File.Move(temporary, Path.Combine(reportDirectory, "p0-lab-session.json"), true);
        progress("USB 测试：" + state + " · " + lastResult);
    }
    internal async Task<bool> RunAsync(P0Path path, CancellationToken cancellation)
    {
        P0Storage.EnsureSafeDirectory(reportDirectory);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancellationTokenSource? currentStop = null; Task? current = null;
        void Start(bool receiver, int port)
        {
            transfers++; latest = null; currentStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var transfer = new P0Transfer(identity, reportDirectory, value => latest = value);
            state = receiver ? "receiving_fixture" : "sending_fixture"; lastResult = "running"; Publish();
            current = transfer.RunAsync(receiver, path, bootstrap.PeerPin, port, (_, _) => Task.FromResult(true), currentStop.Token, operatorFixturePreauthorized: true);
        }
        try
        {
            Start(true, 0);
            while (!lifetime.IsCancellationRequested)
            {
                if (!path.Verify()) { state = "direct_path_lost"; lastResult = "DIRECT_PATH_UNVERIFIED"; break; }
                if (current is { IsCompleted: true })
                {
                    await current.ConfigureAwait(false); current = null; currentStop!.Dispose(); currentStop = null;
                    releaseConfirmed = latest is { CleanupFinished: true, SocketClosed: true, TemporaryFileCleanupConfirmed: true };
                    state = releaseConfirmed ? "waiting_for_command" : "cleanup_unconfirmed"; lastResult = latest?.State ?? "not_reported"; Publish();
                    if (!releaseConfirmed) break;
                }
                string commandPath = Path.Combine(reportDirectory, "p0-lab-command.json");
                if (File.Exists(commandPath))
                {
                    try
                    {
                        var command = P0UsbLabCommand.Parse(P0LabJson.ReadFile(commandPath), bootstrap.SessionNonce);
                        if (commands.Add(command.CommandId))
                        {
                            lastCommand = command.CommandId;
                            if (commands.Count > 64) { state = "command_limit_reached"; lastResult = "POLICY"; break; }
                            if (command.Action == "end") { state = "operator_ended"; lastResult = "ending"; break; }
                            if (command.Action == "cancel") { currentStop?.Cancel(); lastResult = current is null ? "nothing_active" : "cancellation_requested"; Publish(); }
                            else if (current is not null) { lastResult = "BUSY"; Publish(); }
                            else if (transfers >= 2) { lastResult = "FIXTURE_TRANSFER_LIMIT"; Publish(); }
                            else Start(command.Action == "receive", command.Port);
                        }
                    }
                    catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        if (lastResult != "INVALID_COMMAND") { lastResult = "INVALID_COMMAND"; Publish(); }
                    }
                }
                await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { state = "session_stopped"; }
        finally
        {
            currentStop?.Cancel();
            if (current is not null)
            {
                try { await current.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch { releaseConfirmed = false; }
                releaseConfirmed &= latest is { CleanupFinished: true, SocketClosed: true, TemporaryFileCleanupConfirmed: true };
            }
            currentStop?.Dispose(); Publish();
        }
        return releaseConfirmed;
    }
}
