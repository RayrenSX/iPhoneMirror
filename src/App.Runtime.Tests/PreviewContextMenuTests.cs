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
        try { TestPreviewContextMenus(); }
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
                (Name: "wired/wireless touch, Home binding", Control: true, Touch: true, Menu: false, Bound: true),
                (Name: "touch-only callback, Home binding", Control: false, Touch: true, Menu: false, Bound: true),
                (Name: "Bluetooth", Control: true, Touch: false, Menu: false, Bound: true),
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
}
