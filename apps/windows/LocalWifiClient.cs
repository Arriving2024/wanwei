using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Wanwei;

// This experimental transport temporarily replaces the selected adapter's association.
// It does not prove WLAN continuity, internet access, a transfer, a frequency band, or MLO.
// Never serialize a profile XML, SSID, passphrase, native Connection, or this class's fields.
internal sealed class LocalWifiClient : IDisposable, IAsyncDisposable
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RestoreBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly LocalWifiNative.ClientHandle handle;
    private readonly byte[] requestedSsid;
    private Guid interfaceId;
    private string? originalProfile;
    private byte[]? originalSsid;
    private bool baselineCaptured;
    private bool baselineConnected;
    private bool connectionRequestIssued;
    private bool ownsSessionGate = true;

    public LocalWifiConnectionAttributes? Attributes { get; private set; }
    public LocalWifiRestoreSnapshot? Restoration { get; private set; }

    private LocalWifiClient(LocalWifiNative.ClientHandle handle, byte[] requestedSsid)
    { this.handle = handle; this.requestedSsid = requestedSsid; }

    /// <summary>
    /// Connects using in-memory WLAN XML only. Android security types 1 and 2 use the
    /// WPA2PSK/AES baseline; pure WPA3 (3), open (0), and unknown security are refused.
    /// A successful factory confirms the exact requested SSID and temporary WPA2 mode.
    /// IP assignment can lag association; ReadAttributesAsync obtains a fresh sample.
    /// On any failure after the request, restoration is attempted before the sanitized
    /// exception is returned. Its Restoration property is safe for the caller's report.
    /// </summary>
    public static async Task<LocalWifiClient> ConnectAsync(string ssid, string passphrase,
        int androidSecurityType, bool allowDisconnectedBaseline = false,
        CancellationToken cancellationToken = default)
    {
        LocalWifiClientPolicy.ValidateRequest(ssid, passphrase, androidSecurityType);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new LocalWifiClientException("LOCAL_WIFI_WINDOWS_REQUIRED");
        if (!await SessionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new LocalWifiClientException("LOCAL_WIFI_SESSION_ALREADY_ACTIVE");
        LocalWifiClient? client = null;
        try
        {
            client = new(LocalWifiNative.Open(), LocalWifiClientPolicy.EncodeSsid(ssid));
            client.CaptureBaseline(allowDisconnectedBaseline);
            cancellationToken.ThrowIfCancellationRequested();
            // Revalidate immediately before changing the sole adapter. A user/network
            // manager switching the baseline during preparation aborts the experiment.
            client.RequireUnchangedBaseline();
            string xml = LocalWifiClientPolicy.BuildTemporaryProfileXml(ssid, passphrase, androidSecurityType);
            client.connectionRequestIssued = true;
            LocalWifiNative.Connect(client.handle, client.interfaceId, mode: 1, xml);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < ConnectBudget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = LocalWifiNative.Current(client.handle, client.interfaceId);
                client.Attributes = client.Describe(current, elapsed.ElapsedMilliseconds);
                if (current is { State: 1 } && LocalWifiClientPolicy.SsidEquals(current.Value.Ssid, client.requestedSsid))
                {
                    LocalWifiClientPolicy.RequireSecureTemporaryConnection(current.Value.Mode,
                        current.Value.SecurityEnabled != 0, current.Value.OneXEnabled != 0,
                        current.Value.AuthAlgorithm, current.Value.CipherAlgorithm, current.Value.BssType);
                    return client;
                }
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            throw new LocalWifiClientException("LOCAL_WIFI_CONNECT_TIMEOUT");
        }
        catch (Exception error)
        {
            LocalWifiRestoreSnapshot? restoration = null;
            LocalWifiConnectionAttributes? attributes = client?.Attributes;
            if (client is not null) restoration = await client.RestoreAsync().ConfigureAwait(false);
            else SessionGate.Release();
            // Deliberately do not retain an InnerException: it can contain input values.
            throw new LocalWifiClientException(error is LocalWifiClientException known ? known.Code :
                error is OperationCanceledException ? "LOCAL_WIFI_CANCELLED" : "LOCAL_WIFI_UNEXPECTED_ERROR",
                (error as LocalWifiClientException)?.NativeError, restoration, attributes);
        }
    }

    public async Task<LocalWifiConnectionAttributes> ReadAttributesAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Restoration is not null) throw new LocalWifiClientException("LOCAL_WIFI_SESSION_CLOSED", restoration: Restoration);
            return Attributes = Describe(LocalWifiNative.Current(handle, interfaceId), 0);
        }
        finally { operationGate.Release(); }
    }

    internal async Task<P0Path> CreateFixturePathAsync(CancellationToken cancellationToken = default)
    {
        // Association can precede DHCP. Poll only this owned adapter for five
        // seconds; never substitute a default route or another interface.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RequireFixtureAssociation();
                var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n =>
                    Guid.TryParse(n.Id, out var id) && id == interfaceId && n.OperationalStatus == OperationalStatus.Up);
                if (adapter is not null)
                {
                    var ip = adapter.GetIPProperties();
                    var endpoint = LocalWifiFixturePathPolicy.Select(interfaceId, ip.GetIPv4Properties().Index,
                        ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => new LocalWifiFixtureAddress(a.Address, a.PrefixLength)).ToArray(),
                        ip.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray());
                    return P0Path.CreateLocalHotspot(this, endpoint);
                }
            }
            catch (LocalWifiClientException error) when (error.Code == "LOCAL_WIFI_FIXTURE_DHCP_NOT_READY" && elapsed.Elapsed < TimeSpan.FromSeconds(5)) { }
            finally { operationGate.Release(); }
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5)) throw new LocalWifiClientException("LOCAL_WIFI_FIXTURE_DHCP_NOT_READY");
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RequireFixtureAssociation()
    {
        if (Restoration is not null || !connectionRequestIssued || handle.IsClosed || handle.IsInvalid)
            throw new LocalWifiClientException("LOCAL_WIFI_SESSION_CLOSED");
        LocalWifiClientPolicy.RequireSingleInterface(LocalWifiNative.Interfaces(handle).Count);
        var current = LocalWifiNative.Current(handle, interfaceId);
        if (current is not { State: 1 } || !LocalWifiClientPolicy.SsidEquals(current.Value.Ssid, requestedSsid))
            throw new LocalWifiClientException("LOCAL_WIFI_ASSOCIATION_LOST");
        LocalWifiClientPolicy.RequireSecureTemporaryConnection(current.Value.Mode, current.Value.SecurityEnabled != 0,
            current.Value.OneXEnabled != 0, current.Value.AuthAlgorithm, current.Value.CipherAlgorithm, current.Value.BssType);
    }

    internal bool VerifyFixturePath(LocalWifiFixtureEndpoint expected)
    {
        // The TLS writer and terminal reader may verify concurrently. Briefly
        // serialize metadata queries instead of treating ordinary contention as
        // loss of the native association. Restoration still causes a bounded failure.
        if (!operationGate.Wait(TimeSpan.FromSeconds(1))) return false;
        try
        {
            if (expected.InterfaceId != interfaceId) return false;
            RequireFixtureAssociation();
            var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n =>
                Guid.TryParse(n.Id, out var id) && id == interfaceId && n.OperationalStatus == OperationalStatus.Up);
            if (adapter is null) return false;
            var ip = adapter.GetIPProperties();
            var current = LocalWifiFixturePathPolicy.Select(interfaceId, ip.GetIPv4Properties().Index,
                ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new LocalWifiFixtureAddress(a.Address, a.PrefixLength)).ToArray(),
                ip.GatewayAddresses.Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray());
            return current == expected;
        }
        catch { return false; }
        finally { operationGate.Release(); }
    }

    private void CaptureBaseline(bool allowDisconnectedBaseline)
    {
        var interfaces = LocalWifiNative.Interfaces(handle);
        LocalWifiClientPolicy.RequireSingleInterface(interfaces.Count);
        interfaceId = interfaces[0].Id;
        uint state = LocalWifiNative.State(handle, interfaceId);
        var current = state == 1 ? LocalWifiNative.Current(handle, interfaceId) : null;
        bool persistent = current is { State: 1 } && current.Value.Mode is 0 or 4 &&
            !string.IsNullOrEmpty(current.Value.ProfileName) && current.Value.Ssid.Length is >= 1 and <= 32 &&
            current.Value.BssType == 1 && LocalWifiNative.ProfileExists(handle, interfaceId, current.Value.ProfileName);
        LocalWifiClientPolicy.ValidateBaseline(interfaces.Count, state, persistent, allowDisconnectedBaseline);
        baselineConnected = state == 1;
        if (baselineConnected)
        {
            originalProfile = current!.Value.ProfileName;
            originalSsid = (byte[])current.Value.Ssid.Clone();
            if (LocalWifiClientPolicy.SsidEquals(originalSsid, requestedSsid))
                throw new LocalWifiClientException("LOCAL_WIFI_TARGET_EQUALS_BASELINE");
        }
        baselineCaptured = true;
    }

    private void RequireUnchangedBaseline()
    {
        LocalWifiClientPolicy.RequireSingleInterface(LocalWifiNative.Interfaces(handle).Count);
        uint state = LocalWifiNative.State(handle, interfaceId);
        if (!baselineConnected)
        {
            if (state != 4) throw new LocalWifiClientException("LOCAL_WIFI_BASELINE_CHANGED");
            return;
        }
        var current = state == 1 ? LocalWifiNative.Current(handle, interfaceId) : null;
        if (!OriginalMatches(current) || !LocalWifiNative.ProfileExists(handle, interfaceId, originalProfile!))
            throw new LocalWifiClientException("LOCAL_WIFI_BASELINE_CHANGED");
    }

    private bool OriginalMatches(LocalWifiNative.Connection? current) => current is { State: 1 } &&
        current.Value.Mode is 0 or 4 && current.Value.ProfileName == originalProfile &&
        originalSsid is not null && LocalWifiClientPolicy.SsidEquals(current.Value.Ssid, originalSsid);

    /// <summary>
    /// Restores only the captured adapter, independently of cancellation. Idempotent.
    /// Returns a report even on restoration failure; callers must check Confirmed.
    /// Closing the WLAN handle is not treated as restoration confirmation.
    /// </summary>
    public async Task<LocalWifiRestoreSnapshot> RestoreAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Restoration is not null) return Restoration;
            var elapsed = Stopwatch.StartNew();
            try
            {
                if (!connectionRequestIssued)
                    return Restoration = new("no_connection_request_issued", false, true,
                        baselineCaptured ? baselineConnected : null, null, null, null, null, elapsed.ElapsedMilliseconds);

                if (baselineConnected)
                {
                    if (OriginalMatches(LocalWifiNative.Current(handle, interfaceId)))
                        return Restoration = new("original_connection_already_restored", true, true, true,
                            true, true, null, null, elapsed.ElapsedMilliseconds);
                    // A profile-name connection uses an existing profile. Never create,
                    // replace, export, save, delete, or read the original key material.
                    LocalWifiNative.Connect(handle, interfaceId, mode: 0, originalProfile!);
                    while (elapsed.Elapsed < RestoreBudget)
                    {
                        var current = LocalWifiNative.Current(handle, interfaceId);
                        if (OriginalMatches(current))
                            return Restoration = new("original_connection_restored", true, true, true,
                                true, true, null, null, elapsed.ElapsedMilliseconds);
                        await Task.Delay(PollInterval).ConfigureAwait(false);
                    }
                    return Restoration = new("original_connection_restore_timeout", true, false, true,
                        null, null, null, null, elapsed.ElapsedMilliseconds);
                }

                if (LocalWifiNative.State(handle, interfaceId) == 4)
                    return Restoration = new("disconnected_baseline_already_restored", true, true, false,
                        null, null, true, null, elapsed.ElapsedMilliseconds);
                LocalWifiNative.Disconnect(handle, interfaceId);
                while (elapsed.Elapsed < RestoreBudget)
                {
                    if (LocalWifiNative.State(handle, interfaceId) == 4)
                        return Restoration = new("disconnected_baseline_restored", true, true, false,
                            null, null, true, null, elapsed.ElapsedMilliseconds);
                    await Task.Delay(PollInterval).ConfigureAwait(false);
                }
                return Restoration = new("disconnected_baseline_restore_timeout", true, false, false,
                    null, null, false, null, elapsed.ElapsedMilliseconds);
            }
            catch (Exception error)
            {
                return Restoration = new("restore_failed", true, false, baselineConnected,
                    null, null, null, (error as LocalWifiClientException)?.NativeError, elapsed.ElapsedMilliseconds);
            }
            finally
            {
                handle.Dispose();
                if (originalSsid is not null) CryptographicOperations.ZeroMemory(originalSsid);
                CryptographicOperations.ZeroMemory(requestedSsid);
                originalProfile = null;
                if (ownsSessionGate) { ownsSessionGate = false; SessionGate.Release(); }
            }
        }
        finally { operationGate.Release(); }
    }

    private LocalWifiConnectionAttributes Describe(LocalWifiNative.Connection? current, long elapsedMs)
    {
        uint? channel = null, channelError = null;
        var ip = LocalWifiNative.AdapterAddresses(interfaceId);
        if (current is { State: 1 })
        {
            (channel, channelError) = LocalWifiNative.Channel(handle, interfaceId);
        }
        return new(current is { State: 1 }, current is { State: 1 } &&
            LocalWifiClientPolicy.SsidEquals(current.Value.Ssid, requestedSsid), current?.Mode == 1,
            current?.Mode, current?.PhyType, current is null ? null : LocalWifiClientPolicy.PhyName(current.Value.PhyType),
            channel, channelError, current?.SignalQuality, current?.RxRate, current?.TxRate,
            current?.AuthAlgorithm, current?.CipherAlgorithm, ip.Status, ip.Addresses, ip.Gateways,
            ip.Ipv4InterfaceIndex, baselineConnected, elapsedMs);
    }

    public void Dispose() => RestoreAsync().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() => await RestoreAsync().ConfigureAwait(false);
    public override string ToString() => "LocalWifiClient (credentials and network identity redacted)";
}

