using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Controls;

internal sealed class NativePreviewMouse
{
    internal const int WmMouseLeave = 0x02A3;
    private bool _trackingLeave;

    internal void TrackLeave(nint hwnd)
    {
        if (_trackingLeave) return;
        var request = new MouseTracking
        {
            Size = (uint)Marshal.SizeOf<MouseTracking>(),
            Flags = 2, // TME_LEAVE
            Window = hwnd,
        };
        _trackingLeave = TrackMouseEvent(ref request);
    }

    internal void OnLeave() => _trackingLeave = false;

    internal static bool TryGetWheelClientPosition(nint hwnd, nint screenPosition, out nint clientPosition)
    {
        // A focused preview can receive wheel messages while the cursor is
        // elsewhere. Require the actual hit window before forwarding them.
        var point = new Point
        {
            X = unchecked((short)screenPosition.ToInt64()),
            Y = unchecked((short)(screenPosition.ToInt64() >> 16)),
        };
        clientPosition = 0;
        if (WindowFromPoint(point) != hwnd || !ScreenToClient(hwnd, ref point)) return false;
        clientPosition = (nint)((point.X & 0xFFFF) | (point.Y << 16));
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking
    {
        internal uint Size;
        internal uint Flags;
        internal nint Window;
        internal uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref MouseTracking request);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref Point point);
}
