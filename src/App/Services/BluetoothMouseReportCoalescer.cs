namespace IPhoneMirror.App.Services;

/// <summary>Coalesces unsent relative-motion reports to the newest state.</summary>
internal static class BluetoothMouseReportCoalescer
{
    internal const int ReportLength = 6;

    internal static byte[] MergePendingMotion(byte[]? pending, byte[] incoming)
    {
        // Relative reports become stale while the GATT stack is blocked. Do
        // not add old travel to the next packet: that creates visible pointer
        // drift when the stack catches up. The caller still preserves button
        // and wheel reports as discrete priority events.
        if (incoming.Length != ReportLength || incoming[5] != 0)
            return incoming;
        // Latest-wins coalescing bounds latency to one report interval even
        // when input arrives faster than BLE can deliver notifications.
        return incoming;
    }
}
