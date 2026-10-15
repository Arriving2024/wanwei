using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Wanwei;

internal sealed record P0Path(IPAddress Local, IPAddress Remote, string InterfaceId, Func<bool> StillConnected, bool LoopbackHostTest = false)
{
    internal static P0Path Create(IReadOnlyList<DirectEndpointEvidence> pairs, Func<bool> stillConnected)
    {
        var candidates = pairs.Where(DirectPairing.ValidEndpoint).Select(p => new {
            Local = IPAddress.Parse(p.LocalAddress), Remote = IPAddress.Parse(p.RemoteAddress)
        }).Where(p => p.Local.AddressFamily == AddressFamily.InterNetwork).DistinctBy(p => (p.Local, p.Remote)).ToArray();
        if (candidates.Length != 1) throw new P0Exception("DIRECT_PATH_UNVERIFIED");
        var selected = candidates[0];
        var adapter = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SingleOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(selected.Local)));
        if (adapter is null || !stillConnected()) throw new P0Exception("DIRECT_PATH_UNVERIFIED");
        return new(selected.Local, selected.Remote, adapter.Id, stillConnected);
    }
    internal bool Verify()
    {
        if (LoopbackHostTest) return IPAddress.IsLoopback(Local) && IPAddress.IsLoopback(Remote) && StillConnected();
        try { return StillConnected() && NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Id == InterfaceId &&
            n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(Local))); }
        catch { return false; }
    }
}

internal sealed class P0TransferStatus
{
    public string Schema { get; } = "wanwei.p0.transfer/1";
    public string AppVersion { get; } = "0.3.2";
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public string Role { get; set; } = "not_started";
    public string State { get; set; } = "idle";
    public string LocalIdentity { get; set; } = "";
    public string TrustedPeerIdentity { get; set; } = "";
    public string? AuthenticatedPeerIdentity { get; set; }
    public string? TlsProtocol { get; set; }
    public string? CipherSuite { get; set; }
    public string? NativeLocalAddress { get; set; }
    public string? NativeRemoteAddress { get; set; }
    public string? InterfaceId { get; set; }
    public string? SocketLocalAddress { get; set; }
    public int? SocketLocalPort { get; set; }
    public string? SocketRemoteAddress { get; set; }
    public int? SocketRemotePort { get; set; }
    public bool PeerTrustExplicitlyConfirmed { get; set; }
    public bool ReceiverUserAccepted { get; set; }
    public bool OperatorFixturePreauthorized { get; set; }
    public string PreauthorizationSource { get; set; } = "none";
    public string? TransferId { get; set; }
    public ulong Bytes { get; set; }
    public string? Sha256 { get; set; }
    public bool LocalSaved { get; set; }
    public bool PeerSaveConfirmed { get; set; }
    public string? SavedLocalName { get; set; }
    public string? ErrorCode { get; set; }
    public bool SocketClosed { get; set; }
    public bool TemporaryFileCleanupConfirmed { get; set; }
    public bool CleanupFinished { get; set; }
    public string PortExchange { get; set; } = "manual_out_of_band";
    public bool? RendezvousControlClosed { get; set; }
    public bool SessionResumptionAllowed { get; } = false;
    public string EndpointSource { get; set; } = "native_WiFiDirectDevice_and_actual_bound_socket";
    public string InternetContinuity { get; } = "not_tested";
}

