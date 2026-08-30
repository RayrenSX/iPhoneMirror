namespace IPhoneMirror.App.Services;

/// <summary>Combines unsent relative-motion reports without losing travel.</summary>
internal static class BluetoothMouseReportCoalescer
{
    internal const int ReportLength = 6;

    internal static byte[] MergePendingMotion(byte[]? pending, byte[] incoming)
    {
        if (pending is null || pending.Length != ReportLength ||
            incoming.Length != ReportLength || pending[0] != incoming[0] ||
            incoming[5] != 0)
            return incoming;

        var x = Math.Clamp(ReadInt16(pending, 1) + ReadInt16(incoming, 1),
            short.MinValue + 1, short.MaxValue);
        var y = Math.Clamp(ReadInt16(pending, 3) + ReadInt16(incoming, 3),
            short.MinValue + 1, short.MaxValue);
        WriteInt16(incoming, 1, (short)x);
        WriteInt16(incoming, 3, (short)y);
        return incoming;
    }

    private static short ReadInt16(byte[] report, int offset) =>
        (short)(report[offset] | report[offset + 1] << 8);

    private static void WriteInt16(byte[] report, int offset, short value)
    {
        report[offset] = (byte)(value & 0xFF);
        report[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
