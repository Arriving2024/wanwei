using System.Runtime.InteropServices;

namespace Wanwei;

internal static class NativeWlan
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Association
    {
        public uint SsidLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Ssid;
        public int BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int PhyType;
        public uint PhyIndex, SignalQuality, RxRate, TxRate;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Connection
    {
        public int State, Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Association Association;
        public int SecurityEnabled, OneXEnabled, AuthAlgorithm, CipherAlgorithm;
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, int opcode,
        IntPtr reserved, out uint size, out IntPtr data, out int type);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    public static string PackageStatus()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        return result == 15700 ? "unpackaged" : result is 0 or 122 ? "packaged" : $"unknown_error_{result}";
    }
    public static NetworkSample Snapshot(RunPrivacy privacy, long elapsedMs)
    {
        uint error = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (error != 0) return new(elapsedMs, "wlan_open_failed", error, []);
        try
        {
            error = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (error != 0) return new(elapsedMs, "wlan_enum_failed", error, []);
            try
            {
                var adapters = new List<WlanAdapter>();
                int count = Marshal.ReadInt32(list);
                if (count < 0 || count > 1024) return new(elapsedMs, "invalid_native_count", null, []);
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<InterfaceInfo>(list + 8 + i * Marshal.SizeOf<InterfaceInfo>());
                    string? apToken = null;
                    uint? queryError = null;
                    int state = item.State;
                    if (state == 1)
                    {
                        uint query = WlanQueryInterface(handle, ref item.Id, 7, IntPtr.Zero, out var size, out var data, out _);
                        if (query == 0)
                        {
                            try
                            {
                                if (size < Marshal.SizeOf<Connection>()) queryError = 13;
                                else
                                {
                                    var connection = Marshal.PtrToStructure<Connection>(data);
                                    state = connection.State;
                                    if (state == 1 && connection.Association.Bssid.Any(b => b != 0))
                                        apToken = privacy.Token("ap", Convert.ToHexString(connection.Association.Bssid));
                                }
                            }
                            finally { WlanFreeMemory(data); }
                        }
                        else queryError = query;
                    }
                    adapters.Add(new(privacy.Token("adapter", item.Id.ToString()), item.Description,
                        state == 1 ? "connected" : state == 4 ? "disconnected" : $"state_{state}", apToken, queryError));
                }
                return new(elapsedMs, "ok", null, adapters);
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }
}
