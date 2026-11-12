using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Controls;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RedrawWindowPos
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    private delegate nint RedrawSubclass(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint window, RedrawSubclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint window, RedrawSubclass callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    private static int RunPreviewResizeRedrawTests()
    {
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var mainHandle = new WindowInteropHelper(main).Handle;
        var child = InteractionCreateWindow(0, "STATIC", "", 0x40000000,
            0, 0, 100, 100, mainHandle, 0, 0, 0);
        var host = new NativePreviewHost();
        ((IDisposable)host).Dispose();
        InteractionSet(host, "_window", child);
        var observed = new List<RedrawWindowPos>();
        RedrawSubclass callback = (window, message, wParam, lParam, _, _) =>
        {
            if (message == 0x0046)
            {
                // A real HwndHost installs this callback when creating its HWND.
                // This renderer-free fixture routes messages through the same
                // production WndProc before the default native processing.
                if (window == child) KeyboardCall(host, "WndProc", window, (int)message, wParam, lParam, false);
                observed.Add(Marshal.PtrToStructure<RedrawWindowPos>(lParam));
            }
            return DefSubclassProc(window, message, wParam, lParam);
        };
        try
        {
            InteractionAssert(SetWindowSubclass(child, callback, 1, 0), "Cannot observe native preview moves.");
            foreach (var bounds in new[] { new Rect(30, 20, 220, 330), new Rect(90, 20, 220, 330),
                new Rect(90, 20, 100, 240), new Rect(5, 10, 300, 450) })
            {
                observed.Clear();
                KeyboardCall(host, "OnWindowPositionChanged", bounds);
                InteractionAssert(observed.Count > 0 && observed.All(position => (position.Flags & 0x0114) == 0x0114),
                    "Native preview move/resize can copy stale pixels or change activation/z-order: " +
                    string.Join(", ", observed.Select(position => $"{position.Flags:X}:{position.Width}x{position.Height}")));
                PreviewStyleGetWindowRect(child, out var actual);
                InteractionAssert(actual.Right - actual.Left == bounds.Width && actual.Bottom - actual.Top == bounds.Height,
                    "No-copy resize did not apply the requested native preview bounds.");
            }
            InteractionAssert(SetWindowSubclass(mainHandle, callback, 2, 0), "Cannot observe parent resizing.");
            foreach (var target in new[] { 1400d, 1600d, 1280d })
            {
                observed.Clear();
                KeyboardCall(main, "SetLightweightWindowWidthImmediately", target);
                InteractionAssert(observed.Any(position => position.Window == mainHandle && (position.Flags & 0x100) != 0),
                    "Immediate parent resize can copy stale preview pixels.");
            }
            PreviewStyleGetWindowRect(mainHandle, out var outer);
            SetKeyboardField(main, "_lightweightWindowWidthStartPixels", outer.Right - outer.Left);
            SetKeyboardField(main, "_lightweightWindowWidthTargetPixels", outer.Right - outer.Left + 180);
            SetKeyboardField(main, "_lightweightWindowStartX", outer.Left);
            SetKeyboardField(main, "_lightweightWindowTargetX", outer.Left);
            SetKeyboardField(main, "_lightweightWindowTopPixels", outer.Top);
            SetKeyboardField(main, "_lightweightWindowHeightPixels", outer.Bottom - outer.Top);
            foreach (var progress in new[] { .1, .25, .5, .75, 1d })
            {
                observed.Clear();
                KeyboardCall(main, "ApplyLightweightWindowAnimationFrame", mainHandle, progress, true);
                InteractionAssert(observed.Any(position => position.Window == mainHandle && (position.Flags & 0x100) != 0),
                    "Animated parent resize can copy stale preview pixels.");
            }
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<RedrawWindowPos>());
            try
            {
                observed.Clear();
                KeyboardCall(host, "OnWindowPositionChanged", new Rect(5, 10, 300, 450));
                InteractionAssert(observed.Count == 0, "Identical child bounds triggered another native position transaction.");
                SetKeyboardField(main, "_lightweightWindowAnimationActive", true);
                Marshal.StructureToPtr(new RedrawWindowPos { Window = mainHandle, Flags = 0x14 }, memory, false);
                KeyboardCall(main, "WindowMessageHook", mainHandle, 0x0046, (nint)0, memory, false);
                InteractionAssert((Marshal.PtrToStructure<RedrawWindowPos>(memory).Flags & 0x100) != 0,
                    "OS-driven parent resizes can still copy stale pixels during presentation/animation.");
                Marshal.StructureToPtr(new RedrawWindowPos { Window = mainHandle, Flags = 0x15 }, memory, false);
                KeyboardCall(main, "WindowMessageHook", mainHandle, 0x0046, (nint)0, memory, false);
                InteractionAssert((Marshal.PtrToStructure<RedrawWindowPos>(memory).Flags & 0x100) == 0,
                    "A pure window move invalidated the retained client pixels.");
            }
            finally { SetKeyboardField(main, "_lightweightWindowAnimationActive", false); Marshal.FreeHGlobal(memory); }
            Console.WriteLine("PASS: actual child position/size messages, immediate parent widths and animated resize frames all disable stale client pixel copying; bounds, activation and z-order retained.");
            return 0;
        }
        finally
        {
            RemoveWindowSubclass(child, callback, 1); RemoveWindowSubclass(mainHandle, callback, 2);
            InteractionSet(host, "_window", (nint)0); InteractionDestroyWindow(child);
            CloseWorkspaceTestWindow(main); app.Shutdown(); GC.KeepAlive(callback);
        }
    }
}