internal sealed class P0Transfer(P0Identity identity, string reportDirectory, Action<P0TransferStatus> update)
{
    internal async Task RunAsync(bool receiver, P0Path path, string peerPin, int remotePort,
        Func<string, CancellationToken, Task<bool>> acceptOffer, CancellationToken cancellation, bool operatorFixturePreauthorized = false,
        Func<int, CancellationToken, P0RendezvousResponder>? listenerReady = null, string portExchange = "manual_out_of_band")
    {
        var status = new P0TransferStatus { Role = receiver ? "receiver" : "sender", LocalIdentity = identity.Fingerprint,
            TrustedPeerIdentity = peerPin, PeerTrustExplicitlyConfirmed = true, NativeLocalAddress = path.Local.ToString(),
            NativeRemoteAddress = path.Remote.ToString(), InterfaceId = path.InterfaceId, PortExchange = portExchange };
        status.OperatorFixturePreauthorized = operatorFixturePreauthorized;
        status.PreauthorizationSource = operatorFixturePreauthorized ? "explicit_cli_local_USB_bootstrap_fixed_1MiB_fixture" : "none";
        if (path.LoopbackHostTest) status.EndpointSource = "host_test_loopback_no_radio_no_Direct_claim";
        void Publish(string state)
        {
            status.State = state;
            Directory.CreateDirectory(reportDirectory);
            string json = JsonSerializer.Serialize(status, ReportStore.Json);
            string temporaryReport = Path.Combine(reportDirectory, "latest-transfer.json.tmp");
            File.WriteAllText(temporaryReport, json, new UTF8Encoding(false));
            File.Move(temporaryReport, Path.Combine(reportDirectory, "latest-transfer.json"), true);
            update(status);
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Socket? socket = null; SslStream? tls = null; P0Wire? wire = null;
        P0RendezvousResponder? rendezvous = null;
        P0Storage? storage = null;
        Task? pendingWrite = null;
        Task<P0Message>? pendingRead = null;
        Task<P0Frame>? pendingFrame = null;
        try
        {
            if (!P0Protocol.IsFingerprint(peerPin) || (!receiver && remotePort is < 1 or > 65535)) throw new P0Exception("INVALID_MESSAGE");
            if (!path.Verify()) throw new P0Exception("DIRECT_PATH_UNVERIFIED");
            using var establishment = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            establishment.CancelAfter(TimeSpan.FromSeconds(30));
            if (receiver)
            {
                listener.ExclusiveAddressUse = true; listener.Bind(new IPEndPoint(path.Local, 0)); listener.Listen(1);
                var local = (IPEndPoint)listener.LocalEndPoint!; status.SocketLocalAddress = local.Address.ToString(); status.SocketLocalPort = local.Port;
                if (listenerReady is not null)
                {
                    // Synchronous binding happens before reporting readiness. A failed
                    // control bind stops this receiver; it never falls back to a LAN.
                    rendezvous = listenerReady(local.Port, establishment.Token);
                    status.RendezvousControlClosed = false;
                }
                Publish("listening");
                using var acceptDeadline = rendezvous is null ? null : CancellationTokenSource.CreateLinkedTokenSource(establishment.Token, rendezvous.Stopped);
                try { socket = await listener.AcceptAsync(acceptDeadline?.Token ?? establishment.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!establishment.IsCancellationRequested && rendezvous?.FailureCode is not null)
                { throw new P0Exception(rendezvous.FailureCode); }
                finally
                {
                    if (rendezvous is not null)
                    {
                        // Stop advertising as soon as Accept completes, before TLS or
                        // the OFFER prompt. UDP never accepts an application file.
                        try { await rendezvous.DisposeAsync().ConfigureAwait(false); status.RendezvousControlClosed = rendezvous.ResourceReleaseConfirmed; }
                        catch { status.RendezvousControlClosed = false; }
                    }
                }
                if (status.RendezvousControlClosed == false) throw new P0Exception("RENDEZVOUS_CLEANUP_UNCONFIRMED");
                listener.Close();
            }
            else
            {
                socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(path.Local, 0)); Publish("connecting");
                await socket.ConnectAsync(new IPEndPoint(path.Remote, remotePort), establishment.Token).ConfigureAwait(false);
            }
            using var closeOnCancel = lifetime.Token.Register(() => { try { socket.Dispose(); } catch { } });
            var localSocket = (IPEndPoint)socket.LocalEndPoint!; var remoteSocket = (IPEndPoint)socket.RemoteEndPoint!;
            if (!localSocket.Address.Equals(path.Local) || !remoteSocket.Address.Equals(path.Remote) || !path.Verify()) throw new P0Exception("DIRECT_PATH_UNVERIFIED");
            status.SocketLocalAddress = localSocket.Address.ToString(); status.SocketLocalPort = localSocket.Port;
            status.SocketRemoteAddress = remoteSocket.Address.ToString(); status.SocketRemotePort = remoteSocket.Port;
            socket.NoDelay = true;
            tls = new SslStream(new NetworkStream(socket, false), false, (_, cert, _, _) => P0Identity.ValidatePeer(cert, peerPin));
            Publish("tls_authenticating");
            if (receiver)
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false, AllowRenegotiation = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, establishment.Token).ConfigureAwait(false);
            else
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                    TargetHost = "wanwei-p0", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => identity.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false, AllowRenegotiation = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, establishment.Token).ConfigureAwait(false);
            if (!tls.IsMutuallyAuthenticated || tls.SslProtocol != SslProtocols.Tls13 || !P0Identity.ValidatePeer(tls.RemoteCertificate, peerPin)) throw new P0Exception("TLS_AUTHENTICATION_FAILED");
            lifetime.CancelAfter(TimeSpan.FromSeconds(180));
            status.AuthenticatedPeerIdentity = peerPin; status.TlsProtocol = tls.SslProtocol.ToString(); status.CipherSuite = tls.NegotiatedCipherSuite.ToString();
            wire = new(tls, path.Verify, lifetime.Token); Publish("authenticated");
            long helloDeadline = wire.Deadline(10);
            if (receiver)
            {
                var hello = await wire.ControlUntilAsync(helloDeadline).ConfigureAwait(false); P0Protocol.VerifyHello(hello, "sender", peerPin);
                await wire.ControlUntilAsync(P0Protocol.Hello("receiver", identity.Fingerprint), helloDeadline).ConfigureAwait(false);
                var offer = await wire.ControlAsync(10).ConfigureAwait(false); P0Protocol.Require(offer, "OFFER"); status.TransferId = offer.TransferId;
                Publish("awaiting_receive_confirmation");
                using var userDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); userDeadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(120_000, wire.RemainingMs)));
                bool authorizedToReceive = false;
                bool frameStartedAfterAccept = false;
                object receiveGate = new();
                using var firstFrameTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); firstFrameTimeout.CancelAfter(TimeSpan.FromSeconds(120));
                var firstFrameWire = new P0Wire(tls, path.Verify, firstFrameTimeout.Token, kind => {
                    lock (receiveGate)
                    {
                        frameStartedAfterAccept = authorizedToReceive;
                        if (!authorizedToReceive && kind == 2) throw new P0Exception("UNEXPECTED_MESSAGE");
                    }
                });
                async Task<P0Frame> AwaitNextFrameAsync()
                {
                    var frame = await firstFrameWire.ReadAsync(180).ConfigureAwait(false);
                    lock (receiveGate)
                    {
                        if (!frameStartedAfterAccept)
                        {
                            if (frame.Kind == 1) { var early = P0Protocol.Parse(frame.Payload); P0Protocol.Require(early, "NO_MESSAGE_ALLOWED", status.TransferId); }
                            throw new P0Exception("UNEXPECTED_MESSAGE");
                        }
                    }
                    return frame;
                }
                pendingFrame = AwaitNextFrameAsync();
                Task<bool> userDecision = acceptOffer(peerPin, userDeadline.Token);
                bool accepted;
                try
                {
                    if (await Task.WhenAny(userDecision, pendingFrame).ConfigureAwait(false) == pendingFrame)
                    {
                        userDeadline.Cancel();
                        try { await userDecision.ConfigureAwait(false); } catch (OperationCanceledException) { }
                        _ = await pendingFrame.ConfigureAwait(false); throw new P0Exception("UNEXPECTED_MESSAGE");
                    }
                    accepted = await userDecision.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { throw new P0Exception("TIMEOUT"); }
                if (!accepted)
                {
                    await wire.ControlAsync(new { type = "REJECT", transferId = status.TransferId, reason = "USER_DECLINED" }).ConfigureAwait(false); Publish("rejected"); return;
                }
                status.ReceiverUserAccepted = !operatorFixturePreauthorized;
                try { storage = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "P0Received")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    await wire.ControlAsync(new { type = "REJECT", transferId = status.TransferId, reason = "POLICY" }).ConfigureAwait(false); Publish("storage_rejected"); return;
                }
                lock (receiveGate)
                {
                    // A frame fully read before this transition was received
                    // without ACCEPT and must never become file data.
                    if (pendingFrame.IsCompleted) { _ = pendingFrame.GetAwaiter().GetResult(); throw new P0Exception("UNEXPECTED_MESSAGE"); }
                    authorizedToReceive = true;
                    firstFrameTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                }
                await wire.ControlAsync(new { type = "ACCEPT", transferId = status.TransferId }).ConfigureAwait(false); Publish("receiving");
                while (status.Bytes < P0Protocol.Size)
                {
                    var frame = pendingFrame is not null ? await pendingFrame.ConfigureAwait(false) : await wire.ReadAsync(30).ConfigureAwait(false);
                    pendingFrame = null;
                    if (frame.Kind != 2) { var control = P0Protocol.Parse(frame.Payload); P0Protocol.Require(control, "DATA", status.TransferId); }
                    if ((ulong)frame.Payload.Length > (ulong)P0Protocol.Size - status.Bytes) throw new P0Exception("SIZE_MISMATCH");
                    try { storage.Write(frame.Payload); } catch (Exception e) when (e is IOException or UnauthorizedAccessException && e is not P0Exception) { throw new P0Exception("STORAGE_FAILED"); }
                    status.Bytes += (ulong)frame.Payload.Length;
                }
                wire.VerifyPath();
                try { storage.Commit(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException && e is not P0Exception) { throw new P0Exception("STORAGE_FAILED"); }
                status.LocalSaved = true; status.SavedLocalName = storage.SavedLocalName; status.Sha256 = P0Protocol.Hash;
                Publish("local_saved");
                await wire.ControlAsync(P0Protocol.Complete(status.TransferId!)).ConfigureAwait(false); Publish("complete");
            }
            else
            {
                await wire.ControlUntilAsync(P0Protocol.Hello("sender", identity.Fingerprint), helloDeadline).ConfigureAwait(false);
                var hello = await wire.ControlUntilAsync(helloDeadline).ConfigureAwait(false); P0Protocol.VerifyHello(hello, "receiver", peerPin);
                status.TransferId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                await wire.ControlAsync(P0Protocol.Offer(status.TransferId)).ConfigureAwait(false); Publish("awaiting_peer_acceptance");
                var decision = await wire.ControlAsync(120).ConfigureAwait(false);
                if (decision.Type == "REJECT") { P0Protocol.Require(decision, "REJECT", status.TransferId); Publish("rejected"); return; }
                P0Protocol.Require(decision, "ACCEPT", status.TransferId); Publish("sending");
                // Read ERROR/COMPLETE concurrently with writes, allowing an early
                // peer error to stop further DATA without waiting for all bytes.
                using var progressTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); progressTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                Task<P0Message> response = wire.TerminalWithProgressAsync(progressTimeout.Token); pendingRead = response;
                byte[] chunk = P0Protocol.RampChunk();
                for (int i = 0; i < 16; i++)
                {
                    if (response.IsCompleted) { var early = await response.ConfigureAwait(false); P0Protocol.Require(early, "ERROR", status.TransferId); throw new P0Exception("UNEXPECTED_MESSAGE"); }
                    Task write = wire.WriteAsync(2, chunk); pendingWrite = write;
                    if (await Task.WhenAny(write, response).ConfigureAwait(false) == response && !write.IsCompleted)
                    {
                        var early = await response.ConfigureAwait(false);
                        if (i < 15) { P0Protocol.Require(early, "ERROR", status.TransferId); throw new P0Exception("UNEXPECTED_MESSAGE"); }
                        // The receiver may finish while the final FlushAsync is
                        // completing. ERROR still aborts immediately; COMPLETE
                        // is validated now and adopted only after that write.
                        P0Protocol.Require(early, "COMPLETE", status.TransferId);
                    }
                    await write.ConfigureAwait(false); pendingWrite = null; status.Bytes += 65536; progressTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                }
                Publish("waiting_saved_confirmation");
                var complete = await response.ConfigureAwait(false); P0Protocol.Require(complete, "COMPLETE", status.TransferId);
                status.PeerSaveConfirmed = true; status.Sha256 = P0Protocol.Hash; Publish("complete");
            }
        }
        catch (Exception e)
        {
            string code = e switch {
                P0Exception pe => pe.Code,
                OperationCanceledException when cancellation.IsCancellationRequested => "CANCELLED",
                OperationCanceledException => "TIMEOUT",
                AuthenticationException => "TLS_AUTHENTICATION_FAILED",
                IOException when cancellation.IsCancellationRequested => "CANCELLED",
                SocketException when cancellation.IsCancellationRequested => "CANCELLED",
                _ => "CONNECTION_FAILED"
            };
            status.ErrorCode = code;
            // SslStream permits one writer. If a DATA write is unsettled, close
            // directly instead of racing a terminal ERROR with that writer.
            if (wire is not null && pendingWrite is null && code is not ("TLS_AUTHENTICATION_FAILED" or "CONNECTION_FAILED")) await wire.ErrorAsync(status.TransferId, code).ConfigureAwait(false);
            Publish(code == "CANCELLED" ? "cancelled" : "failed");
        }
        finally
        {
            if (rendezvous is not null)
            {
                try { await rendezvous.DisposeAsync().ConfigureAwait(false); status.RendezvousControlClosed = rendezvous.ResourceReleaseConfirmed; }
                catch { status.RendezvousControlClosed = false; }
                if (status.RendezvousControlClosed == false) status.ErrorCode ??= "RENDEZVOUS_CLEANUP_UNCONFIRMED";
            }
            try { storage?.Dispose(); status.TemporaryFileCleanupConfirmed = storage?.CleanupConfirmed ?? true; }
            catch { status.TemporaryFileCleanupConfirmed = false; status.ErrorCode ??= "STORAGE_CLEANUP_UNCONFIRMED"; }
            bool socketsClosed = status.RendezvousControlClosed != false;
            try { tls?.Dispose(); } catch { socketsClosed = false; }
            try { socket?.Dispose(); } catch { socketsClosed = false; }
            try { listener.Dispose(); } catch { socketsClosed = false; }
            status.SocketClosed = socketsClosed;
            status.CleanupFinished = true;
            if (!status.SocketClosed || !status.TemporaryFileCleanupConfirmed) status.State = "cleanup_unconfirmed";
            try { if (pendingWrite is not null) await pendingWrite.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            try { if (pendingRead is not null) await pendingRead.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            try { if (pendingFrame is not null) await pendingFrame.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            Publish(status.State);
            string pathForReport = Path.Combine(reportDirectory, "transfer-" + status.RunId + ".json");
            File.WriteAllText(pathForReport, JsonSerializer.Serialize(status, ReportStore.Json), new UTF8Encoding(false));
        }
    }
}
