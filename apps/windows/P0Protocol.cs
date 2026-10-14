using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wanwei;

internal sealed class P0Exception(string code) : IOException(code)
{
    internal string Code { get; } = code;
}

internal sealed record P0Frame(byte Kind, byte[] Payload);
internal sealed record P0Message(string Type, string? TransferId, Dictionary<string, JsonElement> Fields)
{
    internal string Text(string name) => Fields[name].GetString()!;
}

internal static class P0Protocol
{
    internal const int Size = 1_048_576;
    internal const string Hash = "fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83";
    internal const string Fixture = "ramp256-1mib-v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ErrorCodes = ["UNSUPPORTED_VERSION", "UNSUPPORTED_SERVICE", "INVALID_MESSAGE", "INVALID_FRAME", "UNEXPECTED_MESSAGE", "IDENTITY_MISMATCH", "SIZE_MISMATCH", "HASH_MISMATCH", "STORAGE_FAILED", "CANCELLED", "TIMEOUT", "DIRECT_PATH_UNVERIFIED"];
    internal static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsTransferId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void ValidateFrameHeader(byte kind, uint size)
    {
        if (size == 0 || kind is not (1 or 2) || size > (kind == 1 ? 4096u : 65536u)) throw new P0Exception("INVALID_FRAME");
    }
    internal static P0Message Parse(byte[] payload)
    {
        try
        {
            if (payload.Length is < 1 or > 4096 || (payload.Length >= 3 && payload[0] == 0xef && payload[1] == 0xbb && payload[2] == 0xbf)) throw new P0Exception("INVALID_MESSAGE");
            _ = StrictUtf8.GetString(payload);
            using var doc = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new P0Exception("INVALID_MESSAGE");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in doc.RootElement.EnumerateObject()) if (!fields.TryAdd(field.Name, field.Value.Clone())) throw new P0Exception("INVALID_MESSAGE");
            string Text(string key)
            {
                if (!fields.TryGetValue(key, out var f) || f.ValueKind != JsonValueKind.String) throw new P0Exception("INVALID_MESSAGE");
                return f.GetString()!;
            }
            string type = Text("type");
            string[] keys = type switch {
                "HELLO" => ["type", "version", "role", "service", "identity"],
                "OFFER" => ["type", "transferId", "fixture", "sizeBytes", "sha256"],
                "ACCEPT" => ["type", "transferId"], "REJECT" => ["type", "transferId", "reason"],
                "COMPLETE" => ["type", "transferId", "sizeBytes", "sha256"],
                "ERROR" => ["type", "transferId", "code"], _ => throw new P0Exception("INVALID_MESSAGE")
            };
            if (fields.Count != keys.Length || keys.Any(k => !fields.ContainsKey(k))) throw new P0Exception("INVALID_MESSAGE");
            string? id = null;
            if (type != "HELLO")
            {
                if (type == "ERROR" && fields["transferId"].ValueKind == JsonValueKind.Null) id = null;
                else { id = Text("transferId"); if (!IsTransferId(id)) throw new P0Exception("INVALID_MESSAGE"); }
            }
            if (type == "HELLO")
            {
                if (Text("version") != "0.1") throw new P0Exception("UNSUPPORTED_VERSION");
                if (Text("service") != "file.p0") throw new P0Exception("UNSUPPORTED_SERVICE");
                if (Text("role") is not ("sender" or "receiver") || !IsFingerprint(Text("identity"))) throw new P0Exception("INVALID_MESSAGE");
            }
            if (type is "OFFER" or "COMPLETE")
            {
                var size = fields["sizeBytes"];
                if (size.ValueKind != JsonValueKind.Number || !Regex.IsMatch(size.GetRawText(), "^(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant) || !size.TryGetUInt32(out uint value) || value != Size) throw new P0Exception(type == "COMPLETE" ? "SIZE_MISMATCH" : "INVALID_MESSAGE");
                if (Text("sha256") != Hash) throw new P0Exception(type == "COMPLETE" ? "HASH_MISMATCH" : "INVALID_MESSAGE");
                if (type == "OFFER" && Text("fixture") != Fixture) throw new P0Exception("INVALID_MESSAGE");
            }
            if (type == "REJECT" && Text("reason") is not ("USER_DECLINED" or "NO_SPACE" or "BUSY" or "POLICY")) throw new P0Exception("INVALID_MESSAGE");
            if (type == "ERROR" && !ErrorCodes.Contains(Text("code"))) throw new P0Exception("INVALID_MESSAGE");
            return new(type, id, fields);
        }
        catch (P0Exception) { throw; }
        catch (Exception e) when (e is JsonException or DecoderFallbackException or InvalidOperationException or FormatException) { throw new P0Exception("INVALID_MESSAGE"); }
    }
    internal static void Require(P0Message m, string type, string? id = null)
    {
        if (m.Type == "ERROR")
        {
            if (m.TransferId != id) throw new P0Exception("UNEXPECTED_MESSAGE");
            throw new P0Exception("PEER_" + m.Text("code"));
        }
        if (m.Type != type || (type is not ("HELLO" or "OFFER") && m.TransferId != id) || (type == "OFFER" && id is not null && m.TransferId != id)) throw new P0Exception("UNEXPECTED_MESSAGE");
    }
    internal static void VerifyHello(P0Message m, string role, string authenticatedPeer)
    {
        Require(m, "HELLO");
        if (m.Text("identity") != authenticatedPeer) throw new P0Exception("IDENTITY_MISMATCH");
        if (m.Text("role") != role) throw new P0Exception("UNEXPECTED_MESSAGE");
    }
    internal static object Hello(string role, string identity) => new { type = "HELLO", version = "0.1", role, service = "file.p0", identity };
    internal static object Offer(string id) => new { type = "OFFER", transferId = id, fixture = Fixture, sizeBytes = Size, sha256 = Hash };
    internal static object Complete(string id) => new { type = "COMPLETE", transferId = id, sizeBytes = Size, sha256 = Hash };
    internal static byte[] RampChunk(int size = 65536)
    {
        var bytes = new byte[size]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i; return bytes;
    }
}

// Every operation is bounded by a monotonic absolute session deadline. The first
// header byte is also subject to the current phase's budget; trickle bytes cannot
// restart the frame budget.
internal sealed class P0Wire(Stream stream, Func<bool> pathVerified, CancellationToken cancellation, Action<byte>? frameStarted = null)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    internal long Deadline(int seconds) => Math.Min(180_000, clock.ElapsedMilliseconds + seconds * 1000L);
    internal long RemainingMs => Math.Max(0, 180_000 - clock.ElapsedMilliseconds);
    internal void VerifyPath() { if (!pathVerified()) throw new P0Exception("DIRECT_PATH_UNVERIFIED"); }
    private CancellationTokenSource Budget(long deadlineMs)
    {
        cancellation.ThrowIfCancellationRequested();
        long remaining = Math.Min(180_000, deadlineMs) - clock.ElapsedMilliseconds;
        if (remaining <= 0) throw new P0Exception("TIMEOUT");
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation); stop.CancelAfter(TimeSpan.FromMilliseconds(remaining)); return stop;
    }
    private async Task ReadExactlyAsync(Memory<byte> buffer, long deadline)
    {
        using var stop = Budget(deadline);
        try { await stream.ReadExactlyAsync(buffer, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new P0Exception("TIMEOUT"); }
    }
    internal Task<P0Frame> ReadAsync(int phaseSeconds) => ReadUntilAsync(Deadline(phaseSeconds));
    internal async Task<P0Frame> ReadUntilAsync(long phaseDeadline)
    {
        VerifyPath();
        byte[] header = new byte[5];
        await ReadExactlyAsync(header.AsMemory(0, 1), phaseDeadline).ConfigureAwait(false);
        frameStarted?.Invoke(header[0]);
        long firstByteDeadline = clock.ElapsedMilliseconds + 10_000;
        await ReadExactlyAsync(header.AsMemory(1), Math.Min(firstByteDeadline, phaseDeadline)).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        P0Protocol.ValidateFrameHeader(header[0], size);
        byte[] payload = new byte[(int)size];
        long payloadDeadline = header[0] == 1 ? firstByteDeadline : clock.ElapsedMilliseconds + 30_000;
        await ReadExactlyAsync(payload, Math.Min(payloadDeadline, phaseDeadline)).ConfigureAwait(false);
        VerifyPath(); return new(header[0], payload);
    }
    internal async Task<P0Message> ControlAsync(int phaseSeconds)
    {
        var frame = await ReadAsync(phaseSeconds).ConfigureAwait(false);
        if (frame.Kind != 1) throw new P0Exception("UNEXPECTED_MESSAGE"); return P0Protocol.Parse(frame.Payload);
    }
    internal async Task<P0Message> ControlUntilAsync(long deadline)
    {
        var frame = await ReadUntilAsync(deadline).ConfigureAwait(false);
        if (frame.Kind != 1) throw new P0Exception("UNEXPECTED_MESSAGE"); return P0Protocol.Parse(frame.Payload);
    }
    internal async Task<P0Message> TerminalWithProgressAsync(CancellationToken progressTimeout)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, progressTimeout);
        // Retain the original absolute clock while a separate rearmed inactivity
        // token follows DATA progress and the final COMPLETE wait.
        var reader = new P0Wire(stream, pathVerified, linked.Token);
        try { return await reader.ControlAsync((int)Math.Ceiling(RemainingMs / 1000.0)).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new P0Exception("TIMEOUT"); }
    }
    internal Task ControlAsync(object control) => WriteAsync(1, JsonSerializer.SerializeToUtf8Bytes(control));
    internal Task ControlUntilAsync(object control, long deadline) => WriteAsync(1, JsonSerializer.SerializeToUtf8Bytes(control), deadline);
    internal async Task WriteAsync(byte kind, byte[] payload, long? stageDeadline = null)
    {
        VerifyPath(); P0Protocol.ValidateFrameHeader(kind, (uint)payload.Length);
        using var stop = Budget(Math.Min(clock.ElapsedMilliseconds + 30_000, stageDeadline ?? long.MaxValue));
        byte[] frame = new byte[payload.Length + 5]; frame[0] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length); payload.CopyTo(frame, 5);
        try { await stream.WriteAsync(frame, stop.Token).ConfigureAwait(false); await stream.FlushAsync(stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new P0Exception("TIMEOUT"); }
    }
    internal async Task ErrorAsync(string? id, string code)
    {
        if (code.StartsWith("PEER_", StringComparison.Ordinal)) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "ERROR", transferId = id, code });
            byte[] frame = new byte[payload.Length + 5]; frame[0] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length); payload.CopyTo(frame, 5);
            await stream.WriteAsync(frame, stop.Token).ConfigureAwait(false);
        }
        catch { /* One bounded, sanitized best-effort terminal message. */ }
    }
}
