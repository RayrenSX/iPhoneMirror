using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;
internal static partial class Program
{
    // Opt-in hardware integration test. Configuration contains the existing
    // private device identity and is never copied into test output.
    // Actual model startup, GATT, native window procedures and foreground guards
    // are retained. Capture discovery is replaced with known device geometry;
    // Phone-side observer events must independently confirm the UI actions.
    private static int RunAbsoluteBluetoothWindowLive(string configPath, string commandDirectory)
    {
        var config = JsonDocument.Parse(File.ReadAllText(configPath)).RootElement;
        MappingAssert(config.GetProperty("productType").GetString() == "iPhone13,1", "Expected the authorized 12 mini.");
        var udid = config.GetProperty("udid").GetString()!;
        var folder = Path.GetFullPath(commandDirectory);
        Directory.CreateDirectory(folder);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        app.UpdateSettings.BluetoothWindowMouseEnabled = true;
        app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = BluetoothHidProtocol.AbsoluteMouseReportMapVersion;
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: true);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var service = (BluetoothHidMouseService)KeyboardField(vm, "_bluetoothControl");
        var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (DeviceViewModel)ctor.Invoke([udid, "12 mini hardware test", "iPhone13,1", "18.7.8", "USB", "", Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        vm.Devices.Add(device);
        vm.SelectedDevice = device;
        SetKeyboardField(vm, "_sourceVideoWidth", 1080u);
        SetKeyboardField(vm, "_sourceVideoHeight", 2340u);
        var host = (NativePreviewHost)KeyboardField(main, "MainPreviewHost");
        var windows = (Dictionary<string, NativePreviewWindow>)KeyboardField(KeyboardField(main, "_secondaryMirrors"), "_windows");
        NativePreviewWindow? detached = null;
        var hwnd = new WindowInteropHelper(main).Handle;
        var target = host.WindowHandle;
        void WaitTask(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(45);
            while (!task.IsCompleted && DateTime.UtcNow < until) AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
            MappingAssert(task.IsCompleted, "Hardware test timed out.");
            task.GetAwaiter().GetResult();
        }
        void Focus(nint window)
        {
            if (window == hwnd)
            {
                main.Topmost = true;
                main.Activate();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                main.Topmost = false;
            }
            for (var attempt = 0; attempt < 15 && AbsoluteLiveGetForeground() != window; attempt++)
            {
                _ = AbsoluteLiveForeground(window);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            }
            MappingAssert(AbsoluteLiveGetForeground() == window, "The real desktop refused foreground ownership; no fake foreground is allowed.");
            KeyboardCall(main, "FocusControlDevice", udid, window == hwnd ? (nint)0 : window);
        }
        try
        {
            WaitTask(vm.EnableBluetoothControlAsync(udid, preserveExistingBinding: true, fromReverseControl: true));
            var until = DateTime.UtcNow.AddSeconds(35);
            while (!vm.BluetoothControlIsInputEnabled && DateTime.UtcNow < until) AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            MappingAssert(vm.BluetoothControlIsInputEnabled && service.UsesAbsoluteMouse && service.IsConnected,
                "The real model/GATT route did not become ready.");
            Focus(hwnd);
            host.Visibility = Visibility.Visible;
            main.UpdateLayout();
            host.SetPresentationVisible(true);
            Console.WriteLine("HARDWARE WINDOW READY: main; actual foreground/GATT; file commands enabled.");
            var stop = false;
            while (!stop)
            {
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                var input = Path.Combine(folder, "command.json");
                if (!File.Exists(input)) continue;
                using var command = JsonDocument.Parse(File.ReadAllText(input));
                File.Delete(input);
                var cmd = command.RootElement;
                var operation = cmd.GetProperty("operation").GetString();
                if (operation == "quit") { File.WriteAllText(Path.Combine(folder, "result.json"), "{}"); stop = true; continue; }
                if (operation == "toolbar-stop")
                {
                    Focus(hwnd);
                    var button = (System.Windows.Controls.Button)main.FindName("BluetoothControlButton");
                    MappingAssert(button.IsEnabled, "The real connected Bluetooth toolbar button is disabled.");
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var untilStopped = DateTime.UtcNow.AddSeconds(8);
                    while ((vm.IsBluetoothControlEnabled || (bool)KeyboardField(vm, "_bluetoothControlStopping")) && DateTime.UtcNow < untilStopped)
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
                    MappingAssert(!vm.IsBluetoothControlEnabled && !service.IsAdvertising && vm.BluetoothControlTargetUdid is null,
                        "The toolbar failed to close the real Bluetooth connection.");
                    File.WriteAllText(Path.Combine(folder, "result.json"), "{\"stopped\":true}");
                    stop = true;
                    continue;
                }
                if (operation == "dismiss-menu")
                {
                    MappingAssert(detached is not null, "A menu requires an independent window.");
                    ((System.Windows.Controls.ContextMenu)KeyboardField(detached!, "_contextMenu")).IsOpen = false;
                    Focus(detached!.Handle);
                    File.WriteAllText(Path.Combine(folder, "result.json"), "{\"dismissed\":true}");
                    continue;
                }
                if (operation == "detached")
                {
                    if (detached is null)
                    {
                        var shellCtor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
                        var parameters = shellCtor.GetParameters();
                        var values = parameters.Select(p => p.DefaultValue).ToArray();
                        object[] required = [1080u, 2340u, "12 mini Bluetooth hardware integration", (Func<nint, bool>)(_ => true), (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
                        Array.Copy(required, values, required.Length);
                        void Arg(string name, object value) => values[Array.FindIndex(parameters, p => p.Name == name)] = value;
                        Arg("isReverseControlEnabled", (Func<nint, bool>)(_ => vm.BluetoothControlIsInputEnabled));
                        Arg("pointerInput", (Action<PreviewPointerEventArgs>)(e => KeyboardCall(main, "OnIndependentPointerInput", udid, e)));
                        detached = (NativePreviewWindow)shellCtor.Invoke(values);
                        detached.BluetoothWindowMouseEnabled = true;
                        detached.PrepareForBluetoothControl();
                        KeyboardCall(detached, "ShowInitially");
                        windows[udid] = detached;
                        detached.ActivationChanged += active => KeyboardCall(main, "OnIndependentKeyboardFocusChanged", udid, detached.Handle, active);
                    }
                    target = detached.Handle;
                    Focus(target);
                }
                else if (operation == "main")
                {
                    if (detached is not null) { windows.Remove(udid); detached.Dispose(); detached = null; }
                    target = host.WindowHandle;
                    Focus(hwnd);
                    host.Visibility = Visibility.Visible;
                    main.UpdateLayout();
                    host.SetPresentationVisible(true);
                }
                else if (operation == "pause") WaitTask(vm.ToggleBluetoothControlAsync(udid));
                else if (operation == "blur")
                {
                    var other = new Window { Style = null, Title = "Bluetooth blur verification", Width = 300, Height = 160 };
                    other.Show();
                    other.Activate();
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
                    MappingAssert(AbsoluteLiveGetForeground() != (detached?.Handle ?? hwnd), "Focus did not leave the control window.");
                    if (KeyboardField(service, "_mousePumpTask") is Task pending) WaitTask(pending);
                    other.Close();
                    Focus(detached?.Handle ?? hwnd);
                }
                else if (operation == "relative")
                {
                    WaitTask(vm.DisableBluetoothControlAsync());
                    app.UpdateSettings.BluetoothWindowMouseEnabled = false;
                    app.UpdateSettings.BluetoothHidReportMapAcknowledgedVersion = BluetoothHidProtocol.ReportMapVersion;
                    KeyboardCall(vm, "OnPropertyChanged", nameof(MainViewModel.BluetoothWindowMouseEnabled));
                    WaitTask(vm.EnableBluetoothControlAsync(udid, preserveExistingBinding: true, fromReverseControl: true));
                    var readyUntil = DateTime.UtcNow.AddSeconds(35);
                    while (!vm.BluetoothControlIsInputEnabled && DateTime.UtcNow < readyUntil) AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                    MappingAssert(vm.BluetoothControlIsInputEnabled && !service.UsesAbsoluteMouse && service.IsConnected,
                        "Turning the experiment off did not restore the relative HID route.");
                    Focus(detached?.Handle ?? hwnd);
                }
                else if (operation == "f9")
                {
                    // UI previews intentionally omit the global hook. Enable
                    // the production hook for this explicit hardware action,
                    // redirecting all preference writes into the test folder.
                    SetKeyboardField(app, "_settingsStore", new Updater.UpdateSettingsStore(Path.Combine(folder, "settings.json")));
                    app.IsUiPreviewMode = false;
                    KeyboardCall(main, "ConfigureKeyboardShortcuts", Enum.GetValues<BluetoothShortcutAction>()
                        .ToDictionary(action => action, action => KeyboardShortcut.FromSettings(app.UpdateSettings, action)));
                    KeyboardCall(main, "ReconcileKeyboardHook");
                    MappingAssert((nint)KeyboardField(main, "_keyboardHook") != 0, "The real F9 keyboard hook was not installed.");
                    Focus(detached?.Handle ?? hwnd);
                    AbsoluteLiveKeyEvent(0x78, 0, 0, 0);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    AbsoluteLiveKeyEvent(0x78, 0, 2, 0);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
                }
                else if (operation == "rotate")
                {
                    MappingAssert(detached is not null, "Rotation requires the detached window.");
                    KeyboardCall(detached!, "Rotate", 1);
                }
                else
                {
                    Focus(detached?.Handle ?? hwnd);
                    host.Visibility = Visibility.Visible;
                    main.UpdateLayout();
                    host.SetPresentationVisible(true);
                    MappingAssert(AbsoluteLiveGetClient(target, out var rect), "No native client rectangle.");
                    var sourceWidth = detached is null ? 1080u : (uint)KeyboardField(detached, "_sourceWidth");
                    var sourceHeight = detached is null ? 2340u : (uint)KeyboardField(detached, "_sourceHeight");
                    var rotation = detached is null ? 0 : (int)KeyboardField(detached, "_rotation");
                    if ((rotation & 1) != 0) (sourceWidth, sourceHeight) = (sourceHeight, sourceWidth);
                    var content = PreviewCoordinateMapper.ContentRect(rect.Right, rect.Bottom, sourceWidth, sourceHeight);
                    var x = operation == "leave" ? -5 : (int)(content.Left + cmd.GetProperty("x").GetDouble() * content.Width);
                    var y = operation == "leave" ? -5 : (int)(content.Top + cmd.GetProperty("y").GetDouble() * content.Height);
                    var point = (nint)((x & 0xFFFF) | (y << 16));
                    var buttons = cmd.TryGetProperty("buttons", out var buttonValue) ? buttonValue.GetByte() : (byte)0;
                    var keyState = (nint)((buttons & 3) | ((buttons & 4) << 2));
                    var message = operation switch { "down" => 0x201, "up" => 0x202, "wheel" => 0x20A, "leave" => 0x2A3, _ => 0x200 };
                    var screen = new AbsoluteLivePoint { X = x, Y = y };
                    MappingAssert(AbsoluteLiveClientToScreen(target, ref screen), "No native screen point.");
                    AbsoluteLiveSetCursor(screen.X, screen.Y);
                    if (operation == "wheel")
                    {
                        point = (nint)((screen.X & 0xFFFF) | (screen.Y << 16));
                        keyState = (nint)((long)keyState | ((long)cmd.GetProperty("delta").GetInt32() << 16));
                    }
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                    if (operation is "down" or "up" or "wheel" or "right")
                        MappingAssert(AbsoluteLiveWindowFromPoint(screen) == target,
                            "The requested input point is occluded; refusing to click another application.");
                    if (operation == "down") AbsoluteLiveMouseEvent(0x0002, 0, 0, 0, 0);
                    else if (operation == "up") AbsoluteLiveMouseEvent(0x0004, 0, 0, 0, 0);
                    else if (operation == "wheel") AbsoluteLiveMouseEvent(0x0800, 0, 0, unchecked((uint)cmd.GetProperty("delta").GetInt32()), 0);
                    else if (operation == "right")
                    {
                        AbsoluteLiveMouseEvent(0x0008, 0, 0, 0, 0);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
                        AbsoluteLiveMouseEvent(0x0010, 0, 0, 0, 0);
                    }
                    else if (operation == "leave") AbsoluteLiveSendMessage(target, message, keyState, point);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
                    if (KeyboardField(service, "_mousePumpTask") is Task pending) WaitTask(pending);
                }
                Console.WriteLine($"HARDWARE WINDOW ACK {operation}; connected={service.IsConnected}; failed={service.HasTransportFailure}; absolute={service.UsesAbsoluteMouse}");
                MappingAssert(service.IsConnected && !service.HasTransportFailure, "The hardware route disconnected during the operation.");
                File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { operation, connected = service.IsConnected, failed = service.HasTransportFailure, paused = vm.BluetoothControlIsInputPaused, buttons = (byte)KeyboardField(main, "_controlButtons"), absolute = service.UsesAbsoluteMouse,
                    menu = detached is not null && ((System.Windows.Controls.ContextMenu)KeyboardField(detached, "_contextMenu")).IsOpen }));
            }
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            AbsoluteLiveMouseEvent(0x0004, 0, 0, 0, 0);
            WaitTask(vm.DisableBluetoothControlAsync());
            if (detached is not null) { windows.Remove(udid); detached.Dispose(); }
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AbsoluteLiveRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AbsoluteLivePoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool AbsoluteLiveForeground(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint AbsoluteLiveGetForeground();
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] private static extern bool AbsoluteLiveGetClient(nint hwnd, out AbsoluteLiveRect rect);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen")] private static extern bool AbsoluteLiveClientToScreen(nint hwnd, ref AbsoluteLivePoint point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")] private static extern bool AbsoluteLiveSetCursor(int x, int y);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern nint AbsoluteLiveWindowFromPoint(AbsoluteLivePoint point);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void AbsoluteLiveKeyEvent(byte key, byte scan, uint flags, nuint extraInfo);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void AbsoluteLiveMouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint AbsoluteLiveSendMessage(nint hwnd, int message, nint wParam, nint lParam);
}
