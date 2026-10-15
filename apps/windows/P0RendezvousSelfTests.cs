using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Wanwei;

internal static class P0RendezvousSelfTests
{
    // Host tests use a different loopback address from the Android host tests.
    // No adapter discovery, wireless API, system setting, or firewall mutation.
    private static readonly IPAddress SenderAddress = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress ReceiverAddress = IPAddress.Parse("127.0.0.2");
    private static readonly string SenderPin = new('a', 64), ReceiverPin = new('b', 64);
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static P0Path Path(bool receiver, Func<bool>? connected = null) => new(receiver ? ReceiverAddress : SenderAddress,
        receiver ? SenderAddress : ReceiverAddress, "loopback-control-test-no-native-claim", connected ?? (() => true), true);
    private static Socket Udp(IPAddress address, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try { socket.Bind(new IPEndPoint(address, port)); return socket; } catch { socket.Dispose(); throw; }
    }

    internal static void Run(Action<string, bool> check)
    {
        byte[] query = P0Rendezvous.Query(Nonce, SenderPin, ReceiverPin);
        var message = P0Rendezvous.Parse(query);
        string queryText = Encoding.UTF8.GetString(query);
        byte[] answer = P0Rendezvous.Answer(message, 49152);
        string answerText = Encoding.UTF8.GetString(answer);
        bool Reject(byte[] bytes) { try { P0Rendezvous.Parse(bytes); return false; } catch (P0Exception e) { return e.Code == "INVALID_RENDEZVOUS_MESSAGE"; } }
        bool RejectText(string text) => Reject(Encoding.UTF8.GetBytes(text));
        check("rendezvous query exact schema", message is { Type: "QUERY", Nonce: Nonce, Port: null } && message.SenderIdentity == SenderPin && message.ReceiverIdentity == ReceiverPin);
        check("rendezvous answer preserves nonce and both directional identities", P0Rendezvous.Parse(answer) == message with { Type = "ANSWER", Port = 49152 });
        check("rendezvous 1024 byte bound includes valid whitespace", P0Rendezvous.Parse(Encoding.UTF8.GetBytes(queryText.PadRight(1024))).Type == "QUERY");
        check("rendezvous oversized datagram rejected", Reject(Encoding.UTF8.GetBytes(queryText.PadRight(1025))));
        check("rendezvous empty datagram rejected", Reject([]));
        check("rendezvous invalid UTF8 rejected", Reject([0xc3, 0x28]));
        check("rendezvous BOM rejected", RejectText("\uFEFF" + queryText));
        check("rendezvous duplicate fields rejected", RejectText(queryText[..^1] + ",\"type\":\"QUERY\"}"));
        check("rendezvous unknown fields rejected", RejectText(queryText[..^1] + ",\"address\":\"8.8.8.8\"}"));
        check("rendezvous query cannot include a port", RejectText(queryText[..^1] + ",\"port\":49152}"));
        check("rendezvous missing field rejected", RejectText(queryText.Replace(",\"nonce\":\"" + Nonce + "\"", "")));
        check("rendezvous unknown version rejected", RejectText(queryText.Replace("/0.1", "/0.2")));
        check("rendezvous unknown type rejected", RejectText(queryText.Replace("QUERY", "HELLO")));
        check("rendezvous trailing JSON rejected", RejectText(queryText + "{}"));
        check("rendezvous wrong root rejected", RejectText("[" + queryText + "]"));
        check("rendezvous nonce length rejected", RejectText(queryText.Replace(Nonce, Nonce[..^1])));
        check("rendezvous uppercase nonce rejected", RejectText(queryText.Replace(Nonce, Nonce.ToUpperInvariant())));
        check("rendezvous identity must be full lowercase SPKI", RejectText(queryText.Replace(SenderPin, SenderPin.ToUpperInvariant())) && RejectText(queryText.Replace(SenderPin, SenderPin[..^1])));
        check("rendezvous identical identities rejected", RejectText(queryText.Replace(SenderPin, ReceiverPin)));
        foreach (string value in new[] { "0", "-0", "-1", "65536", "49152.0", "4.9152e4", "\"49152\"", "null", "true" })
            check("rendezvous strict port token " + value, RejectText(answerText.Replace("49152", value)));
        check("rendezvous port endpoints accepted", P0Rendezvous.Parse(Encoding.UTF8.GetBytes(answerText.Replace("49152", "1"))).Port == 1 && P0Rendezvous.Parse(Encoding.UTF8.GetBytes(answerText.Replace("49152", "65535"))).Port == 65535);

        var receiverPath = Path(true); var senderPath = Path(false);
        var expectedSource = new IPEndPoint(ReceiverAddress, P0Rendezvous.ControlPort);
        var parsedAnswer = P0Rendezvous.Parse(answer);
        check("rendezvous query accepts only selected native peer and confirmed pins", P0Rendezvous.QueryMatches(message, new(SenderAddress, 54321), receiverPath, ReceiverPin, SenderPin));
        check("rendezvous query rejects wrong source IP", !P0Rendezvous.QueryMatches(message, new(ReceiverAddress, 54321), receiverPath, ReceiverPin, SenderPin));
        check("rendezvous query rejects unconfirmed identities", !P0Rendezvous.QueryMatches(message with { SenderIdentity = new('c', 64) }, new(SenderAddress, 54321), receiverPath, ReceiverPin, SenderPin));
        check("rendezvous answer binds both pins and nonce", P0Rendezvous.AnswerMatches(parsedAnswer, expectedSource, senderPath, Nonce, SenderPin, ReceiverPin));
        check("rendezvous answer source port exact", !P0Rendezvous.AnswerMatches(parsedAnswer, new(ReceiverAddress, P0Rendezvous.ControlPort + 1), senderPath, Nonce, SenderPin, ReceiverPin));
        check("rendezvous answer source address exact", !P0Rendezvous.AnswerMatches(parsedAnswer, new(SenderAddress, P0Rendezvous.ControlPort), senderPath, Nonce, SenderPin, ReceiverPin));
        check("rendezvous stale nonce rejected", !P0Rendezvous.AnswerMatches(parsedAnswer, expectedSource, senderPath, new('0', 32), SenderPin, ReceiverPin));
        check("rendezvous reversed pin directions rejected", !P0Rendezvous.AnswerMatches(parsedAnswer, expectedSource, senderPath, Nonce, ReceiverPin, SenderPin));
        check("rendezvous query cannot serve as answer", !P0Rendezvous.AnswerMatches(message, expectedSource, senderPath, Nonce, SenderPin, ReceiverPin));
        check("rendezvous resolve deadline and packet cap", P0Rendezvous.MayQuery(9999, 19) && !P0Rendezvous.MayQuery(10_000, 19) && !P0Rendezvous.MayQuery(100, 20) && !P0Rendezvous.MayQuery(-1, 0));
        check("rendezvous responder deadline and packet cap", P0Rendezvous.MayRespond(29_999, 63, 29_899) && !P0Rendezvous.MayRespond(30_000, 63, 29_800) && !P0Rendezvous.MayRespond(100, 64, 0));
        check("rendezvous responder rate bound", !P0Rendezvous.MayRespond(99, 1, 0) && P0Rendezvous.MayRespond(100, 1, 0));
        bool ContextRejected(P0Path path)
        {
            try { P0Rendezvous.VerifyContext(path, SenderPin, ReceiverPin); return false; } catch (P0Exception e) { return e.Code == "DIRECT_PATH_UNVERIFIED"; }
        }
        check("rendezvous refuses invalid native path", ContextRejected(Path(false, () => false)));
        check("rendezvous refuses unspecified bind", ContextRejected(new(IPAddress.Any, ReceiverAddress, "not-native", () => true, true)));
        check("rendezvous refuses IPv6 control path", ContextRejected(new(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, "not-native", () => true, true)));
        check("rendezvous never sends control packets to multicast or limited broadcast", !P0Rendezvous.IsUnicastAddress(IPAddress.Parse("224.0.0.1")) && !P0Rendezvous.IsUnicastAddress(IPAddress.Broadcast));
        check("rendezvous never binds unspecified or zero-network address", !P0Rendezvous.IsUnicastAddress(IPAddress.Any) && !P0Rendezvous.IsUnicastAddress(IPAddress.Parse("0.1.2.3")));
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            bool cancelled = false;
            try { P0Rendezvous.ResolveAsync(senderPath, SenderPin, ReceiverPin, stop.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            check("rendezvous pre-cancel touches no socket", cancelled);
        }
        ResponderAsync(check).GetAwaiter().GetResult();
        ResolverFiltersAsync(check).GetAwaiter().GetResult();
        LifecycleAsync(check).GetAwaiter().GetResult();
        ResolverTimeoutAsync(check).GetAwaiter().GetResult();
    }

    private static async Task<bool> HasPacketAsync(Socket socket, int milliseconds)
    {
        using var budget = new CancellationTokenSource(milliseconds);
        try { await socket.ReceiveFromAsync(new byte[2048], SocketFlags.None, new IPEndPoint(ReceiverAddress, 0), budget.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task ResponderAsync(Action<string, bool> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var receiver = P0RendezvousResponder.Start(Path(true), ReceiverPin, SenderPin, 49152, stop.Token);
        try
        {
            using var client = Udp(SenderAddress, 0);
            var target = new IPEndPoint(ReceiverAddress, P0Rendezvous.ControlPort);
            await client.SendToAsync(P0Rendezvous.Query(Nonce, new('c', 64), ReceiverPin), SocketFlags.None, target, stop.Token);
            check("rendezvous responder ignores unexpected peer pin on UDP", !await HasPacketAsync(client, 150));
            await client.SendToAsync(new byte[1025], SocketFlags.None, target, stop.Token);
            check("rendezvous responder ignores oversized UDP", !await HasPacketAsync(client, 150));
            await client.SendToAsync(P0Rendezvous.Query(Nonce, SenderPin, ReceiverPin), SocketFlags.None, target, stop.Token);
            byte[] buffer = new byte[1025];
            var response = await client.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(ReceiverAddress, 0), stop.Token);
            var answer = P0Rendezvous.Parse(buffer.AsMemory(0, response.ReceivedBytes));
            check("rendezvous native-address-only responder returns ready TCP port on host loopback", answer.Port == 49152 && P0Rendezvous.AnswerMatches(answer, (IPEndPoint)response.RemoteEndPoint, Path(false), Nonce, SenderPin, ReceiverPin));
        }
        finally { await receiver.DisposeAsync(); }
        check("rendezvous responder closes socket and worker", receiver.ResourceReleaseConfirmed);
        using var rebound = Udp(ReceiverAddress, P0Rendezvous.ControlPort);
        check("rendezvous responder port immediately reusable after close", rebound.LocalEndPoint is IPEndPoint { Port: P0Rendezvous.ControlPort });
        bool unavailable = false;
        try { _ = P0RendezvousResponder.Start(Path(true), ReceiverPin, SenderPin, 49152, stop.Token); }
        catch (P0Exception e) { unavailable = e.Code == "RENDEZVOUS_UNAVAILABLE"; }
        check("rendezvous fixed-port conflict is explicit failure", unavailable);
    }

    private static async Task ResolverFiltersAsync(Action<string, bool> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var server = Udp(ReceiverAddress, P0Rendezvous.ControlPort);
        IPEndPoint? observedSender = null;
        Task response = Task.Run(async () => {
            byte[] buffer = new byte[1025];
            var packet = await server.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(SenderAddress, 0), stop.Token);
            observedSender = (IPEndPoint)packet.RemoteEndPoint;
            var query = P0Rendezvous.Parse(buffer.AsMemory(0, packet.ReceivedBytes));
            await server.SendToAsync(P0Rendezvous.Answer(query with { Nonce = new('0', 32) }, 49151), SocketFlags.None, observedSender, stop.Token);
            await server.SendToAsync(P0Rendezvous.Answer(query with { ReceiverIdentity = new('c', 64) }, 49152), SocketFlags.None, observedSender, stop.Token);
            using var wrongPort = Udp(ReceiverAddress, 0);
            await wrongPort.SendToAsync(P0Rendezvous.Answer(query, 49153), SocketFlags.None, observedSender, stop.Token);
            using var wrongAddress = Udp(IPAddress.Parse("127.0.0.3"), P0Rendezvous.ControlPort);
            await wrongAddress.SendToAsync(P0Rendezvous.Answer(query, 49154), SocketFlags.None, observedSender, stop.Token);
            await server.SendToAsync(new byte[1025], SocketFlags.None, observedSender, stop.Token);
            await server.SendToAsync(P0Rendezvous.Answer(query, 49167), SocketFlags.None, observedSender, stop.Token);
        });
        int port = await P0Rendezvous.ResolveAsync(Path(false), SenderPin, ReceiverPin, stop.Token);
        await response;
        check("rendezvous resolver ignores forged pin nonce source and oversized hints on host loopback", port == 49167 && observedSender?.Address.Equals(SenderAddress) == true);
        using var rebound = Udp(SenderAddress, observedSender!.Port);
        check("rendezvous resolver closes ephemeral socket before returning", ((IPEndPoint)rebound.LocalEndPoint!).Port == observedSender.Port);
    }

    private static async Task LifecycleAsync(Action<string, bool> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int connected = 1;
        var receiver = P0RendezvousResponder.Start(Path(true, () => Volatile.Read(ref connected) == 1), ReceiverPin, SenderPin, 49152, stop.Token);
        CancellationToken stopped = receiver.Stopped;
        Volatile.Write(ref connected, 0);
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, stopped))
        { try { await Task.Delay(Timeout.Infinite, wait.Token); } catch (OperationCanceledException) { } }
        await receiver.DisposeAsync();
        check("rendezvous invalidated native path stops idle responder", stopped.IsCancellationRequested && receiver.FailureCode == "DIRECT_PATH_UNVERIFIED" && receiver.ResourceReleaseConfirmed);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        receiver = P0RendezvousResponder.Start(Path(true), ReceiverPin, SenderPin, 49152, cancel.Token);
        cancel.Cancel();
        await receiver.DisposeAsync(); await receiver.DisposeAsync();
        check("rendezvous cancellation and repeated disposal release fixed UDP port", receiver.ResourceReleaseConfirmed);
        using var server = Udp(ReceiverAddress, P0Rendezvous.ControlPort);
        using var resolveStop = new CancellationTokenSource(120);
        bool cancelled = false;
        try { await P0Rendezvous.ResolveAsync(Path(false), SenderPin, ReceiverPin, resolveStop.Token); } catch (OperationCanceledException) { cancelled = true; }
        check("rendezvous resolver user cancellation remains cancellation", cancelled);
    }

