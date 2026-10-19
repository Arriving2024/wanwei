using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Wanwei;

internal static class LocalWifiFixtureSelfTests
{
    internal static void Run(Action<string, bool> check)
    {
        PathPolicy(check);
        InputPolicy(check);
        ReverseRolesAsync(check, wrongPin: false).GetAwaiter().GetResult();
        ReverseRolesAsync(check, wrongPin: true).GetAwaiter().GetResult();
    }

    private static void PathPolicy(Action<string, bool> check)
    {
        Guid adapter = Guid.NewGuid();
        LocalWifiFixtureAddress Address(string text, int prefix = 24) => new(IPAddress.Parse(text), prefix);
        IPAddress Gateway(string text) => IPAddress.Parse(text);
        bool Reject(LocalWifiFixtureAddress[] local, IPAddress[] remote, string code)
        {
            try { LocalWifiFixturePathPolicy.Select(adapter, 5, local, remote); return false; }
            catch (LocalWifiClientException error) { return error.Code == code; }
        }
        var endpoint = LocalWifiFixturePathPolicy.Select(adapter, 5, [Address("192.168.87.23")], [Gateway("192.168.87.1")]);
        check("LOHS fixture uses only exact adapter IPv4 and on-subnet gateway", endpoint ==
            new LocalWifiFixtureEndpoint(Gateway("192.168.87.23"), Gateway("192.168.87.1"), 24, adapter, 5));
        check("LOHS fixture waits for missing DHCP gateway", Reject([Address("192.168.87.23")], [], "LOCAL_WIFI_FIXTURE_DHCP_NOT_READY"));
        check("LOHS fixture refuses multiple local IPv4 addresses", Reject([Address("192.168.87.23"), Address("192.168.87.24")], [Gateway("192.168.87.1")], "LOCAL_WIFI_FIXTURE_ENDPOINT_AMBIGUOUS"));
        check("LOHS fixture refuses multiple adapter gateways", Reject([Address("192.168.87.23")], [Gateway("192.168.87.1"), Gateway("192.168.87.2")], "LOCAL_WIFI_FIXTURE_ENDPOINT_AMBIGUOUS"));
        check("LOHS fixture never adopts another subnet default gateway", Reject([Address("192.168.87.23")], [Gateway("10.0.0.1")], "LOCAL_WIFI_FIXTURE_GATEWAY_OUTSIDE_SUBNET"));
        check("LOHS fixture rejects network endpoint", Reject([Address("192.168.87.0")], [Gateway("192.168.87.1")], "LOCAL_WIFI_FIXTURE_GATEWAY_OUTSIDE_SUBNET"));
        check("LOHS fixture rejects broadcast gateway", Reject([Address("192.168.87.23")], [Gateway("192.168.87.255")], "LOCAL_WIFI_FIXTURE_GATEWAY_OUTSIDE_SUBNET"));
        check("LOHS fixture rejects all-address subnet", Reject([Address("192.168.87.23", 0)], [Gateway("192.168.87.1")], "LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID"));
        check("LOHS fixture rejects self gateway", Reject([Address("192.168.87.23")], [Gateway("192.168.87.23")], "LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID"));
        check("LOHS fixture rejects APIPA fallback", Reject([Address("169.254.1.2")], [Gateway("169.254.1.1")], "LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID"));
        check("LOHS fixture rejects loopback and multicast", Reject([Address("127.0.0.2")], [Gateway("127.0.0.1")], "LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID") &&
            Reject([Address("224.0.0.2")], [Gateway("224.0.0.1")], "LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID"));
        check("LOHS fixture IPv6 does not become IPv4 proof", Reject([Address("::1")], [Gateway("::2")], "LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID"));
    }