internal sealed record LocalWifiFixtureAddress(IPAddress Address, int PrefixLength);
internal sealed record LocalWifiFixtureEndpoint(IPAddress Local, IPAddress Remote, int PrefixLength, Guid InterfaceId, int InterfaceIndex);

internal static class LocalWifiFixturePathPolicy
{
    internal static LocalWifiFixtureEndpoint Select(Guid interfaceId, int interfaceIndex,
        IReadOnlyList<LocalWifiFixtureAddress> addresses, IReadOnlyList<IPAddress> gateways)
    {
        if (interfaceId == Guid.Empty || interfaceIndex <= 0) throw new LocalWifiClientException("LOCAL_WIFI_FIXTURE_INTERFACE_INVALID");
        if (addresses.Count == 0 || gateways.Count == 0) throw new LocalWifiClientException("LOCAL_WIFI_FIXTURE_DHCP_NOT_READY");
        if (addresses.Count != 1 || gateways.Count != 1) throw new LocalWifiClientException("LOCAL_WIFI_FIXTURE_ENDPOINT_AMBIGUOUS");
        var local = addresses[0]; var remote = gateways[0];
        if (local.PrefixLength is < 1 or > 30 || !Usable(local.Address) || !Usable(remote) || local.Address.Equals(remote))
            throw new LocalWifiClientException("LOCAL_WIFI_FIXTURE_ENDPOINT_INVALID");
        uint a = Number(local.Address), b = Number(remote), mask = uint.MaxValue << (32 - local.PrefixLength);
        uint network = a & mask, broadcast = network | ~mask;
        if ((b & mask) != network || a == network || a == broadcast || b == network || b == broadcast)
            throw new LocalWifiClientException("LOCAL_WIFI_FIXTURE_GATEWAY_OUTSIDE_SUBNET");
        return new(local.Address, remote, local.PrefixLength, interfaceId, interfaceIndex);
    }
    private static bool Usable(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
    private static uint Number(IPAddress address) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
}

// These records intentionally have no SSID, BSSID, adapter GUID/name, profile, or key.
internal sealed record LocalWifiConnectionAttributes(bool Connected, bool RequestedSsidMatched,
    bool TemporaryProfileMode, uint? ConnectionMode, uint? PhyTypeValue, string? PhyType,
    uint? Channel, uint? ChannelQueryError, uint? SignalQualityPercent,
    uint? RxLinkRateKbps, uint? TxLinkRateKbps, uint? AuthAlgorithm, uint? CipherAlgorithm,
    string IpStatus, IReadOnlyList<string> AdapterIpAddresses, IReadOnlyList<string> AdapterGateways,
    int? Ipv4InterfaceIndex, bool BaselineWasConnected, long ElapsedMs)
{
    public double? RxLinkRateMbps => RxLinkRateKbps / 1000.0;
    public double? TxLinkRateMbps => TxLinkRateKbps / 1000.0;
}

internal sealed record LocalWifiRestoreSnapshot(string Status, bool Attempted, bool Confirmed,
    bool? BaselineWasConnected, bool? OriginalSsidMatched, bool? OriginalProfileMatched,
    bool? DisconnectedBaselineConfirmed, uint? NativeError, long ElapsedMs);

internal sealed class LocalWifiClientException : Exception
{
    public string Code { get; }
    public uint? NativeError { get; }
    public LocalWifiRestoreSnapshot? Restoration { get; }
    public LocalWifiConnectionAttributes? Attributes { get; }
    internal LocalWifiClientException(string code, uint? nativeError = null,
        LocalWifiRestoreSnapshot? restoration = null, LocalWifiConnectionAttributes? attributes = null)
        : base(nativeError is null ? code : $"{code} (native {nativeError.Value})")
    { Code = code; NativeError = nativeError; Restoration = restoration; Attributes = attributes; }
}

// All policy methods can be tested on the host without invoking any WLAN API.
internal static class LocalWifiClientPolicy
{
    internal const string ProfileNamespace = "http://www.microsoft.com/networking/WLAN/profile/v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeSsid(string ssid)
    {
        if (ssid is null) throw new LocalWifiClientException("LOCAL_WIFI_INVALID_SSID");
        byte[] bytes;
        try { bytes = StrictUtf8.GetBytes(ssid); }
        catch (EncoderFallbackException) { throw new LocalWifiClientException("LOCAL_WIFI_INVALID_SSID"); }
        if (bytes.Length is < 1 or > 32) throw new LocalWifiClientException("LOCAL_WIFI_INVALID_SSID");
        return bytes;
    }