    private static async Task ResolverTimeoutAsync(Action<string, bool> check)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var server = Udp(ReceiverAddress, P0Rendezvous.ControlPort);
        var receivedAt = new List<long>();
        var clock = Stopwatch.StartNew();
        string? nonce = null; bool sameNonce = true;
        Task drain = Task.Run(async () => {
            byte[] buffer = new byte[1025];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await server.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(SenderAddress, 0), stop.Token);
                    var query = P0Rendezvous.Parse(buffer.AsMemory(0, packet.ReceivedBytes));
                    nonce ??= query.Nonce; sameNonce &= query.Nonce == nonce;
                    receivedAt.Add(clock.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        bool timedOut = false;
        try { await P0Rendezvous.ResolveAsync(Path(false), SenderPin, ReceiverPin, stop.Token); }
        catch (P0Exception e) { timedOut = e.Code == "RENDEZVOUS_TIMEOUT"; }
        long elapsed = clock.ElapsedMilliseconds;
        stop.Cancel(); await drain;
        check("rendezvous missing receiver expires at ten second absolute deadline", timedOut && elapsed is >= 9500 and < 11_500);
        check("rendezvous retries reuse nonce and respect twenty packet cap", sameNonce && receivedAt.Count is > 0 and <= P0Rendezvous.MaximumQueries);
        check("rendezvous retry interval never catches up with packet bursts", receivedAt.Zip(receivedAt.Skip(1), (first, second) => second - first).All(gap => gap >= 450));
    }
}
