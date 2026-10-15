using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wanwei;

// These datagrams are unauthenticated port hints. Only the existing pinned
// mutual TLS connection can authenticate the peer or authorize an OFFER.
internal sealed record P0RendezvousMessage(string Type, string Nonce, string SenderIdentity, string ReceiverIdentity, int? Port);

internal static class P0Rendezvous
{
    internal const string Protocol = "wanwei-rendezvous/0.1";
    internal const int ControlPort = 45873;
    internal const int MaximumDatagramBytes = 1024;
    internal const int ResolveMilliseconds = 10_000;
    internal const int QueryIntervalMilliseconds = 500;
    internal const int MaximumQueries = 20;
    internal const int RespondMilliseconds = 30_000;
    internal const int ResponseIntervalMilliseconds = 100;
    internal const int MaximumResponses = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static P0RendezvousMessage Parse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            if (payload.Length is < 1 or > MaximumDatagramBytes ||
                (payload.Length >= 3 && payload.Span[0] == 0xef && payload.Span[1] == 0xbb && payload.Span[2] == 0xbf))
                throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
            _ = StrictUtf8.GetString(payload.Span);
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 2, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject())
                if (!fields.TryAdd(field.Name, field.Value)) throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
            string Text(string key)
            {
                if (!fields.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String) throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
                return value.GetString()!;
            }
            string type = Text("type");
            string[] keys = type switch {
                "QUERY" => ["protocol", "type", "nonce", "senderIdentity", "receiverIdentity"],
                "ANSWER" => ["protocol", "type", "nonce", "senderIdentity", "receiverIdentity", "port"],
                _ => throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE")
            };
            if (fields.Count != keys.Length || keys.Any(key => !fields.ContainsKey(key)) || Text("protocol") != Protocol)
                throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
            string nonce = Text("nonce"), sender = Text("senderIdentity"), receiver = Text("receiverIdentity");
            if (!P0Protocol.IsTransferId(nonce) || !P0Protocol.IsFingerprint(sender) || !P0Protocol.IsFingerprint(receiver) || sender == receiver)
                throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
            int? port = null;
            if (type == "ANSWER")
            {
                JsonElement value = fields["port"];
                string raw = value.GetRawText();
                if (value.ValueKind != JsonValueKind.Number || raw.Length is < 1 or > 5 || raw.Any(c => c is < '0' or > '9') ||
                    !value.TryGetInt32(out int number) || number is < 1 or > 65535)
                    throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
                port = number;
            }
            return new(type, nonce, sender, receiver, port);
        }
        catch (P0Exception) { throw; }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException or FormatException)
        { throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE"); }
    }

    internal static byte[] Query(string nonce, string sender, string receiver) =>
        JsonSerializer.SerializeToUtf8Bytes(new { protocol = Protocol, type = "QUERY", nonce, senderIdentity = sender, receiverIdentity = receiver });
    internal static byte[] Answer(P0RendezvousMessage query, int port) =>
        JsonSerializer.SerializeToUtf8Bytes(new { protocol = Protocol, type = "ANSWER", nonce = query.Nonce,
            senderIdentity = query.SenderIdentity, receiverIdentity = query.ReceiverIdentity, port });

    internal static bool QueryMatches(P0RendezvousMessage message, IPEndPoint source, P0Path path, string localPin, string peerPin) =>
        message.Type == "QUERY" && source.Address.Equals(path.Remote) && source.Port is >= 1 and <= 65535 &&
        message.SenderIdentity == peerPin && message.ReceiverIdentity == localPin;
    internal static bool AnswerMatches(P0RendezvousMessage message, IPEndPoint source, P0Path path, string nonce, string localPin, string peerPin) =>
        message.Type == "ANSWER" && source.Address.Equals(path.Remote) && source.Port == ControlPort && message.Nonce == nonce &&
        message.SenderIdentity == localPin && message.ReceiverIdentity == peerPin && message.Port is >= 1 and <= 65535;
    internal static bool MayQuery(long elapsedMilliseconds, int sent) => elapsedMilliseconds is >= 0 and < ResolveMilliseconds && sent is >= 0 and < MaximumQueries;
    internal static bool MayRespond(long elapsedMilliseconds, int sent, long lastResponseMilliseconds) =>
        elapsedMilliseconds is >= 0 and < RespondMilliseconds && sent is >= 0 and < MaximumResponses && elapsedMilliseconds - lastResponseMilliseconds >= ResponseIntervalMilliseconds;
    internal static bool IsUnicastAddress(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is > 0 and < 224;

    internal static void VerifyContext(P0Path path, string localPin, string peerPin)
    {
        if (!P0Protocol.IsFingerprint(localPin) || !P0Protocol.IsFingerprint(peerPin) || localPin == peerPin) throw new P0Exception("IDENTITY_MISMATCH");
        bool addresses = IsUnicastAddress(path.Local) && IsUnicastAddress(path.Remote) &&
            (path.LoopbackHostTest ? IPAddress.IsLoopback(path.Local) && IPAddress.IsLoopback(path.Remote) :
                DirectPairing.ValidEndpoint(new(path.Local.ToString(), "", path.Remote.ToString(), "")));
        if (!addresses || !path.Verify()) throw new P0Exception("DIRECT_PATH_UNVERIFIED");
    }

    internal static async Task<int> ResolveAsync(P0Path path, string localPin, string peerPin, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        VerifyContext(path, localPin, peerPin);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ResolveMilliseconds);
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = true;
        socket.Bind(new IPEndPoint(path.Local, 0));
        using var closeOnCancel = deadline.Token.Register(() => { try { socket.Dispose(); } catch { } });
        var clock = Stopwatch.StartNew();
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        byte[] query = Query(nonce, localPin, peerPin), buffer = new byte[MaximumDatagramBytes + 1];
        var destination = new IPEndPoint(path.Remote, ControlPort);
        int sent = 0;
        long nextQuery = 0;
        try
        {
            while (!deadline.IsCancellationRequested && clock.ElapsedMilliseconds < ResolveMilliseconds)
            {
                VerifyContext(path, localPin, peerPin);
                if (clock.ElapsedMilliseconds >= nextQuery && MayQuery(clock.ElapsedMilliseconds, sent))
                {
                    await socket.SendToAsync(query, SocketFlags.None, destination, deadline.Token).ConfigureAwait(false);
                    sent++; nextQuery = clock.ElapsedMilliseconds + QueryIntervalMilliseconds;
                }
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                long remaining = ResolveMilliseconds - clock.ElapsedMilliseconds;
                poll.CancelAfter((int)Math.Max(1, Math.Min(100, Math.Min(remaining, Math.Max(1, nextQuery - clock.ElapsedMilliseconds)))));
                SocketReceiveFromResult packet;
                try { packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(path.Remote, 0), poll.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
                VerifyContext(path, localPin, peerPin);
                if (packet.RemoteEndPoint is not IPEndPoint source || packet.ReceivedBytes > MaximumDatagramBytes) continue;
                P0RendezvousMessage answer;
                try { answer = Parse(buffer.AsMemory(0, packet.ReceivedBytes)); } catch (P0Exception) { continue; }
                deadline.Token.ThrowIfCancellationRequested();
                if (clock.ElapsedMilliseconds >= ResolveMilliseconds) throw new P0Exception("RENDEZVOUS_TIMEOUT");
                if (AnswerMatches(answer, source, path, nonce, localPin, peerPin)) return answer.Port!.Value;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException && deadline.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            throw new P0Exception("RENDEZVOUS_TIMEOUT");
        }
        cancellation.ThrowIfCancellationRequested();
        throw new P0Exception("RENDEZVOUS_TIMEOUT");
    }
}

internal sealed class P0RendezvousResponder : IAsyncDisposable
{
    private readonly Socket socket;
    private readonly P0Path path;
    private readonly string localPin, peerPin;
    private readonly int port;
    private readonly CancellationToken callerCancellation;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenSource stopped = new();
    private readonly CancellationTokenRegistration closeOnCancel;
    private readonly Task worker;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int closeAttempted, disposeStarted, sourceDisposed;
    private bool socketClosed;
    internal string? FailureCode { get; private set; }
    internal CancellationToken Stopped => stopped.Token;
    internal bool ResourceReleaseConfirmed => socketClosed && worker.IsCompleted;

    private P0RendezvousResponder(P0Path path, string localPin, string peerPin, int port, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        P0Rendezvous.VerifyContext(path, localPin, peerPin);
        if (port is < 1 or > 65535) throw new P0Exception("INVALID_RENDEZVOUS_MESSAGE");
        this.path = path; this.localPin = localPin; this.peerPin = peerPin; this.port = port;
        callerCancellation = cancellation;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lifetime.CancelAfter(P0Rendezvous.RespondMilliseconds);
        socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try { socket.Bind(new IPEndPoint(path.Local, P0Rendezvous.ControlPort)); }
        catch
        {
            socket.Dispose(); lifetime.Dispose(); stopped.Dispose();
            throw new P0Exception("RENDEZVOUS_UNAVAILABLE");
        }
        closeOnCancel = lifetime.Token.Register(CloseSocket);
        worker = RespondAsync();
    }

    internal static P0RendezvousResponder Start(P0Path path, string localPin, string peerPin, int port, CancellationToken cancellation) =>
        new(path, localPin, peerPin, port, cancellation);

    private void CloseSocket()
    {
        if (Interlocked.Exchange(ref closeAttempted, 1) != 0) return;
        try { socket.Dispose(); socketClosed = true; } catch { socketClosed = false; }
    }
    private async Task RespondAsync()
    {
        byte[] buffer = new byte[P0Rendezvous.MaximumDatagramBytes + 1];
        int responses = 0;
        long lastResponse = -P0Rendezvous.ResponseIntervalMilliseconds;
        try
        {
            while (!lifetime.IsCancellationRequested && clock.ElapsedMilliseconds < P0Rendezvous.RespondMilliseconds && responses < P0Rendezvous.MaximumResponses)
            {
                P0Rendezvous.VerifyContext(path, localPin, peerPin);
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                poll.CancelAfter(100);
                SocketReceiveFromResult packet;
                try { packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(path.Remote, 0), poll.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { continue; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize) { continue; }
                P0Rendezvous.VerifyContext(path, localPin, peerPin);
                if (packet.RemoteEndPoint is not IPEndPoint source || packet.ReceivedBytes > P0Rendezvous.MaximumDatagramBytes) continue;
                P0RendezvousMessage query;
                try { query = P0Rendezvous.Parse(buffer.AsMemory(0, packet.ReceivedBytes)); } catch (P0Exception) { continue; }
                if (!P0Rendezvous.QueryMatches(query, source, path, localPin, peerPin) ||
                    !P0Rendezvous.MayRespond(clock.ElapsedMilliseconds, responses, lastResponse)) continue;
                byte[] answer = P0Rendezvous.Answer(query, port);
                await socket.SendToAsync(answer, SocketFlags.None, source, lifetime.Token).ConfigureAwait(false);
                responses++; lastResponse = clock.ElapsedMilliseconds;
            }
            if (!callerCancellation.IsCancellationRequested && !lifetime.IsCancellationRequested) FailureCode = "RENDEZVOUS_LIMIT_REACHED";
        }
        catch (P0Exception e) { FailureCode = e.Code; }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException && lifetime.IsCancellationRequested)
        { if (!callerCancellation.IsCancellationRequested && Volatile.Read(ref disposeStarted) == 0) FailureCode = "RENDEZVOUS_TIMEOUT"; }
        catch { FailureCode = "RENDEZVOUS_FAILED"; }
        finally { CloseSocket(); stopped.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) == 0) { lifetime.Cancel(); CloseSocket(); }
        try { await worker.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch { socketClosed = false; _ = worker.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        if (worker.IsCompleted && Interlocked.Exchange(ref sourceDisposed, 1) == 0)
        { closeOnCancel.Dispose(); lifetime.Dispose(); stopped.Dispose(); }
    }
}
