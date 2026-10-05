using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Controls;

// Both native preview surfaces use WM_TOUCH; Windows keeps each contact
// targeted at its original HWND until up, even when it leaves the client area.
internal static class NativeTouchInput
{
    internal const int WmTouch = 0x0240;

    internal static bool IsPromotedMouseMessage(int message) =>
        message is >= 0x0200 and <= 0x020E &&
        (unchecked((ulong)GetMessageExtraInfo().ToInt64()) & 0xFFFFFF80UL) == 0xFF515780UL;

    internal static void Dispatch(nint hwnd, nint wParam, nint handle, bool enabled,
        int width, int height, uint sourceWidth, uint sourceHeight, int rotation,
        Action<PreviewPointerEventArgs> send)
    {
        try
        {
            var count = (int)(wParam.ToInt64() & 0xffff);
            if (!enabled || count == 0) return;
            var inputs = new TouchInput[count];
            if (!GetTouchInputInfo(handle, count, inputs, Marshal.SizeOf<TouchInput>())) return;
            foreach (var input in inputs)
            {
                var point = new Point { X = input.X / 100, Y = input.Y / 100 };
                if (!ScreenToClient(hwnd, ref point)) continue;
                var kind = (input.Flags & 4) != 0 ? PreviewPointerKind.TouchUp :
                    (input.Flags & 2) != 0 ? PreviewPointerKind.TouchDown : PreviewPointerKind.TouchMove;
                if ((input.Flags & 7) == 0) continue;
                send(new PreviewPointerEventArgs(kind, point.X, point.Y, 0, 0,
                    width, height, sourceWidth, sourceHeight, rotation, input.Id, hwnd));
            }
        }
        finally { CloseTouchInputHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TouchInput
    {
        internal int X, Y;
        internal nint Source;
        internal uint Id, Flags, Mask, Time;
        internal nuint ExtraInfo;
        internal uint ContactWidth, ContactHeight;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterTouchWindow(nint hwnd, uint flags = 0);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTouchInputInfo(nint handle, int count, [Out] TouchInput[] inputs, int size);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseTouchInputHandle(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref Point point);
    [DllImport("user32.dll")]
    private static extern nint GetMessageExtraInfo();
}