    public static void ValidateRequest(string ssid, string passphrase, int androidSecurityType)
    {
        if (androidSecurityType == 3) throw new LocalWifiClientException("LOCAL_WIFI_PURE_WPA3_UNSUPPORTED");
        if (androidSecurityType is not (1 or 2)) throw new LocalWifiClientException("LOCAL_WIFI_SECURITY_UNSUPPORTED");
        _ = EncodeSsid(ssid);
        // LocalOnlyHotspot uses a passphrase. A 64-character raw PSK is deliberately
        // outside this API; never guess a key type or downgrade to open authentication.
        if (passphrase is null || passphrase.Length is < 8 or > 63 || passphrase.Any(c => c is < ' ' or > '~'))
            throw new LocalWifiClientException("LOCAL_WIFI_INVALID_PASSPHRASE");
    }

    public static string BuildTemporaryProfileXml(string ssid, string passphrase, int androidSecurityType)
    {
        ValidateRequest(ssid, passphrase, androidSecurityType);
        var text = new StringBuilder();
        using var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false });
        void Element(string name, string value) => writer.WriteElementString(name, ProfileNamespace, value);
        writer.WriteStartElement("WLANProfile", ProfileNamespace);
        Element("name", "Wanwei-LocalWifi-Temporary");
        writer.WriteStartElement("SSIDConfig", ProfileNamespace);
        writer.WriteStartElement("SSID", ProfileNamespace);
        // A hex SSID avoids XML/code-page ambiguity; comparison uses the exact bytes.
        Element("hex", Convert.ToHexString(EncodeSsid(ssid)));
        writer.WriteEndElement();
        Element("nonBroadcast", "false"); writer.WriteEndElement();
        Element("connectionType", "ESS"); Element("connectionMode", "manual");
        writer.WriteStartElement("MSM", ProfileNamespace);
        writer.WriteStartElement("security", ProfileNamespace);
        writer.WriteStartElement("authEncryption", ProfileNamespace);
        Element("authentication", "WPA2PSK"); Element("encryption", "AES"); Element("useOneX", "false");
        writer.WriteEndElement();
        writer.WriteStartElement("sharedKey", ProfileNamespace);
        Element("keyType", "passPhrase"); Element("protected", "false"); Element("keyMaterial", passphrase);
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        writer.Flush();
        return text.ToString();
    }

    public static void RequireSingleInterface(int interfaceCount)
    {
        if (interfaceCount != 1) throw new LocalWifiClientException(interfaceCount == 0 ?
            "LOCAL_WIFI_NO_INTERFACE" : "LOCAL_WIFI_MULTIPLE_INTERFACES");
    }

    public static void ValidateBaseline(int interfaceCount, uint state, bool persistentProfileAvailable,
        bool allowDisconnectedBaseline = false)
    {
        RequireSingleInterface(interfaceCount);
        if (state == 1 && persistentProfileAvailable) return;
        if (state == 4 && allowDisconnectedBaseline) return;
        throw new LocalWifiClientException(state == 1 ? "LOCAL_WIFI_BASELINE_NOT_RESTORABLE" :
            state == 4 ? "LOCAL_WIFI_DISCONNECTED_BASELINE_REQUIRES_OPT_IN" : "LOCAL_WIFI_INTERFACE_NOT_STABLE");
    }

    public static bool SsidEquals(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
        actual.Length is >= 1 and <= 32 && expected.Length is >= 1 and <= 32 && actual.SequenceEqual(expected);

    public static void RequireSecureTemporaryConnection(uint mode, bool securityEnabled, bool oneXEnabled,
        uint authAlgorithm, uint cipherAlgorithm, uint bssType)
    {
        if (mode != 1) throw new LocalWifiClientException("LOCAL_WIFI_TEMPORARY_MODE_NOT_OBSERVED");
        if (!securityEnabled || oneXEnabled || authAlgorithm != 7 || cipherAlgorithm != 4 || bssType != 1)
            throw new LocalWifiClientException("LOCAL_WIFI_SECURE_BASELINE_NOT_OBSERVED");
    }

    public static string PhyName(uint value) => value switch
    { 0 => "unknown", 1 => "FHSS", 2 => "DSSS", 3 => "IR", 4 => "OFDM", 5 => "HRDSSS", 6 => "ERP", 7 => "802.11n", 8 => "802.11ac", 9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be", _ => "unrecognized" };
}

// ABI verified against Microsoft wlanapi.h/wlantypes.h and the API documentation:
// https://learn.microsoft.com/windows/win32/api/wlanapi/ns-wlanapi-wlan_connection_parameters
// https://learn.microsoft.com/windows/win32/api/wlanapi/nf-wlanapi-wlanqueryinterface
// https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/wlanapi.h
// The SDK defines association Rx/Tx rate units as Kbits/second. Enum/BOOL/ULONG
// fields are 32 bits; pointer alignment is native, including ConnectionParameters.
internal static class LocalWifiNative
{
    private const uint ErrorInvalidState = 5023;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InterfaceInfo
    {
        internal Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
        internal uint State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeConnection
    {
        internal uint State, Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Profile;
        internal uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] Ssid;
        internal uint BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] internal byte[] Bssid;
        internal uint PhyType, PhyIndex, SignalQuality, RxRate, TxRate;
        internal int SecurityEnabled, OneXEnabled;
        internal uint AuthAlgorithm, CipherAlgorithm;
    }

    // Deliberately not a record: compiler-generated ToString must not reveal identity.
    internal struct Connection
    {
        internal uint State, Mode, BssType, PhyType, SignalQuality, RxRate, TxRate, AuthAlgorithm, CipherAlgorithm;
        internal int SecurityEnabled, OneXEnabled;
        internal string ProfileName;
        internal byte[] Ssid;
        public override readonly string ToString() => "WLAN connection (identity redacted)";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionParameters
    {
        internal uint Mode;
        internal IntPtr Profile, Ssid, DesiredBssids;
        internal uint BssType, Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
        internal uint Flags;
    }

    internal sealed class ClientHandle : SafeHandle
    {
        internal ClientHandle(IntPtr value) : base(IntPtr.Zero, true) => SetHandle(value);
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => WlanCloseHandle(handle, IntPtr.Zero) == 0;
    }

    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanEnumInterfaces(ClientHandle handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanGetProfileList(ClientHandle handle, ref Guid id, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanQueryInterface(ClientHandle handle, ref Guid id, uint opcode, IntPtr reserved, out uint size, out IntPtr data, out uint type);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanConnect(ClientHandle handle, ref Guid id, ref ConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll", ExactSpelling = true)] private static extern uint WlanDisconnect(ClientHandle handle, ref Guid id, IntPtr reserved);

    internal static ClientHandle Open()
    {
        if (!AbiLayoutIsValid()) throw new LocalWifiClientException("LOCAL_WIFI_ABI_LAYOUT_INVALID");
        uint error = WlanOpenHandle(2, IntPtr.Zero, out _, out var value);
        var result = new ClientHandle(value);
        if (error == 0 && !result.IsInvalid) return result;
        result.Dispose();
        throw new LocalWifiClientException("LOCAL_WIFI_OPEN_FAILED", error);
    }

    // Pure ABI check: safe to run in the host self-tests without opening a WLAN handle.
    internal static bool AbiLayoutIsValid() =>
        Marshal.SizeOf<InterfaceInfo>() == 532 && Marshal.SizeOf<ProfileInfo>() == 516 &&
        Marshal.SizeOf<NativeConnection>() == 604 &&
        Marshal.OffsetOf<NativeConnection>(nameof(NativeConnection.SsidLength)).ToInt32() == 520 &&
        Marshal.OffsetOf<NativeConnection>(nameof(NativeConnection.PhyType)).ToInt32() == 568 &&
        Marshal.OffsetOf<NativeConnection>(nameof(NativeConnection.SecurityEnabled)).ToInt32() == 588 &&
        Marshal.SizeOf<ConnectionParameters>() == (IntPtr.Size == 8 ? 40 : 24) &&
        Marshal.OffsetOf<ConnectionParameters>(nameof(ConnectionParameters.Profile)).ToInt32() == IntPtr.Size &&
        Marshal.OffsetOf<ConnectionParameters>(nameof(ConnectionParameters.BssType)).ToInt32() == (IntPtr.Size == 8 ? 32 : 16);

    internal static IReadOnlyList<InterfaceInfo> Interfaces(ClientHandle handle)
    {
        IntPtr list = IntPtr.Zero;
        try
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out list), "LOCAL_WIFI_ENUM_FAILED");
            if (list == IntPtr.Zero) throw new LocalWifiClientException("LOCAL_WIFI_NATIVE_DATA_INVALID");
            int count = Marshal.ReadInt32(list);
            if (count is < 0 or > 1024) throw new LocalWifiClientException("LOCAL_WIFI_NATIVE_DATA_INVALID");
            var items = new List<InterfaceInfo>(count);
            for (int i = 0; i < count; i++)
                items.Add(Marshal.PtrToStructure<InterfaceInfo>(IntPtr.Add(list, checked(8 + i * Marshal.SizeOf<InterfaceInfo>()))));
            return items;
        }
        finally { if (list != IntPtr.Zero) WlanFreeMemory(list); }
    }

    internal static bool ProfileExists(ClientHandle handle, Guid id, string profile)
    {
        IntPtr list = IntPtr.Zero;
        try
        {
            Check(WlanGetProfileList(handle, ref id, IntPtr.Zero, out list), "LOCAL_WIFI_PROFILE_LIST_FAILED");
            if (list == IntPtr.Zero) throw new LocalWifiClientException("LOCAL_WIFI_NATIVE_DATA_INVALID");
            int count = Marshal.ReadInt32(list);
            if (count is < 0 or > 65536) throw new LocalWifiClientException("LOCAL_WIFI_NATIVE_DATA_INVALID");
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<ProfileInfo>(IntPtr.Add(list, checked(8 + i * Marshal.SizeOf<ProfileInfo>())));
                if (string.Equals(item.Name, profile, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        finally { if (list != IntPtr.Zero) WlanFreeMemory(list); }
    }

    internal static uint State(ClientHandle handle, Guid id)
    {
        var (value, error) = QueryUInt(handle, id, 6);
        if (error is not null) throw new LocalWifiClientException("LOCAL_WIFI_STATE_QUERY_FAILED", error);
        return value!.Value;
    }

    internal static (uint? Value, uint? Error) Channel(ClientHandle handle, Guid id) => QueryUInt(handle, id, 8);

    private static (uint? Value, uint? Error) QueryUInt(ClientHandle handle, Guid id, uint opcode)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint error = WlanQueryInterface(handle, ref id, opcode, IntPtr.Zero, out uint size, out data, out _);
            if (error != 0) return (null, error);
            if (data == IntPtr.Zero || size < 4) return (null, 13);
            return (unchecked((uint)Marshal.ReadInt32(data)), null);
        }
        finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
    }

    internal static Connection? Current(ClientHandle handle, Guid id)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint error = WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out uint size, out data, out _);
            if (error == ErrorInvalidState) return null;
            Check(error, "LOCAL_WIFI_CONNECTION_QUERY_FAILED");
            if (data == IntPtr.Zero || size < Marshal.SizeOf<NativeConnection>())
                throw new LocalWifiClientException("LOCAL_WIFI_NATIVE_DATA_INVALID");
            var current = Marshal.PtrToStructure<NativeConnection>(data);
            if (current.SsidLength > 32 || current.SignalQuality > 100)
                throw new LocalWifiClientException("LOCAL_WIFI_NATIVE_DATA_INVALID");
            return new Connection { State = current.State, Mode = current.Mode, ProfileName = current.Profile,
                Ssid = current.Ssid.AsSpan(0, (int)current.SsidLength).ToArray(), BssType = current.BssType,
                PhyType = current.PhyType, SignalQuality = current.SignalQuality, RxRate = current.RxRate, TxRate = current.TxRate,
                SecurityEnabled = current.SecurityEnabled, OneXEnabled = current.OneXEnabled,
                AuthAlgorithm = current.AuthAlgorithm, CipherAlgorithm = current.CipherAlgorithm };
        }
        finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
    }

    internal static void Connect(ClientHandle handle, Guid id, uint mode, string profileOrXml)
    {
        IntPtr profile = IntPtr.Zero;
        try
        {
            profile = Marshal.StringToHGlobalUni(profileOrXml);
            var parameters = new ConnectionParameters { Mode = mode, Profile = profile, BssType = 1, Flags = 0 };
            Check(WlanConnect(handle, ref id, ref parameters, IntPtr.Zero), mode == 1 ? "LOCAL_WIFI_CONNECT_REQUEST_FAILED" : "LOCAL_WIFI_RESTORE_REQUEST_FAILED");
        }
        finally { if (profile != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(profile); }
    }

    internal static void Disconnect(ClientHandle handle, Guid id) =>
        Check(WlanDisconnect(handle, ref id, IntPtr.Zero), "LOCAL_WIFI_DISCONNECT_REQUEST_FAILED");

    internal static (string Status, IReadOnlyList<string> Addresses, IReadOnlyList<string> Gateways, int? Ipv4InterfaceIndex) AdapterAddresses(Guid id)
    {
        try
        {
            var matches = NetworkInterface.GetAllNetworkInterfaces().Where(n => Guid.TryParse(n.Id, out Guid value) && value == id).ToArray();
            if (matches.Length != 1) return ("adapter_not_uniquely_matched", [], [], null);
            var properties = matches[0].GetIPProperties();
            var addresses = properties.UnicastAddresses.Select(x => x.Address)
                .Where(x => (x.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) && !IPAddress.IsLoopback(x))
                .Select(x => x.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            var gateways = properties.GatewayAddresses.Select(x => x.Address)
                .Where(x => !x.Equals(IPAddress.Any) && !x.Equals(IPAddress.IPv6Any))
                .Select(x => x.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            return ("ok", addresses, gateways, properties.GetIPv4Properties()?.Index);
        }
        catch { return ("ip_query_failed", [], [], null); }
    }

    private static void Check(uint error, string code)
    { if (error != 0) throw new LocalWifiClientException(code, error); }
}
