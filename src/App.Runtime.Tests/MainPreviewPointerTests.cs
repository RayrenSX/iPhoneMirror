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
            TestNativeMouseShortcuts(window, packets, hwnd, x, y, context);
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

    private static void TestNativeMouseShortcutRoute(MainWindow window, DeviceViewModel device,
        MemoryStream packets, string context)
    {
        var preview = (Controls.NativePreviewHost)window.FindName("MainPreviewHost");
        var foreground = KeyboardField(window, "_keyboardForegroundWindow");
        var focus = KeyboardField(window, "_keyboardFocusedWindow");
        try
        {
            SetKeyboardField(window, "_keyboardForegroundWindow",
                (Func<nint>)(() => new WindowInteropHelper(window).Handle));
            SetKeyboardField(window, "_keyboardFocusedWindow", (Func<nint>)(() => preview.WindowHandle));
            KeyboardCall(window, "FocusControlDevice", device.Udid, (nint)0);
            preview.Visibility = Visibility.Visible;
            preview.Width = 300;
            preview.Height = 600;
            window.UpdateLayout();
            KeyboardCall(preview, "SetPresentationVisible", true);
            InteractionAssert(preview.WindowHandle != 0 && preview.CapturePointerInput,
                $"{context}: native shortcut test requires an enabled HWND.");
            TestNativeMouseShortcuts(window, packets, preview.WindowHandle, 100, 200, context);
            TestIndependentNativeMouseShortcuts(window, device, packets, context);
            Console.WriteLine($"{context}: main right/middle shortcuts and independent middle shortcut/menu priority passed.");
        }
        finally
        {
            preview.ReleasePointerCapture();
            preview.SetPresentationVisible(false);
            preview.ClearValue(UIElement.VisibilityProperty);
            preview.ClearValue(FrameworkElement.WidthProperty);
            preview.ClearValue(FrameworkElement.HeightProperty);
            SetKeyboardField(window, "_keyboardForegroundWindow", foreground);
            SetKeyboardField(window, "_keyboardFocusedWindow", focus);
        }
    }

    private static void TestIndependentNativeMouseShortcuts(MainWindow window, DeviceViewModel device,
        MemoryStream packets, string context)
    {
        var type = typeof(Windows.NativePreviewWindow);
        var constructor = type.GetConstructors(KeyboardTestMembers).Single();
        var parameters = constructor.GetParameters();
        var args = parameters.Select(p => p.DefaultValue).ToArray();
        object?[] required = [390u, 844u, "Mouse shortcut regression", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, args, required.Length);
        void Set(string name, object value) => args[Array.FindIndex(parameters, p => p.Name == name)] = value;
        var touchEnabled = true;
        Set("isUsbControlEnabled", (Func<bool>)(() => touchEnabled));
        Set("isReverseControlEnabled", (Func<nint, bool>)(_ => !touchEnabled));
        Set("pointerInput", (Action<Controls.PreviewPointerEventArgs>)(e =>
            KeyboardCall(window, "OnIndependentPointerInput", device.Udid, e)));
        Set("keyboardInput", (Action<Controls.PreviewKeyboardEventArgs>)(e =>
            KeyboardCall(window, "OnIndependentKeyboardInput", device.Udid, e)));
        using var preview = (Windows.NativePreviewWindow)constructor.Invoke(args);
        var handle = preview.Handle;
        var foreground = KeyboardField(window, "_keyboardForegroundWindow");
        SetKeyboardField(preview, "_inputForegroundWindow", (Func<nint>)(() => handle));
        SetKeyboardField(window, "_keyboardForegroundWindow", (Func<nint>)(() => handle));
        SetKeyboardField(window, "_activeControlWindow", handle);
        SetKeyboardField(window, "_activeControlUdid", device.Udid);
        try
        {
            TestNativeMouseShortcuts(window, packets, handle, 100, 200, context + "/independent");
            // Exercise Bluetooth's native right-button forwarding with the same
            // in-memory action transport; no real Bluetooth device is needed.
            touchEnabled = false;
            TestNativeMouseShortcuts(window, packets, handle, 100, 200, context + "/independent-bluetooth");
            touchEnabled = true;
            var bindings = (Dictionary<Services.BluetoothShortcutAction, Services.KeyboardShortcut>)
                KeyboardField(window, "_bluetoothShortcuts");
            var saved = new Dictionary<Services.BluetoothShortcutAction, Services.KeyboardShortcut>(bindings);
            try
            {
                foreach (var action in bindings.Keys.ToArray()) bindings[action] = Services.KeyboardShortcut.Unbound;
                KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
                bindings[Services.BluetoothShortcutAction.Home] = Services.KeyboardShortcut.HomeDefault;
                KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
                packets.SetLength(0);
                var position = (nint)((200 << 16) | 100);
                PreviewStyleSendMessage(handle, 0x0204, 0, position);
                PreviewStyleSendMessage(handle, 0x0205, 0, position);
                var menu = (System.Windows.Controls.ContextMenu)KeyboardField(preview, "_contextMenu");
                InteractionAssert(menu.IsOpen && ReadShortcutPackets(packets).Length == 0,
                    $"{context}: independent right-click must prefer the menu even with Home bound.");
                menu.IsOpen = false;
            }
            finally
            {
                bindings.Clear();
                foreach (var pair in saved) bindings[pair.Key] = pair.Value;
                KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
            }
        }
        finally
        {
            SetKeyboardField(window, "_activeControlWindow", (nint)0);
            SetKeyboardField(window, "_activeControlUdid", null);
            SetKeyboardField(window, "_keyboardForegroundWindow", foreground);
        }
    }

    private static void TestNativeMouseShortcuts(MainWindow window, MemoryStream packets,
        nint hwnd, int x, int y, string context)
    {
        var bindings = (Dictionary<Services.BluetoothShortcutAction, Services.KeyboardShortcut>)
            KeyboardField(window, "_bluetoothShortcuts");
        var saved = new Dictionary<Services.BluetoothShortcutAction, Services.KeyboardShortcut>(bindings);
        try
        {
            foreach (var action in bindings.Keys.ToArray()) bindings[action] = Services.KeyboardShortcut.Unbound;
            foreach (var (button, down, up) in new[]
            {
                (Services.KeyboardShortcut.MouseRight, 0x0204, 0x0205),
                (Services.KeyboardShortcut.MouseMiddle, 0x0207, 0x0208),
            })
            foreach (var modifiers in new[] { 0u, Services.KeyboardShortcut.Control })
            {
                if (context.EndsWith("/independent") && button == Services.KeyboardShortcut.MouseRight) continue;
                KeyboardCall(window, "FocusControlDevice", KeyboardField(window, "ActiveInputDeviceUdid"),
                    context.Contains("/independent") ? hwnd : (nint)0);
                bindings[Services.BluetoothShortcutAction.Home] = new(modifiers, button);
                KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
                AwaitMapping((Task)KeyboardField(window, "_keyboardHandoff"));
                packets.SetLength(0);
                var router = (Services.KeyboardInputRouter)KeyboardField(window, "_keyboardRouter");
                var modifier = new Services.MappedKey(0xA2, 0, false);
                Services.KeyboardPressRoute Resolve(Services.MappedKey key, bool chord) =>
                    throw new InvalidOperationException("Mouse shortcut leaked its Ctrl prefix.");
                if (modifiers != 0) router.RouteEvent(modifier, true, Resolve, (_, _) => { });
                var position = (nint)((y << 16) | (x & 0xffff));
                // Deliver a complete click before queued shortcut execution, including
                // the synchronous WM_CAPTURECHANGED produced by ReleaseCapture.
                PreviewStyleSendMessage(hwnd, down, 0, position);
                var generation = router.Generation;
                PreviewStyleSendMessage(hwnd, up, 0, position);
                InteractionAssert(router.Generation == generation,
                    $"{context}/{button}: normal mouse release reset keyboard ownership.");
                AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                var frames = ReadShortcutPackets(packets);
                InteractionAssert(frames.Length == 2 && frames.All(f =>
                    f.GetProperty("kind").GetString() == "button_event" &&
                    f.GetProperty("usageCode").GetInt32() == 0x40),
                    $"{context}/{button}/{modifiers}: native mouse shortcut was cancelled or leaked input.");
                if (modifiers != 0)
                {
                    packets.SetLength(0);
                    PreviewStyleSendMessage(hwnd, down, 0, position);
                    PreviewStyleSendMessage(hwnd, up, 0, position);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                    InteractionAssert(ReadShortcutPackets(packets).Length == 2,
                        $"{context}/{button}: holding Ctrl across clicks lost the second shortcut.");
                    router.RouteEvent(modifier, false, Resolve, (_, _) => { });
                }
                else
                {
                    foreach (var cancel in new[] { 0x001F, 0x0215 }) // cancel mode / stolen capture
                    {
                        packets.SetLength(0);
                        PreviewStyleSendMessage(hwnd, down, 0, position);
                        PreviewStyleSendMessage(hwnd, cancel, 0, 0);
                        PreviewStyleSendMessage(hwnd, up, 0, position);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
                        InteractionAssert(!ReadShortcutPackets(packets).Any(f =>
                            f.GetProperty("kind").GetString() == "button_event" &&
                            f.GetProperty("state").GetString() == "down"),
                            $"{context}/{button}: cancelled mouse click executed a stale shortcut.");
                        // An uncaptured right release may open the independent menu.
                        if (context.EndsWith("/independent"))
                            PreviewStyleSendMessage(hwnd, 0x001F, 0, 0);
                    }
                }
            }
        }
        finally
        {
            bindings.Clear();
            foreach (var pair in saved) bindings[pair.Key] = pair.Value;
            KeyboardCall(window, "ConfigureKeyboardShortcuts", bindings);
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
