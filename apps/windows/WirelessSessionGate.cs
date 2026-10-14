namespace Wanwei;

// One publisher/listener owner across discovery-only advertisement and pairing.
// Uncertain release stays blocked until process exit; no OS pairing is deleted.
internal static class WirelessSessionGate
{
    private static int held;
    internal static bool RestartRequired => Volatile.Read(ref held) != 0;
    internal static bool TryEnter() => Interlocked.CompareExchange(ref held, 1, 0) == 0;
    internal static void Release(bool confirmed)
    {
        if (confirmed) Interlocked.Exchange(ref held, 0);
    }
}
