using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wanwei;

// Explicit owner-authorized fixed-fixture lab. USB/stdin carries credentials,
// full public pins and endpoint/port control only; all file bytes use pinned TLS.
internal static class LocalWifiFixtureLab
{
    internal static async Task<int> RunAsync(string directory, CancellationToken cancellation = default)
    {
        P0Storage.EnsureSafeDirectory(directory);
        if (File.Exists(Path.Combine(directory, "local-wifi-fixture-ready.json")))
            throw new P0Exception("LOCAL_WIFI_FIXTURE_OUTPUT_ALREADY_USED");
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        LocalWifiClient? client = null;
        LocalWifiFixtureBootstrap? bootstrap = null;
        LocalWifiRestoreSnapshot? restoration = null;
        LocalWifiConnectionAttributes? attributes = null;
        P0TransferStatus? taskStatus = null;
        string phase = "reading_bootstrap", role = "none";
        string? errorCode = null, commandId = null;
        bool finished = false, success = false, taskFinished = false, taskSuccess = false;
        var started = DateTimeOffset.UtcNow;
        string? nonceHash = null;
        int completed = 0;
        void Publish()
        {
            try { WriteReport(directory, "local-wifi-fixture-status.json", new {
                schema = "wanwei.windows.local-wifi-fixture-status/1", startedAtUtc = started,
                updatedAtUtc = DateTimeOffset.UtcNow, sessionId = bootstrap?.SessionId,
                sessionNonceSha256 = nonceHash, phase, role, commandId, finished, success,
                completedFixtureCount = completed, taskFinished, taskSuccess, errorCode,
                task = taskStatus, attributes, restoration,
                transportKind = "android_local_only_hotspot", mloActualConnectionVerified = false,
                continuousInternetTested = false, ordinaryUserFlowTested = false,
                credentialsPersistedByApplication = false, sharedRouterUsed = false,
                usbCarriesFileData = false
            }); }
            catch { errorCode ??= "LOCAL_WIFI_FIXTURE_REPORT_FAILED"; }
        }
        try
        {
            using var inputDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            inputDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            bootstrap = LocalWifiFixturePolicy.ParseBootstrap(await ReadLineAsync(reader, inputDeadline.Token).ConfigureAwait(false));
            nonceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(bootstrap.Nonce)));
            using var identity = P0Identity.Load();
            identity.ExportPublic(directory);
            phase = "connecting"; Publish();
            using (var join = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                join.CancelAfter(TimeSpan.FromSeconds(25));
                client = await LocalWifiClient.ConnectAsync(bootstrap.Ssid, bootstrap.Passphrase, bootstrap.SecurityType,
                    allowDisconnectedBaseline: false, cancellationToken: join.Token).ConfigureAwait(false);
            }
            bootstrap.ClearCredentials();
            using var commandWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            commandWindow.CancelAfter(TimeSpan.FromSeconds(70));
            var path = await client.CreateFixturePathAsync(commandWindow.Token).ConfigureAwait(false);
            attributes = await client.ReadAttributesAsync(commandWindow.Token).ConfigureAwait(false);
            WriteReport(directory, "local-wifi-fixture-ready.json", new {
                schema = "wanwei.windows.local-wifi-fixture-ready/1", createdAtUtc = DateTimeOffset.UtcNow,
                sessionId = bootstrap.SessionId, localAddress = path.Local.ToString(), remoteAddress = path.Remote.ToString(),
                prefixLength = path.PrefixLength, interfaceIndex = path.InterfaceIndex,
                windowsFullPin = identity.Fingerprint, publicIdentityPath = Path.Combine(directory, "p0-public-identity.json"),
                transportKind = path.TransportKind, endpointSource = path.EndpointSource,
                sessionNonceSha256 = nonceHash, fileTransferStarted = false, mloActualConnectionVerified = false
            });
            phase = "ready"; Publish();
            var commands = new LocalWifiFixtureCommandGate(bootstrap.SessionId, bootstrap.Nonce);
            while (true)
            {
                var command = commands.Consume(await ReadLineAsync(reader, commandWindow.Token).ConfigureAwait(false));
                commandId = command.CommandId;
                if (command.Action == "end") { phase = "ending"; Publish(); break; }
                role = command.Action == "receive" ? "receiver" : "sender";
                taskFinished = false; taskSuccess = false; taskStatus = null;
                phase = "transferring"; Publish();
                string taskDirectory = Path.Combine(directory, command.Action);
                using var taskDeadline = CancellationTokenSource.CreateLinkedTokenSource(commandWindow.Token);
                taskDeadline.CancelAfter(TimeSpan.FromSeconds(25));
                var transfer = new P0Transfer(identity, taskDirectory, status => {
                    taskStatus = status;
                    taskFinished = status.CleanupFinished;
                    taskSuccess = IsSuccessful(status);
                    Publish();
                });
                await transfer.RunAsync(command.Action == "receive", path, bootstrap.PeerPin, command.Port!.Value,
                    (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(true); }, taskDeadline.Token,
                    operatorFixturePreauthorized: true, portExchange: "explicit_debug_USB_control_dynamic_android_listener",
                    connectionMode: command.Action == "receive" ? P0ConnectionMode.OutboundReceiver : P0ConnectionMode.Default).ConfigureAwait(false);
                taskFinished = taskStatus?.CleanupFinished == true; taskSuccess = taskStatus is not null && IsSuccessful(taskStatus);
                if (!taskSuccess) throw new P0Exception(taskStatus?.ErrorCode ?? "LOCAL_WIFI_FIXTURE_TASK_FAILED");
                completed++; phase = "awaiting_command"; Publish();
            }
            success = completed == 2;
            if (!success) errorCode = "LOCAL_WIFI_FIXTURE_BOTH_DIRECTIONS_NOT_COMPLETED";
        }
        catch (LocalWifiClientException error)
        {
            errorCode = error.Code; restoration = error.Restoration; attributes ??= error.Attributes;
        }
        catch (P0Exception error) { errorCode = error.Code; }
        catch (OperationCanceledException) { errorCode = cancellation.IsCancellationRequested ? "CANCELLED" : "LOCAL_WIFI_FIXTURE_DEADLINE"; }
        catch { errorCode = "LOCAL_WIFI_FIXTURE_UNEXPECTED_ERROR"; }
        finally
        {
            bootstrap?.ClearCredentials();
            if (client is not null)
            {
                phase = "restoring"; Publish();
                try { restoration = await client.RestoreAsync().ConfigureAwait(false); }
                catch { errorCode ??= "LOCAL_WIFI_FIXTURE_RESTORE_FAILED"; }
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { errorCode ??= "LOCAL_WIFI_FIXTURE_DISPOSE_FAILED"; }
                if (restoration?.Confirmed != true) { success = false; errorCode ??= "LOCAL_WIFI_FIXTURE_RESTORE_UNCONFIRMED"; }
            }
            finished = true; phase = errorCode is null && success ? "complete" : "failed";
            success = success && errorCode is null && restoration?.Confirmed == true;
            Publish();
        }
        Console.WriteLine(Path.Combine(directory, "local-wifi-fixture-status.json"));
        return success ? 0 : 2;
    }

    internal static bool IsSuccessful(P0TransferStatus status) => status is { State: "complete", ErrorCode: null,
        Bytes: P0Protocol.Size, Sha256: P0Protocol.Hash, CleanupFinished: true, SocketClosed: true,
        TemporaryFileCleanupConfirmed: true, TlsProtocol: "Tls13" } &&
        (status.Role == "receiver" ? status.LocalSaved : status.PeerSaveConfirmed);

    // Preserve the reader for subsequent commands. Never ReadToEnd: the control
    // channel remains open while its native WLAN association is temporarily active.
    internal static async Task<string> ReadLineAsync(TextReader reader, CancellationToken cancellation)
    {
        var buffer = new char[16_385]; var one = new char[1]; int used = 0;
        try
        {
            while (true)
            {
                int count = await reader.ReadAsync(one.AsMemory(), cancellation).ConfigureAwait(false);
                if (count == 0) throw new P0Exception("LOCAL_WIFI_FIXTURE_CONTROL_CLOSED");
                char value = one[0];
                if (value == '\n')
                {
                    if (used > 0 && buffer[used - 1] == '\r') used--;
                    if (used == 0) throw new P0Exception("LOCAL_WIFI_FIXTURE_INPUT_INVALID");
                    return new string(buffer, 0, used);
                }
                if (used == 16_384) throw new P0Exception("LOCAL_WIFI_FIXTURE_INPUT_TOO_LARGE");
                buffer[used++] = value;
            }
        }
        finally { Array.Clear(buffer); Array.Clear(one); }
    }

    private static void WriteReport(string directory, string name, object report)
    {
        P0Storage.EnsureSafeDirectory(directory);
        string path = Path.Combine(directory, name), temporary = path + ".tmp";
        if (File.Exists(path)) P0Storage.RejectReparse(path);
        if (File.Exists(temporary)) P0Storage.RejectReparse(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, ReportStore.Json), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

internal sealed class LocalWifiFixtureBootstrap
{
    internal Guid SessionId { get; init; }
    internal required string Nonce { get; init; }
    internal required string Ssid { get; set; }
    internal required string Passphrase { get; set; }
    internal int SecurityType { get; init; }
    internal required string PeerPin { get; init; }
    internal void ClearCredentials() { Ssid = ""; Passphrase = ""; }
    public override string ToString() => "LocalWifiFixtureBootstrap (private control redacted)";
}

internal sealed record LocalWifiFixtureCommand(string CommandId, string Action, int? Port);

internal static class LocalWifiFixturePolicy
{
    internal const string Schema = "wanwei.local-wifi.fixture.windows/1";
    internal static LocalWifiFixtureBootstrap ParseBootstrap(string line)
    {
        try
        {
            using var document = ParseObject(line, ["schema", "sessionId", "nonce", "ssid", "passphrase", "securityType", "peerPin", "fixtureConsent", "fixture"]);
            var root = document.RootElement;
            Guid id = Session(root); string nonce = Text(root, "nonce"); string peer = Text(root, "peerPin");
            if (!Hex32(nonce) || !P0Protocol.IsFingerprint(peer) || root.GetProperty("fixtureConsent").ValueKind != JsonValueKind.True ||
                Text(root, "fixture") != P0Protocol.Fixture || !root.GetProperty("securityType").TryGetInt32(out int security)) throw Invalid();
            string ssid = Text(root, "ssid"), passphrase = Text(root, "passphrase");
            LocalWifiClientPolicy.ValidateRequest(ssid, passphrase, security);
            return new() { SessionId = id, Nonce = nonce, Ssid = ssid, Passphrase = passphrase, SecurityType = security, PeerPin = peer };
        }
        catch (P0Exception) { throw; }
        catch { throw Invalid(); }
    }
    internal static JsonDocument ParseObject(string line, string[] keys)
    {
        if (string.IsNullOrEmpty(line) || line.Length > 16_384 || Encoding.UTF8.GetByteCount(line) > 16_384 || line[0] == '\uFEFF') throw Invalid();
        var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var actual = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (actual.Length != keys.Length || actual.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
                actual.Except(keys, StringComparer.Ordinal).Any() || Text(root, "schema") != Schema) throw Invalid();
            return document;
        }
        catch { document.Dispose(); throw Invalid(); }
    }
    internal static Guid Session(JsonElement root) => Guid.TryParse(Text(root, "sessionId"), out var id) && id != Guid.Empty ? id : throw Invalid();
    internal static string Text(JsonElement root, string name) => root.GetProperty(name).ValueKind == JsonValueKind.String ? root.GetProperty(name).GetString()! : throw Invalid();
    internal static bool Hex32(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static P0Exception Invalid() => new("LOCAL_WIFI_FIXTURE_INPUT_INVALID");
}

internal sealed class LocalWifiFixtureCommandGate(Guid sessionId, string nonce)
{
    private readonly HashSet<string> consumedIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> actions = new(StringComparer.Ordinal);
    private bool ended;
    internal LocalWifiFixtureCommand Consume(string line)
    {
        try
        {
            // Determine shape without accepting unvalidated duplicates or fields.
            using var shape = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
            bool end = LocalWifiFixturePolicy.Text(shape.RootElement, "action") == "end";
            using var document = LocalWifiFixturePolicy.ParseObject(line, end ?
                ["schema", "sessionId", "nonce", "commandId", "action"] : ["schema", "sessionId", "nonce", "commandId", "action", "port"]);
            var root = document.RootElement;
            string requestNonce = LocalWifiFixturePolicy.Text(root, "nonce"), id = LocalWifiFixturePolicy.Text(root, "commandId"), action = LocalWifiFixturePolicy.Text(root, "action");
            if (LocalWifiFixturePolicy.Session(root) != sessionId || !LocalWifiFixturePolicy.Hex32(requestNonce) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(requestNonce), Encoding.ASCII.GetBytes(nonce)) ||
                !LocalWifiFixturePolicy.Hex32(id) || action is not ("send" or "receive" or "end")) throw LocalWifiFixturePolicy.Invalid();
            if (ended || !consumedIds.Add(id)) throw new P0Exception("LOCAL_WIFI_FIXTURE_COMMAND_REPLAY");
            // Consume before attempting native/socket operations. Failure must not
            // grant a fresh task under the same identifier or extend the window.
            if (action == "end") { ended = true; return new(id, action, null); }
            if (actions.Count >= 2 || !actions.Add(action)) throw new P0Exception("LOCAL_WIFI_FIXTURE_DIRECTION_ALREADY_USED");
            var portElement = root.GetProperty("port");
            if (!portElement.TryGetInt32(out int port) || port is < 1 or > 65535) throw LocalWifiFixturePolicy.Invalid();
            return new(id, action, port);
        }
        catch (P0Exception) { throw; }
        catch { throw LocalWifiFixturePolicy.Invalid(); }
    }
    public override string ToString() => "LocalWifiFixtureCommandGate (private control redacted)";
}
