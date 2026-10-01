using System.Runtime.InteropServices;

namespace IPhoneMirror.App.Interop;

internal static class NativeCursor
{
    internal static void SetArrow()
    {
        // Native HWNDs can receive WM_SETCURSOR without an active WPF mouse
        // input provider. Set the Win32 cursor directly in that case too.
        // LoadCursor returns a shared system handle; do not destroy it or
        // change ShowCursor's display count when updating only the shape.
        var arrow = LoadCursorW(0, 32512); // IDC_ARROW
        if (arrow != 0) _ = SetCursor(arrow);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetCursor(nint cursor);
}
