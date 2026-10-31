using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewContextMenuTests()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", InteractionMembers)!.SetValue(app, true);
        app.InitializeComponent();
        try
        {
            TestPreviewContextMenus();
            TestUnfixTouchControlWindow();
            TestUnfixMappingAdmission(app);
        }
        finally { app.Shutdown(); }
        return 0;
    }

    private static void TestPreviewContextMenus()
    {
        var settings = ((App)Application.Current).UpdateSettings;
        var previousHomeKey = settings.BluetoothHomeShortcutVirtualKey;
        var previousHomeModifiers = settings.BluetoothHomeShortcutModifiers;
        settings.BluetoothHomeShortcutModifiers = 0;
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Windows.NativePreviewWindow", true)!;
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var parameters = constructor.GetParameters();
        var values = parameters.Select(p => p.DefaultValue).ToArray();
        object?[] required = [390u, 844u, "Context menu regression", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, values, required.Length);
        void Set(string name, object value) => values[Array.FindIndex(parameters, p => p.Name == name)] = value;
        var controlEnabled = false;
        var touchEnabled = false;
        Set("isReverseControlEnabled", (Func<nint, bool>)(_ => controlEnabled));
        Set("isUsbControlEnabled", (Func<bool>)(() => touchEnabled));
        var pointerType = parameters.Single(p => p.Name == "pointerInput").ParameterType;
        var pointer = Expression.Parameter(pointerType.GenericTypeArguments[0]);
        var inputs = new List<object>();
        Set("pointerInput", Expression.Lambda(pointerType,
            Expression.Call(Expression.Constant(inputs), typeof(List<object>).GetMethod("Add")!,
                Expression.Convert(pointer, typeof(object))), pointer).Compile());

        // Real HWND and WPF popup, with inert transport callbacks. No phone is needed.
        using var preview = (IDisposable)constructor.Invoke(values);
        var handle = (nint)type.GetProperty("Handle", InteractionMembers)!.GetValue(preview)!;
        var menu = (ContextMenu)InteractionField(preview, "_contextMenu")!;
        var procedure = type.GetMethod("WindowProcedure", InteractionMembers)!;
        var checks = 0;
        foreach (var layout in new[] { "normal", "fixed", "fullscreen" })
        {
            if (layout == "fixed") type.GetMethod("ToggleFixedWindow", InteractionMembers)!.Invoke(preview, null);
            if (layout == "fullscreen") type.GetMethod("ToggleFullScreen", InteractionMembers)!.Invoke(preview, null);
            foreach (var route in new[]
            {
                (Name: "wired/wireless touch, unbound", Control: true, Touch: true, Menu: true, Bound: false),
                (Name: "touch-only callback, unbound", Control: false, Touch: true, Menu: true, Bound: false),
                (Name: "wired/wireless touch, default Home binding", Control: true, Touch: true, Menu: true, Bound: true),
                (Name: "touch-only callback, default Home binding", Control: false, Touch: true, Menu: true, Bound: true),
                (Name: "Bluetooth", Control: true, Touch: false, Menu: false, Bound: true),
                (Name: "Bluetooth, unbound", Control: true, Touch: false, Menu: false, Bound: false),
                (Name: "view-only", Control: false, Touch: false, Menu: true, Bound: true),
            })
            {
                controlEnabled = route.Control;
                touchEnabled = route.Touch;
                ((App)Application.Current).UpdateSettings.BluetoothHomeShortcutVirtualKey =
                    route.Bound ? (int)Services.KeyboardShortcut.MouseRight : 0;
                foreach (var message in new[] { 0x0204, 0x0205, 0x007B, 0x00A4, 0x00A5 })
                {
                    inputs.Clear();
                    object[] args = [handle, message, (nint)0, (nint)0, false];
                    procedure.Invoke(preview, args);
                    InteractionAssert((bool)args[4] && menu.IsOpen == route.Menu,
                        $"{route.Name}/{layout}: message 0x{message:X4} must be handled with menu open={route.Menu}");
                    if (route.Menu)
                        InteractionAssert(!inputs.Any(input =>
                            input.GetType().GetProperty("Kind", InteractionMembers)!.GetValue(input)!
                                .ToString() is "ButtonDown" or "ButtonUp"),
                            $"{route.Name}/{layout}: opening the menu must not forward a mouse button to the phone");
                    menu.IsOpen = false;
                    DrainDispatcher();
                    checks++;
                }
            }
            // Restore normal cursor negotiation before native window transitions.
            controlEnabled = touchEnabled = false;
        }
        type.GetMethod("ToggleFullScreen", InteractionMembers)!.Invoke(preview, null);
        settings.BluetoothHomeShortcutVirtualKey = previousHomeKey;
        settings.BluetoothHomeShortcutModifiers = previousHomeModifiers;
        Console.WriteLine($"Preview context menu: {checks} routing checks passed across touch, Bluetooth and view-only modes.");
    }

    private static void TestUnfixTouchControlWindow()
    {
        var type = typeof(Windows.NativePreviewWindow);
        var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var parameters = constructor.GetParameters();
        foreach (var sharedCallback in new[] { false, true })
        {
            var values = parameters.Select(p => p.DefaultValue).ToArray();
            object[] required = [390u, 844u, "Unfix touch regression", (Func<nint, bool>)(_ => true),
                (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
            Array.Copy(required, values, required.Length);
            void Set(string name, object value) => values[Array.FindIndex(parameters, p => p.Name == name)] = value;
            var enabled = false;
            var pointers = new List<Controls.PreviewPointerEventArgs>();
            var keys = new List<Controls.PreviewKeyboardEventArgs>();
            Set("isUsbControlEnabled", (Func<bool>)(() => enabled));
            Set("isReverseControlEnabled", (Func<nint, bool>)(_ => sharedCallback && enabled));
            Set("pointerInput", (Action<Controls.PreviewPointerEventArgs>)pointers.Add);
            Set("keyboardInput", (Action<Controls.PreviewKeyboardEventArgs>)keys.Add);
            using var preview = (Windows.NativePreviewWindow)constructor.Invoke(values);
            SetKeyboardField(preview, "_inputForegroundWindow", (Func<nint>)(() => preview.Handle));
            nint Message(int message, nint point = 0) =>
                (nint)KeyboardCall(preview, "WindowProcedure", preview.Handle, message, (nint)0, point, false)!;
            KeyboardCall(preview, "ToggleFixedWindow");
            enabled = true;
            preview.PrepareForUsbControl();
            InteractionAssert(Message(0x84) == 1 && preview.AllowsControlInput,
                "fixed touch window must admit phone input");
            Message(0x201);
            InteractionAssert(pointers.Any(p => p.Kind == Controls.PreviewPointerKind.ButtonDown),
                "fixed touch window must forward the initial press");
            KeyboardCall(preview, "ToggleFixedWindow");
            InteractionAssert(!preview.AllowsControlInput &&
                pointers.Any(p => p.Kind == Controls.PreviewPointerKind.Reset) &&
                keys.Any(k => k.Kind == Controls.PreviewKeyboardKind.Reset) &&
                (byte)KeyboardField(preview, "_capturedMouseButtons") == 0,
                "unfix must revoke input and release held touch/keyboard state");
            var rectArgs = new object?[] { preview.Handle, null };
            type.GetMethod("GetWindowRect", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, rectArgs);
            var rect = rectArgs[1]!;
            var x = ((int)KeyboardField(rect, "Left") + (int)KeyboardField(rect, "Right")) / 2;
            var y = ((int)KeyboardField(rect, "Top") + (int)KeyboardField(rect, "Bottom")) / 2;
            InteractionAssert(Message(0x84, (nint)((y << 16) | (x & 0xffff))) == 2,
                "unfix must restore caption dragging while transport stays enabled");
            pointers.Clear(); keys.Clear();
            foreach (var message in new[] { 0x200, 0x201, 0x202, 0x20a, 0x100, 0x101 }) Message(message);
            InteractionAssert(pointers.Count == 0 && keys.Count == 0,
                "unfixed touch window must stop forwarding mouse and keyboard input");
            KeyboardCall(preview, "ToggleFullScreen");
            Message(0x201);
            pointers.Clear(); keys.Clear();
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert(pointers.Any(p => p.Kind == Controls.PreviewPointerKind.Reset) &&
                keys.Any(k => k.Kind == Controls.PreviewKeyboardKind.Reset) &&
                (byte)KeyboardField(preview, "_capturedMouseButtons") == 0,
                "returning from fullscreen to an unfixed window must release held inputs");
            pointers.Clear(); keys.Clear();
            KeyboardCall(preview, "ToggleFixedWindow");
            Message(0x201); Message(0x202);
            InteractionAssert(preview.AllowsControlInput && pointers.Count == 2,
                "fixing again must restore fresh touch input");
            enabled = false;
        }
        Console.WriteLine("PASS unfix wired/wireless control: drag restored, held inputs reset, forwarding blocked, refix resumes.");
    }

    private static void TestUnfixMappingAdmission(App app)
    {
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (ViewModels.MainViewModel)KeyboardField(main, "_viewModel");
        var manager = (Services.MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var windows = (System.Collections.IDictionary)KeyboardField(manager, "_windows");
        var deviceCtor = typeof(Models.DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (Models.DeviceViewModel)deviceCtor.Invoke(["unfix-mapping-phone", "Test phone", "iPhone13,2", "18.0", "USB", "",
            Enum.Parse(deviceCtor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", device);
        var ctor = typeof(Windows.NativePreviewWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var parameters = ctor.GetParameters();
        var values = parameters.Select(p => p.DefaultValue).ToArray();
        object[] required = [390u, 844u, "Unfix mapping admission", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, values, required.Length);
        values[Array.FindIndex(parameters, p => p.Name == "pointerInput")] = (Action<Controls.PreviewPointerEventArgs>)(_ => { });
        values[Array.FindIndex(parameters, p => p.Name == "isUsbControlEnabled")] = (Func<bool>)(() => true);
        using var preview = (Windows.NativePreviewWindow)ctor.Invoke(values);
        windows[device.Udid] = preview;
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => preview.Handle));
        try
        {
            preview.PrepareForUsbControl();
            InteractionAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "fixed independent window must admit key mappings");
            KeyboardCall(preview, "ToggleFixedWindow");
            InteractionAssert(!(bool)KeyboardCall(main, "MappingFocusAllows")!, "unfixed independent window must pause key mappings");
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "fullscreen must admit mappings on the control surface");
            KeyboardCall(preview, "ToggleFullScreen");
            InteractionAssert(!(bool)KeyboardCall(main, "MappingFocusAllows")!, "leaving fullscreen must restore unfixed mapping pause");
            KeyboardCall(preview, "ToggleFixedWindow");
            InteractionAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "refix must restore mapping admission");
            Console.WriteLine("PASS unfix key mappings: paused while unfixed, restored on refix/fullscreen.");
        }
        finally
        {
            windows.Remove(device.Udid);
            CloseWorkspaceTestWindow(main);
        }
    }
}
