using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void TestMainPreviewPointerRoute(MainWindow window, DeviceViewModel device,
        MemoryStream packets, string context)
    {
        var vm = KeyboardField(window, "_viewModel");
        var preview = (FrameworkElement)window.FindName("MainPreviewHost");
        var foregroundProvider = KeyboardField(window, "_keyboardForegroundWindow");
        var previousWidth = KeyboardField(vm, "_sourceVideoWidth");
        var previousHeight = KeyboardField(vm, "_sourceVideoHeight");
        var previousTitle = window.Title;
        var previousShowInTaskbar = window.ShowInTaskbar;
        var main = new WindowInteropHelper(window).Handle;
        nint foreground = main;
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => foreground));
        SetKeyboardField(vm, "_selectedDevice", device);
        SetKeyboardField(vm, "_sourceVideoWidth", 390u);
        SetKeyboardField(vm, "_sourceVideoHeight", 844u);
        KeyboardCall(window, "FocusControlDevice", device.Udid, (nint)0);
        preview.Visibility = Visibility.Visible;
        preview.Width = 300;
        preview.Height = 600;
        window.UpdateLayout();
        KeyboardCall(preview, "SetPresentationVisible", true);
        var hwnd = (nint)KeyboardField(preview, "WindowHandle");
        try
        {
            InteractionAssert(hwnd != 0 && (bool)KeyboardField(preview, "CapturePointerInput"),
                $"{context}: main preview must own an enabled native input surface.");
            InteractionAssert(PreviewPointerGetClientRect(hwnd, out var bounds) && bounds.Right > 10,
                $"{context}: native preview must have usable client dimensions.");
            var x = bounds.Right / 2;
            var y = bounds.Bottom / 2;
            void Send(int message, int px, int py) => PreviewStyleSendMessage(hwnd, message,
                message is 0x0201 or 0x0200 ? (nint)1 : 0,
                (nint)((py << 16) | (px & 0xffff)));
            void Click()
            {
                Send(0x0201, x, y);
                Send(0x0202, x, y);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            }

            // Use SendMessage through the actual HwndHost subclass and production
            // event subscription. Only the bridge writer and foreground snapshot
            // are faked; no phone receives input and no global mouse input is used.
            window.ShowInTaskbar = true;
            window.Title = "iPhoneMirror preview pointer regression";
            ActivateMappingTestWindow(window);
            var toolbar = (FrameworkElement)window.FindName("VersionButton");
            toolbar.Focus();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            InteractionAssert((bool)KeyboardCall(window, "IsMainKeyboardEditorFocused")!,
                $"{context}: toolbar focus must initially keep keyboard input local.");
            packets.SetLength(0);
            Click();
            var touches = ReadPreviewTouchPackets(packets);
            InteractionAssert(touches.Select(p => p.Action).SequenceEqual(new[] { "down", "up" }) &&
                touches.All(p => Math.Abs(p.X - 0.5) < 0.01 && Math.Abs(p.Y - 0.5) < 0.01),
                $"{context}: a main-preview click must send down/up at the displayed center.");
            InteractionAssert(!(bool)KeyboardCall(window, "IsMainKeyboardEditorFocused")!,
                $"{context}: clicking the native preview must restore keyboard focus from the toolbar.");

            packets.SetLength(0);
            Send(0x0201, x, y);
            Send(0x0200, x, y + 30);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            Send(0x0202, x, y + 30);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            touches = ReadPreviewTouchPackets(packets);
            InteractionAssert(touches.Select(p => p.Action).SequenceEqual(new[] { "down", "move", "up" }) &&
                touches[1].Y > touches[0].Y,
                $"{context}: a main-preview drag must preserve the contact and movement.");

            packets.SetLength(0);
            Send(0x0201, x, y);
            PreviewStyleSendMessage(hwnd, 0x001F, 0, 0); // WM_CANCELMODE
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            InteractionAssert(ReadPreviewTouchPackets(packets).Select(p => p.Action)
                .SequenceEqual(new[] { "down", "up" }),
                $"{context}: losing capture must release the active touch.");

            packets.SetLength(0);
            foreground = 0;
            Click();
            InteractionAssert(ReadPreviewTouchPackets(packets).Length == 0,
                $"{context}: background main-preview clicks must stay blocked.");
            foreground = main;
            Click();
            InteractionAssert(ReadPreviewTouchPackets(packets).Length == 2,
                $"{context}: touch must resume after returning to the main window.");
            TestFivePointPreview(window, device, packets, hwnd, context);
            Console.WriteLine($"{context}: native main-preview click, drag, cancellation and focus checks passed.");
        }
        finally
        {
            KeyboardCall(preview, "ReleasePointerCapture");
            KeyboardCall(preview, "SetPresentationVisible", false);
            preview.ClearValue(UIElement.VisibilityProperty);
            preview.ClearValue(FrameworkElement.WidthProperty);
            preview.ClearValue(FrameworkElement.HeightProperty);
            SetKeyboardField(vm, "_sourceVideoWidth", previousWidth);
            SetKeyboardField(vm, "_sourceVideoHeight", previousHeight);
            SetKeyboardField(window, "_keyboardForegroundWindow", foregroundProvider);
            window.Title = previousTitle;
            window.ShowInTaskbar = previousShowInTaskbar;
        }
    }

    private static (string Action, double X, double Y)[] ReadPreviewTouchPackets(MemoryStream packets)
    {
        using var copy = new MemoryStream(packets.ToArray());
        using var reader = new BinaryReader(copy);
        var result = new List<(string, double, double)>();
        while (copy.Position < copy.Length)
        {
            using var doc = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
            if (!doc.RootElement.TryGetProperty("points", out var points)) continue;
            foreach (var point in points.EnumerateArray())
                result.Add((point.GetProperty("action").GetString()!,
                    point.GetProperty("normalizedX").GetDouble(), point.GetProperty("normalizedY").GetDouble()));
        }
        return result.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewPointerRect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PreviewPointerGetClientRect(nint hwnd, out PreviewPointerRect rect);
}
