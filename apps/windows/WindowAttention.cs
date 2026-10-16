using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Wanwei;

// Only this application's existing HWND is used. Never enumerates windows or
// stores handles / other applications' titles in diagnostic evidence.
internal sealed class WindowAttention : IDisposable
{
    private readonly nint windowHandle;
    internal bool Requested { get; private set; }

    internal WindowAttention(Window window)
    {
        windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == 0) return;
        // FLASHW_TRAY + a finite count, without either continuous TIMER flag.
        // FlashWindowEx's return value describes the previous caption state,
        // not whether the user saw the request, so deliberately ignore it.
        var flash = CreateInfo(windowHandle, 0x00000002, 3);
        _ = FlashWindowEx(ref flash);
        Requested = true;
    }

    internal static bool IsOwnForeground(Window window)
    {
        nint own = new WindowInteropHelper(window).Handle;
        return own != 0 && GetForegroundWindow() == own;
    }

    public void Dispose()
    {
        if (!Requested) return;
        var stop = CreateInfo(windowHandle, 0, 0); // FLASHW_STOP restores state.
        _ = FlashWindowEx(ref stop);
        Requested = false;
    }

    private static FlashInfo CreateInfo(nint windowHandle, uint flags, uint count) => new()
    {
        Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = windowHandle,
        Flags = flags, Count = count, Timeout = 0
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    // Microsoft contracts:
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-flashwindowex
    // https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-flashwinfo
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getforegroundwindow
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