    private static void InputPolicy(Action<string, bool> check)
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        const string id = "1123456789abcdef0123456789abcdef";
        Guid session = Guid.NewGuid();
        string bootstrap = JsonSerializer.Serialize(new { schema = LocalWifiFixturePolicy.Schema, sessionId = session,
            nonce, ssid = "host-test-only-no-radio", passphrase = "test-only-key", securityType = 1,
            peerPin = new string('a', 64), fixtureConsent = true, fixture = P0Protocol.Fixture });
        string Command(string action = "send", string command = id, string? sessionNonce = null, Guid? requestSession = null, int port = 51323) =>
            JsonSerializer.Serialize(new { schema = LocalWifiFixturePolicy.Schema, sessionId = requestSession ?? session,
                nonce = sessionNonce ?? nonce, commandId = command, action, port });
        string End(string command = "2123456789abcdef0123456789abcdef") => JsonSerializer.Serialize(new {
            schema = LocalWifiFixturePolicy.Schema, sessionId = session, nonce, commandId = command, action = "end" });
        bool RejectBootstrap(string input)
        {
            try { LocalWifiFixturePolicy.ParseBootstrap(input); return false; }
            catch (P0Exception error) { return error.Code == "LOCAL_WIFI_FIXTURE_INPUT_INVALID"; }
        }
        bool RejectCommand(LocalWifiFixtureCommandGate gate, string input, string code)
        {
            try { gate.Consume(input); return false; } catch (P0Exception error) { return error.Code == code; }
        }
        var parsed = LocalWifiFixturePolicy.ParseBootstrap(bootstrap);
        check("LOHS fixture bootstrap requires explicit fixed-fixture consent", parsed.SessionId == session && parsed.Nonce == nonce && parsed.SecurityType == 1);
        check("LOHS fixture bootstrap object string redacts credentials", !parsed.ToString().Contains(parsed.Passphrase) && !parsed.ToString().Contains(parsed.Ssid));
        check("LOHS fixture rejects consent=false", RejectBootstrap(bootstrap.Replace("\"fixtureConsent\":true", "\"fixtureConsent\":false")));
        check("LOHS fixture rejects duplicate bootstrap keys", RejectBootstrap(bootstrap[..^1] + ",\"nonce\":\"" + nonce + "\"}"));
        check("LOHS fixture rejects unrecognized bootstrap fields", RejectBootstrap(bootstrap[..^1] + ",\"outputPath\":\"arbitrary\"}"));
        check("LOHS fixture rejects uppercase nonce", RejectBootstrap(bootstrap.Replace(nonce, nonce.ToUpperInvariant())));
        check("LOHS fixture rejects different fixture", RejectBootstrap(bootstrap.Replace(P0Protocol.Fixture, "arbitrary-file")));
        check("LOHS fixture rejects open WiFi credentials", RejectBootstrap(bootstrap.Replace("\"securityType\":1", "\"securityType\":0")));
        check("LOHS fixture rejects oversized UTF8 control object", RejectBootstrap(bootstrap[..^1] + ",\"pad\":\"" + new string('界', 6000) + "\"}"));
        parsed.ClearCredentials();
        check("LOHS fixture drops application references to bootstrap credentials", parsed.Ssid == "" && parsed.Passphrase == "");
        var gate = new LocalWifiFixtureCommandGate(session, nonce);
        check("LOHS fixture first authenticated command selects dynamic Android port", gate.Consume(Command()).Port == 51323);
        check("LOHS fixture consumes command identifier before task starts", RejectCommand(gate, Command(), "LOCAL_WIFI_FIXTURE_COMMAND_REPLAY"));
        check("LOHS fixture same direction cannot be retried with new identifier", RejectCommand(gate, Command(command: "3123456789abcdef0123456789abcdef"), "LOCAL_WIFI_FIXTURE_DIRECTION_ALREADY_USED"));
        check("LOHS fixture rejects another session", RejectCommand(new(session, nonce), Command(requestSession: Guid.NewGuid()), "LOCAL_WIFI_FIXTURE_INPUT_INVALID"));
        check("LOHS fixture rejects wrong nonce", RejectCommand(new(session, nonce), Command(sessionNonce: new string('f', 32)), "LOCAL_WIFI_FIXTURE_INPUT_INVALID"));
        check("LOHS fixture rejects invalid listener port", RejectCommand(new(session, nonce), Command(port: 0), "LOCAL_WIFI_FIXTURE_INPUT_INVALID"));
        check("LOHS fixture rejects duplicate command fields", RejectCommand(new(session, nonce), Command()[..^1] + ",\"action\":\"receive\"}", "LOCAL_WIFI_FIXTURE_INPUT_INVALID"));
        var both = new LocalWifiFixtureCommandGate(session, nonce);
        both.Consume(Command()); both.Consume(Command("receive", "4123456789abcdef0123456789abcdef"));
        check("LOHS fixture opposite role consumes separate identifier", both.Consume(End()).Action == "end");
        check("LOHS fixture end closes authorization", RejectCommand(both, End("5123456789abcdef0123456789abcdef"), "LOCAL_WIFI_FIXTURE_COMMAND_REPLAY"));
        using var lines = new StringReader(bootstrap + "\r\n" + Command() + "\n");
        check("LOHS fixture bootstrap reader preserves next control line", LocalWifiFixtureLab.ReadLineAsync(lines, CancellationToken.None).GetAwaiter().GetResult() == bootstrap &&
            LocalWifiFixtureLab.ReadLineAsync(lines, CancellationToken.None).GetAwaiter().GetResult() == Command());
        check("LOHS fixture CLI accepts fixed lifetime mode", Options.Parse(["--local-wifi-fixture-lab"]).Mode == "local-wifi-fixture-lab");
        bool RejectOptions(string[] args) { try { Options.Parse(args); return false; } catch (ArgumentException) { return true; } }
        check("LOHS fixture CLI refuses caller duration override", RejectOptions(["--local-wifi-fixture-lab", "--seconds", "60"]));
    }

    private static async Task ReverseRolesAsync(Action<string, bool> check, bool wrongPin)
    {
        using var receiverIdentity = P0Identity.Load();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Wanwei local fixture host test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        CryptographicOperations.ZeroMemory(pfx);
        using var senderIdentity = P0Identity.ForHostTest(new X509Certificate2(certificate));
        string knownRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "LocalWifiFixtureSelfTests"));
        string folder = Path.GetFullPath(Path.Combine(knownRoot, Guid.NewGuid().ToString("N")));
        P0Storage.EnsureSafeDirectory(folder);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = new P0Path(IPAddress.Loopback, IPAddress.Loopback, "loopback-host-test", () => true, true);
        P0TransferStatus? tx = null, rx = null;
        try
        {
            var sender = new P0Transfer(senderIdentity, Path.Combine(folder, "server-tx"), status => {
                tx = status; if (status.State == "listening") ready.TrySetResult(status.SocketLocalPort!.Value);
            });
            Task sending = sender.RunAsync(false, path, receiverIdentity.Fingerprint, 0,
                (_, _) => throw new InvalidOperationException("Sender cannot accept an offer"), stop.Token,
                connectionMode: P0ConnectionMode.InboundSenderHostTest);
            int port = await ready.Task.WaitAsync(stop.Token);
            var receiver = new P0Transfer(receiverIdentity, Path.Combine(folder, "client-rx"), status => rx = status);
            Task receiving = receiver.RunAsync(true, path, wrongPin ? new string('0', 64) : senderIdentity.Fingerprint, port,
                (_, _) => Task.FromResult(true), stop.Token, operatorFixturePreauthorized: true,
                connectionMode: P0ConnectionMode.OutboundReceiver);
            await Task.WhenAll(sending, receiving);
            if (wrongPin)
            {
                check("LOHS role inversion wrong full pin prevents receiving any file bytes", rx is { ErrorCode: "TLS_AUTHENTICATION_FAILED", Bytes: 0, LocalSaved: false } && tx?.PeerSaveConfirmed == false);
            }
            else
            {
                check("LOHS outbound file receiver retains production TLS1.3 and saved fixture", rx is not null && LocalWifiFixtureLab.IsSuccessful(rx) && rx is { TcpRole: "client", TlsRole: "client", OperatorFixturePreauthorized: true, ReceiverUserAccepted: false });
                check("LOHS inbound file sender retains HELLO sender role and peer COMPLETE", tx is not null && LocalWifiFixtureLab.IsSuccessful(tx) && tx is { TcpRole: "server", TlsRole: "server" });
                check("LOHS host role test cannot claim WiFi Direct or actual radio", rx is { TransportKind: "host_test_loopback", EndpointSource: "host_test_loopback_no_radio_no_Direct_claim" });
                string saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0Received", rx!.SavedLocalName!);
                check("LOHS outbound receiver committed disk file independently hashes correctly", File.Exists(saved) && new FileInfo(saved).Length == P0Protocol.Size && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(saved))) == P0Protocol.Hash);
            }
            check("LOHS reversed TLS role closes sockets and partial files " + wrongPin, rx is { SocketClosed: true, TemporaryFileCleanupConfirmed: true, CleanupFinished: true } && tx is { SocketClosed: true, TemporaryFileCleanupConfirmed: true, CleanupFinished: true });
            P0TransferStatus? denied = null;
            var nonLabPath = new P0Path(IPAddress.Parse("192.168.49.2"), IPAddress.Parse("192.168.49.1"), "no-native-adapter", () => true);
            await new P0Transfer(receiverIdentity, Path.Combine(folder, "denied-mode"), status => denied = status)
                .RunAsync(true, nonLabPath, senderIdentity.Fingerprint, port, (_, _) => Task.FromResult(true), stop.Token,
                    connectionMode: P0ConnectionMode.OutboundReceiver);
            check("LOHS file role inversion is forbidden for ordinary Direct paths " + wrongPin, denied is { ErrorCode: "CONNECTION_MODE_FORBIDDEN", Bytes: 0 });
        }
        finally
        {
            stop.Cancel();
            if (rx?.SavedLocalName is { } name)
                File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0Received", name));
            if (!folder.StartsWith(knownRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("TEST_CLEANUP_PATH_REJECTED");
            P0Storage.RejectReparse(folder); Directory.Delete(folder, true);
        }
    }
}
