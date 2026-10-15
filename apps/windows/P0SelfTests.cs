using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Wanwei;

internal static class P0SelfTests
{
    internal static void Run(Action<string, bool> check)
    {
        byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        bool Reject(string json, string code = "INVALID_MESSAGE")
        {
            try { P0Protocol.Parse(Bytes(json)); return false; } catch (P0Exception e) { return e.Code == code; }
        }
        const string id = "0123456789abcdef0123456789abcdef";
        string offer = Encoding.UTF8.GetString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(P0Protocol.Offer(id)));
        check("P0 valid fixed OFFER", P0Protocol.Parse(Bytes(offer)).TransferId == id);
        P0Protocol.Require(P0Protocol.Parse(Bytes(offer)), "OFFER");
        check("P0 initial OFFER ID is adopted only after validation", true);
        foreach (string token in new[] { "-0", "1048576.0", "1.048576e6", "\"1048576\"", "4294967296", "-1" })
            check("P0 strict size token " + token, Reject(offer.Replace("1048576", token)));
        check("P0 duplicate keys rejected", Reject(offer.Replace("\"type\":\"OFFER\"", "\"type\":\"OFFER\",\"type\":\"OFFER\"")));
        check("P0 remote path is forbidden", Reject(offer[..^1] + ",\"path\":\"../escape\"}"));
        check("P0 unknown message rejected", Reject("{\"type\":\"DATA_END\"}"));
        check("P0 BOM rejected", Reject("\uFEFF" + offer));
        check("P0 wrong fixture rejected", Reject(offer.Replace(P0Protocol.Fixture, "another")));
        string hello = "{\"type\":\"HELLO\",\"version\":\"0.1\",\"role\":\"sender\",\"service\":\"file.p0\",\"identity\":\"" + new string('a', 64) + "\"}";
        check("P0 version boundary", Reject(hello.Replace("0.1", "0.2"), "UNSUPPORTED_VERSION"));
        check("P0 media service boundary", Reject(hello.Replace("file.p0", "media.p0"), "UNSUPPORTED_SERVICE"));
        bool Error(Action action, string code) { try { action(); return false; } catch (P0Exception e) { return e.Code == code; } }
        check("P0 HELLO bound to authenticated SPKI", Error(() => P0Protocol.VerifyHello(P0Protocol.Parse(Bytes(hello)), "sender", new string('b', 64)), "IDENTITY_MISMATCH"));
        check("P0 HELLO direction exact", Error(() => P0Protocol.VerifyHello(P0Protocol.Parse(Bytes(hello)), "receiver", new string('a', 64)), "UNEXPECTED_MESSAGE"));
        var accept = P0Protocol.Parse(Bytes("{\"type\":\"ACCEPT\",\"transferId\":\"" + id + "\"}"));
        check("P0 transfer ID cannot cross tasks", Error(() => P0Protocol.Require(accept, "ACCEPT", new string('b', 32)), "UNEXPECTED_MESSAGE"));
        check("P0 wrong state rejects ACCEPT as HELLO", Error(() => P0Protocol.Require(accept, "HELLO"), "UNEXPECTED_MESSAGE"));
        foreach (var vector in new[] { (Kind: (byte)0, Size: 1u), (Kind: (byte)3, Size: 1u), (Kind: (byte)1, Size: 0u), (Kind: (byte)1, Size: 4097u), (Kind: (byte)2, Size: 65537u), (Kind: (byte)2, Size: uint.MaxValue) })
            check("P0 frame length/kind rejected " + vector, Error(() => P0Protocol.ValidateFrameHeader(vector.Kind, vector.Size), "INVALID_FRAME"));
        byte[] all = new byte[P0Protocol.Size]; for (int i = 0; i < all.Length; i++) all[i] = (byte)i;
        check("P0 exact 1 MiB fixture hash", Convert.ToHexStringLower(SHA256.HashData(all)) == P0Protocol.Hash);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0SelfTests", Guid.NewGuid().ToString("N"));
        P0Storage.EnsureSafeDirectory(directory);
        string? saved = null;
        try
        {
            using (var storage = new P0Storage(directory)) { for (int i = 0; i < 16; i++) storage.Write(P0Protocol.RampChunk()); storage.Commit(); saved = storage.SavedLocalName; }
            check("P0 synchronize and commit exact fixture", saved is not null && File.Exists(Path.Combine(directory, saved)) && new FileInfo(Path.Combine(directory, saved)).Length == P0Protocol.Size);
            using (var storage = new P0Storage(directory)) { storage.Write([1, 2, 3]); check("P0 truncated storage cannot commit", Error(storage.Commit, "SIZE_MISMATCH")); }
            check("P0 unsuccessful storage removes own partial", !Directory.EnumerateFiles(directory, "*.partial").Any());
            using (var storage = new P0Storage(directory)) { storage.Write(new byte[P0Protocol.Size]); check("P0 incorrect content cannot commit", Error(storage.Commit, "HASH_MISMATCH")); }
            using (var storage = new P0Storage(directory)) { storage.Write(all); check("P0 overflow rejected before addition", Error(() => storage.Write([1]), "SIZE_MISMATCH")); }
            check("P0 failure preserves prior committed file", saved is not null && File.Exists(Path.Combine(directory, saved)));
        }
        finally
        {
            if (saved is not null) File.Delete(Path.Combine(directory, saved));
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        using var key = RSA.Create(2048);
        using var cert = TestCertificate(key);
        string pin = P0Identity.FingerprintOf(cert);
        check("P0 correct SPKI self-signed policy", P0Identity.ValidatePeer(cert, pin));
        check("P0 mismatched SPKI rejected", !P0Identity.ValidatePeer(cert, new string('0', 64)));
        check("P0 missing TLS peer certificate rejected", !P0Identity.ValidatePeer(null, pin));
        using var expired = TestCertificate(key, true);
        check("P0 expired certificate rejected", !P0Identity.ValidatePeer(expired, P0Identity.FingerprintOf(expired)));
        AssociationPolicy(check, cert);
        using var key2 = RSA.Create(2048); using var peer = TestCertificate(key2);
        check("P0 TLS1.3 mutual pinned handshake loopback", TlsHandshake(cert, peer, false).GetAwaiter().GetResult());
        check("P0 TLS pin failure aborts handshake loopback", !TlsHandshake(cert, peer, true).GetAwaiter().GetResult());
        using var installed = P0Identity.Load();
        check("P0 persisted DPAPI identity server Schannel handshake", TlsHandshake(installed.Certificate, peer, false).GetAwaiter().GetResult());
        check("P0 persisted DPAPI identity client Schannel handshake", TlsHandshake(peer, installed.Certificate, false).GetAwaiter().GetResult());
        TransferLoopbackAsync(check, installed, peer, false).GetAwaiter().GetResult();
        TransferLoopbackAsync(check, installed, peer, true).GetAwaiter().GetResult();
        TransferLoopbackAsync(check, installed, peer, false, true).GetAwaiter().GetResult();
        DataBeforeAcceptAsync(check, installed, peer).GetAwaiter().GetResult();
        EarlyCompleteAsync(check, installed, peer).GetAwaiter().GetResult();
        FramingAsync(check).GetAwaiter().GetResult();
        var deadline = new PairingDeadlines(60, 180); deadline.Start(0); deadline.TrySelectRequest(1); deadline.TryObserveConnection(2);
        check("P0 opt-in observation has fixed 180 second boundary", deadline.ObservationBudgetMs == 180_000 && deadline.MaximumRunMs == 285_000 && !deadline.TryObserveConnection(3));
        UsbLabPolicy(check);
    }
    private static void AssociationPolicy(Action<string, bool> check, X509Certificate2 certificate)
    {
        const string address = "12:34:56:78:9a:bc";
        check("AEP query canonicalizes a selected device address", AepAssociationPolicy.NormalizeDeviceAddress("12-34-56-78-9A-BC") == address);
        check("AEP query has one exact validated address selector", AepAssociationPolicy.Selector(address) == "System.Devices.Aep.DeviceAddress:=\"12:34:56:78:9a:bc\"");
        string?[] invalid = [null, "", "000000000000", "00:00:00:00:00:00", "02:00:00:00:00:00", "ff:ff:ff:ff:ff:ff", "13:34:56:78:9a:bc", "12:34-56:78:9a:bc", address + "\n", address + "\r", address + "\"", address + "\\", "12:34:56:78:9a:gg", address + " OR System.Devices.Aep.IsPaired:=false"];
        for (int i = 0; i < invalid.Length; i++) check("AEP malformed/default/injected address rejected " + i,
            AepAssociationPolicy.NormalizeDeviceAddress(invalid[i]) is null && AepAssociationPolicy.Selector(invalid[i]) is null);
        var matched = new OsAssociationEvidence("matched", true, true, false, 10);
        check("AEP association can avoid duplicate pairing when request snapshot is false", AepAssociationPolicy.SkipFreshPairing(false, matched));
        check("AEP lookup never rewrites request IsPaired snapshot", new PairingSessionResult("not_run", false, false, false, "not_created", true, true, 0, 0, 0, 0,
            null, false, "ExistingSystemAssociationObserved", false, false, "not_created", [], [], null) { OsAssociationCheck = matched } is { RequestIsPaired: false, AlreadyPaired: false, OsAssociationCheck.OsAssociationObserved: true, ApplicationIdentity: "not_authenticated" });
        foreach (string status in new[] { "not_observed", "device_address_unavailable", "invalid_device_address", "query_failed", "timed_out", "cancelled", "returned_address_mismatch" })
            check("AEP unknown or failed lookup still requires fresh pairing " + status, !AepAssociationPolicy.SkipFreshPairing(false, new(status, status == "not_observed" ? false : null, status == "not_observed", status == "timed_out", 0)));
        check("AEP unvalidated match cannot skip fresh pairing", !AepAssociationPolicy.SkipFreshPairing(false, matched with { DeviceAddressValidated = false }));
        check("AEP pending or unknown result cannot skip fresh pairing", !AepAssociationPolicy.SkipFreshPairing(null, matched with { QueryStatus = "timed_out", OsAssociationObserved = null, LateReadPendingAtReturn = true }));
        check("AEP original paired request preserves existing reconnection policy", AepAssociationPolicy.SkipFreshPairing(true, new("not_queried_request_already_paired", null, false, false, 0)));
        check("AEP returned device address must agree when available", !AepAssociationPolicy.ReturnedDeviceMatches(address, true, "12:34:56:78:9a:bd") && AepAssociationPolicy.ReturnedDeviceMatches(address, true, "12-34-56-78-9A-BC"));
        check("AEP returned address with wrong type is refused", !AepAssociationPolicy.ReturnedDeviceMatches(address, true, 123));
        check("AEP exact-selector result tolerates unavailable secondary property", AepAssociationPolicy.ReturnedDeviceMatches(address, false, null));
        check("AEP association never replaces application TLS pin verification", AepAssociationPolicy.SkipFreshPairing(false, matched) && !P0Identity.ValidatePeer(certificate, new string('0', 64)));
        var zeroBudget = AepAssociationProbe.RunAsync("not-a-device-no-native-call", 0, CancellationToken.None).GetAwaiter().GetResult();
        check("AEP exhausted negotiation budget touches no native query", zeroBudget.QueryStatus == "no_remaining_negotiation_budget" && !AepAssociationPolicy.SkipFreshPairing(false, zeroBudget));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledQuery = AepAssociationProbe.RunAsync("not-a-device-no-native-call", 3000, cancelled.Token).GetAwaiter().GetResult();
        check("AEP pre-cancelled lookup touches no native query or pairing decision", cancelledQuery.QueryStatus == "cancelled" && !AepAssociationPolicy.SkipFreshPairing(false, cancelledQuery));
        var deadlines = new PairingDeadlines(60); deadlines.Start(0); deadlines.TrySelectRequest(59_000);
        long originalCutoff = deadlines.DeadlineElapsedMs!.Value;
        check("AEP lookup completion does not reset the original 45 second negotiation cutoff", !deadlines.TrySelectRequest(62_000) && !deadlines.Start(62_000) && deadlines.DeadlineElapsedMs == originalCutoff && originalCutoff == 104_000);
    }
    private static void UsbLabPolicy(Action<string, bool> check)
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        string bootstrap = "{\"sessionNonce\":\"" + nonce + "\",\"peerPin\":\"" + new string('a', 64) + "\",\"fixtureConsent\":true,\"fixture\":\"" + P0Protocol.Fixture + "\",\"role\":\"receiver\"}";
        byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        bool Reject(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }
        var boot = P0UsbLabBootstrap.Parse(Bytes(bootstrap));
        check("P0 USB lab exact fixture authorization accepted", boot.SessionNonce == nonce && boot.PeerPin == new string('a', 64));
        check("P0 USB lab missing affirmative fixture consent rejected", Reject(() => P0UsbLabBootstrap.Parse(Bytes(bootstrap.Replace("true", "false")))));
        check("P0 USB lab arbitrary fixture rejected", Reject(() => P0UsbLabBootstrap.Parse(Bytes(bootstrap.Replace(P0Protocol.Fixture, "user-file")))));
        check("P0 USB lab peer pin uppercase rejected", Reject(() => P0UsbLabBootstrap.Parse(Bytes(bootstrap.Replace(new string('a', 64), new string('A', 64))))));
        check("P0 USB lab unknown bootstrap field rejected", Reject(() => P0UsbLabBootstrap.Parse(Bytes(bootstrap[..^1] + ",\"autoTrustAll\":true}"))));
        check("P0 USB lab duplicate bootstrap field rejected", Reject(() => P0UsbLabBootstrap.Parse(Bytes(bootstrap[..^1] + ",\"fixtureConsent\":true}"))));
        check("P0 USB lab oversized bootstrap rejected", Reject(() => P0UsbLabBootstrap.Parse(new byte[16385])));
        var macRestricted = P0UsbLabBootstrap.Parse(Bytes(bootstrap[..^1] + ",\"expectedPhoneP2pMac\":\"12:34:56:78:9a:bc\"}"));
        check("P0 USB lab native bounded MAC token recognized", macRestricted.AllowsNativePeer("WiFiDirect#12:34:56:78:9A:BC#interface", null));
        check("P0 USB lab mismatched native MAC refused", !macRestricted.AllowsNativePeer("WiFiDirect#12:34:56:78:9A:BD#interface", null));
        check("P0 USB lab GUID fragments cannot synthesize a MAC", !macRestricted.AllowsNativePeer("guid-12345678-9abc-abcd-ef1234567890", null));
        string command = "{\"sessionNonce\":\"" + nonce + "\",\"commandId\":\"fedcba9876543210fedcba9876543210\",\"action\":\"send\",\"port\":49152}";
        check("P0 USB lab command is port-only sender", P0UsbLabCommand.Parse(Bytes(command), nonce).Port == 49152);
        check("P0 USB lab mismatched nonce refused", Reject(() => P0UsbLabCommand.Parse(Bytes(command), new string('0', 32))));
        check("P0 USB lab arbitrary IP refused", Reject(() => P0UsbLabCommand.Parse(Bytes(command[..^1] + ",\"peerAddress\":\"8.8.8.8\"}"), nonce)));
        check("P0 USB lab arbitrary path refused", Reject(() => P0UsbLabCommand.Parse(Bytes(command[..^1] + ",\"path\":\"private-file\"}"), nonce)));
        foreach (string invalidPort in new[] { "0", "65536", "49152.0", "4.9152e4", "\"49152\"" })
            check("P0 USB lab strict port " + invalidPort, Reject(() => P0UsbLabCommand.Parse(Bytes(command.Replace("49152", invalidPort)), nonce)));
        check("P0 USB lab arbitrary action refused", Reject(() => P0UsbLabCommand.Parse(Bytes(command.Replace("send", "delete")), nonce)));
        check("P0 normal options have no USB lab bootstrap", Options.Parse([]).LabBootstrapPath is null);
        check("P0 explicit USB lab options default bounded 60 second wait", Options.Parse(["--usb-p0-lab", "bootstrap.json"]) is { Mode: "usb-p0-lab", Seconds: 60, LabBootstrapPath: not null });
        check("P0 USB lab cannot overlap probe mode", Reject(() => Options.Parse(["--probe", "--usb-p0-lab", "bootstrap.json"])));
        var deadline = new PairingDeadlines(60, 600); deadline.Start(10); deadline.TrySelectRequest(11); deadline.TryObserveConnection(12);
        check("P0 USB lab fixed native 600 second observation", deadline.ObservationBudgetMs == 600_000 && deadline.MaximumRunMs == 705_000 && deadline.DeadlineElapsedMs == 600_012 && !deadline.TryObserveConnection(13));
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0SelfTests"));
        string directory = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N"))); P0Storage.EnsureSafeDirectory(directory);
        try
        {
            boot.ConsumeAuthorization(directory);
            bool consumedAgain = false; try { boot.ConsumeAuthorization(directory); } catch (IOException) { consumedAgain = true; }
            check("P0 USB bootstrap consumed once before native operation", consumedAgain);
        }
        finally
        {
            if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("TEST_CLEANUP_PATH_REJECTED");
            P0Storage.RejectReparse(directory); Directory.Delete(directory, true);
        }
    }
    private static async Task DataBeforeAcceptAsync(Action<string, bool> check, P0Identity receiverIdentity, X509Certificate2 clientCertificate)
    {
        string knownRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0SelfTests"));
        string folder = Path.Combine(knownRoot, Guid.NewGuid().ToString("N")); P0Storage.EnsureSafeDirectory(folder);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); P0TransferStatus? rx = null;
        try
        {
            var receiver = new P0Transfer(receiverIdentity, folder, s => { rx = s; if (s.State == "listening") ready.TrySetResult(s.SocketLocalPort!.Value); });
            var path = new P0Path(IPAddress.Loopback, IPAddress.Loopback, "loopback-host-test", () => true, true);
            Task receiving = receiver.RunAsync(true, path, P0Identity.FingerprintOf(clientCertificate), 0, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return true; }, stop.Token);
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, await ready.Task.WaitAsync(stop.Token), stop.Token);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => P0Identity.ValidatePeer(cert, receiverIdentity.Fingerprint));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "wanwei-p0", ClientCertificates = new X509CertificateCollection { clientCertificate }, LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate, EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false, AllowRenegotiation = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, stop.Token);
            var wire = new P0Wire(ssl, () => true, stop.Token);
            await wire.ControlAsync(P0Protocol.Hello("sender", P0Identity.FingerprintOf(clientCertificate)));
            P0Protocol.VerifyHello(await wire.ControlAsync(2), "receiver", receiverIdentity.Fingerprint);
            await wire.ControlAsync(P0Protocol.Offer("0123456789abcdef0123456789abcdef"));
            await wire.WriteAsync(2, [1, 2, 3]);
            var error = await wire.ControlAsync(2);
            await receiving;
            check("P0 DATA before user ACCEPT rejected on authenticated production loopback", error is { Type: "ERROR" } && error.Text("code") == "UNEXPECTED_MESSAGE" && rx is { State: "failed", ErrorCode: "UNEXPECTED_MESSAGE", Bytes: 0, LocalSaved: false, ReceiverUserAccepted: false, SocketClosed: true, TemporaryFileCleanupConfirmed: true });
        }
        finally
        {
            stop.Cancel();
            string resolved = Path.GetFullPath(folder);
            if (!resolved.StartsWith(knownRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("TEST_CLEANUP_PATH_REJECTED");
            P0Storage.RejectReparse(resolved); Directory.Delete(resolved, true);
        }
    }
    private static async Task EarlyCompleteAsync(Action<string, bool> check, P0Identity senderIdentity, X509Certificate2 serverCertificate)
    {
        string knownRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0SelfTests"));
        string folder = Path.Combine(knownRoot, Guid.NewGuid().ToString("N")); P0Storage.EnsureSafeDirectory(folder);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); P0TransferStatus? tx = null;
        try
        {
            Task fakeReceiver = Task.Run(async () => {
                using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => P0Identity.ValidatePeer(cert, senderIdentity.Fingerprint));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCertificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false, AllowRenegotiation = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, stop.Token);
                var wire = new P0Wire(ssl, () => true, stop.Token);
                P0Protocol.VerifyHello(await wire.ControlAsync(2), "sender", senderIdentity.Fingerprint);
                await wire.ControlAsync(P0Protocol.Hello("receiver", P0Identity.FingerprintOf(serverCertificate)));
                var offer = await wire.ControlAsync(2); P0Protocol.Require(offer, "OFFER");
                // Coalesce ACCEPT and the premature COMPLETE into one TLS write
                // so the sender observes the invalid order deterministically.
                using var bytes = new MemoryStream(); var frames = new P0Wire(bytes, () => true, stop.Token);
                await frames.ControlAsync(new { type = "ACCEPT", transferId = offer.TransferId }); await frames.ControlAsync(P0Protocol.Complete(offer.TransferId!));
                await ssl.WriteAsync(bytes.ToArray(), stop.Token); await ssl.FlushAsync(stop.Token);
                try { await wire.ReadAsync(2); } catch (Exception e) when (e is IOException or OperationCanceledException) { }
            });
            var sender = new P0Transfer(senderIdentity, folder, s => tx = s);
            await sender.RunAsync(false, new(IPAddress.Loopback, IPAddress.Loopback, "loopback-host-test", () => true, true), P0Identity.FingerprintOf(serverCertificate), ((IPEndPoint)listener.LocalEndpoint).Port, (_, _) => throw new InvalidOperationException(), stop.Token);
            await fakeReceiver;
            check("P0 COMPLETE before final DATA rejected on authenticated production loopback", tx is { State: "failed", ErrorCode: "UNEXPECTED_MESSAGE", PeerSaveConfirmed: false, SocketClosed: true, TemporaryFileCleanupConfirmed: true });
        }
        finally
        {
            stop.Cancel(); string resolved = Path.GetFullPath(folder);
            if (!resolved.StartsWith(knownRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("TEST_CLEANUP_PATH_REJECTED");
            P0Storage.RejectReparse(resolved); Directory.Delete(resolved, true);
        }
    }
    private static async Task TransferLoopbackAsync(Action<string, bool> check, P0Identity receiverIdentity, X509Certificate2 peerCertificate, bool decline, bool preauthorized = false)
    {
        // Exercise the production TLS/framing/save state machine on loopback.
        // This factory is used only by --self-test; the GUI always requires
        // native Direct endpoint pairs and cannot select loopback.
        using var peerIdentity = P0Identity.ForHostTest(new X509Certificate2(peerCertificate));
        // The X509Certificate2 copy keeps the private key reference on Windows.
        string knownRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0SelfTests"));
        string folder = Path.GetFullPath(Path.Combine(knownRoot, Guid.NewGuid().ToString("N")));
        P0Storage.EnsureSafeDirectory(folder);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var portReady = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = new P0Path(IPAddress.Loopback, IPAddress.Loopback, "loopback-host-test", () => true, true);
        P0TransferStatus? rx = null, tx = null;
        try
        {
            var receiver = new P0Transfer(receiverIdentity, Path.Combine(folder, "rx"), status => { rx = status; if (status.State == "listening") portReady.TrySetResult(status.SocketLocalPort!.Value); });
            Task receiving = receiver.RunAsync(true, path, peerIdentity.Fingerprint, 0, (_, _) => Task.FromResult(!decline), stop.Token, operatorFixturePreauthorized: preauthorized);
            int port = await portReady.Task.WaitAsync(stop.Token);
            var sender = new P0Transfer(peerIdentity, Path.Combine(folder, "tx"), status => tx = status);
            Task sending = sender.RunAsync(false, path, receiverIdentity.Fingerprint, port, (_, _) => throw new InvalidOperationException("Sender cannot approve receiving"), stop.Token);
            await Task.WhenAll(receiving, sending);
            if (decline)
            {
                check("P0 explicit receive refusal prevents file data on TLS loopback", rx is { State: "rejected", Bytes: 0, LocalSaved: false, ReceiverUserAccepted: false } && tx is { State: "rejected", Bytes: 0, PeerSaveConfirmed: false });
            }
            else
            {
                check("P0 production TLS transfer saves exact fixture loopback " + preauthorized, rx is { State: "complete", Bytes: P0Protocol.Size, LocalSaved: true, Sha256: P0Protocol.Hash } && rx.ReceiverUserAccepted == !preauthorized && rx.OperatorFixturePreauthorized == preauthorized);
                check("P0 production sender confirms COMPLETE loopback", tx is { State: "complete", Bytes: P0Protocol.Size, PeerSaveConfirmed: true, Sha256: P0Protocol.Hash });
            }
            check("P0 loopback resources and partial file cleanup confirmed " + decline, rx is { SocketClosed: true, TemporaryFileCleanupConfirmed: true } && tx is { SocketClosed: true, TemporaryFileCleanupConfirmed: true });
            if (preauthorized) check("P0 USB fixture preauthorization never claims receiver button click", rx is { OperatorFixturePreauthorized: true, ReceiverUserAccepted: false } && rx.PreauthorizationSource == "explicit_cli_local_USB_bootstrap_fixed_1MiB_fixture");
        }
        finally
        {
            stop.Cancel();
            if (rx?.SavedLocalName is { } localName) File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0Received", localName));
            // Only this random test directory was created here; resolve and
            // verify the deletion target under the dedicated host-test root.
            if (!folder.StartsWith(knownRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("TEST_CLEANUP_PATH_REJECTED");
            P0Storage.RejectReparse(folder); Directory.Delete(folder, true);
        }
    }
    private static X509Certificate2 TestCertificate(RSA key, bool expired = false)
    {
        var request = new CertificateRequest("CN=Wanwei local host test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    private static async Task<bool> TlsHandshake(X509Certificate2 server, X509Certificate2 client, bool wrongPin)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<bool> accept = Task.Run(async () => {
            try
            {
                using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => P0Identity.ValidatePeer(cert, P0Identity.FingerprintOf(client)));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = server, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false, AllowRenegotiation = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, stop.Token);
                return ssl.IsMutuallyAuthenticated && ssl.SslProtocol == SslProtocols.Tls13 && P0Identity.ValidatePeer(ssl.RemoteCertificate, P0Identity.FingerprintOf(client));
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException) { return false; }
        });
        bool clientOk;
        try
        {
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => P0Identity.ValidatePeer(cert, wrongPin ? new string('0', 64) : P0Identity.FingerprintOf(server)));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "wanwei-p0", ClientCertificates = new X509CertificateCollection { client }, LocalCertificateSelectionCallback = (_, _, _, _, _) => client, EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false, AllowRenegotiation = false, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, stop.Token);
            clientOk = ssl.IsMutuallyAuthenticated && ssl.SslProtocol == SslProtocols.Tls13;
        }
        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException) { clientOk = false; }
        return await accept && clientOk;
    }
    private static async Task FramingAsync(Action<string, bool> check)
    {
        using var input = new FragmentedStream([1, 0, 0, 0, 2, (byte)'{', (byte)'}', 2, 0, 0, 0, 3, 1, 2, 3]);
        var wire = new P0Wire(input, () => true, CancellationToken.None);
        var one = await wire.ReadAsync(1); var two = await wire.ReadAsync(1);
        check("P0 partial reads and merged frames preserved", one.Kind == 1 && one.Payload.Length == 2 && two.Kind == 2 && two.Payload.SequenceEqual(new byte[] { 1, 2, 3 }));
        using var truncated = new MemoryStream([2, 0, 0, 0, 4, 1]);
        try { await new P0Wire(truncated, () => true, CancellationToken.None).ReadAsync(1); check("P0 truncated frame rejected", false); }
        catch (EndOfStreamException) { check("P0 truncated frame rejected", true); }
        using var timeout = new BlockingStream();
        try { await new P0Wire(timeout, () => true, CancellationToken.None).ReadUntilAsync(1); check("P0 no first-byte progress times out", false); }
        catch (P0Exception e) { check("P0 no first-byte progress times out", e.Code == "TIMEOUT"); }
        try { await new P0Wire(input, () => false, CancellationToken.None).ReadAsync(1); check("P0 lost native path cannot read", false); }
        catch (P0Exception e) { check("P0 lost native path cannot read", e.Code == "DIRECT_PATH_UNVERIFIED"); }
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
    private sealed class BlockingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
