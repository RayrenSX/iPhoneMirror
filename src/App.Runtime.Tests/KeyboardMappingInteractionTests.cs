using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunKeyboardMappingInteractionTests(string output, bool lifecycleOnly = false)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        try
        {
            if (!lifecycleOnly)
            {
                TestMappingCaptureTransactions(); TestMappingWindowsKeyChords(); TestMappingVisualCoordinates();
                TestMappingNativePreviewFocus(app); TestMappingOverlayInteraction(app, output);
            }
            TestMappingSurfaceLifecycle(app);
            Console.WriteLine("PASS keyboard mapping interaction suite and fixture cleanup.");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void ActivateMappingTestWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        window.Activate(); MappingSetForeground(hwnd);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (MappingGetForeground() != hwnd)
            Console.WriteLine($"FOCUS WAIT: test window {hwnd}, foreground {MappingGetForeground()}, enabled={MappingIsEnabled(hwnd)}. Activate the test window to continue.");
        while (MappingGetForeground() != hwnd && clock.Elapsed.TotalSeconds < 30)
            AdvanceDispatcher(TimeSpan.FromMilliseconds(10));
        MappingAssert(MappingGetForeground() == hwnd,
            $"Test could not acquire foreground: expected {hwnd}, actual {MappingGetForeground()}; another window owns keyboard input.");
    }
    private static void TestMappingNativePreviewFocus(App app)
    {
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        main.ShowInTaskbar = true;
        main.Title = "Keyboard mapping interaction test";
        var mainHandle = new WindowInteropHelper(main).Handle;
        var host = KeyboardField(main, "MainPreviewHost");
        using var native = new HwndSource(new HwndSourceParameters("Mapping native focus regression")
        { ParentWindow = mainHandle, WindowStyle = 0x50000000, Width = 100, Height = 100 });
        var manager = new KeyboardMappingWindow(new(), _ => null, _ => null, _ => null, () => { }, () => "MappingOff")
            { Owner = main, ShowInTaskbar = true };
        try
        {
            // Reproduce the real native-child/WPF-logical-focus split, with a
            // manager still open and the settings panel still visible.
            SetKeyboardField(host, "_window", native.Handle);
            SetKeyboardField(main, "_mappingWindow", manager);
            SetKeyboardField(main, "_isSettingsPanelVisible", true);
            manager.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            MappingAssert(!(bool)KeyboardCall(main, "MappingFocusAllows")!, "Active manager did not pause mappings.");
            ActivateMappingTestWindow(main);
            System.Windows.Input.Keyboard.ClearFocus();
            MappingSetFocus(native.Handle);
            MappingAssert((bool)KeyboardCall(main, "MappingFocusAllows")!,
                "Native preview remained paused by null WPF focus, visible settings or inactive manager.");
            ActivateMappingTestWindow(manager);
            MappingAssert(!(bool)KeyboardCall(main, "MappingFocusAllows")!, "Returning to settings failed to pause mappings.");
            ActivateMappingTestWindow(main); MappingSetFocus(native.Handle);
            MappingAssert((bool)KeyboardCall(main, "MappingFocusAllows")!, "Mapping did not recover after returning to native preview.");
            Console.WriteLine("PASS actual native-child focus with null WPF focus, open inactive manager, visible settings and repeated focus transitions.");
        }
        finally
        {
            SetKeyboardField(main, "_mappingWindow", null);
            SetKeyboardField(host, "_window", (nint)0);
            manager.Close(); CloseWorkspaceTestWindow(main);
        }
    }
    private static void TestMappingCaptureTransactions()
    {
        var capture = new KeyboardMappingCapture();
        var callbacks = new Queue<Action>();
        var observed = new List<MappedKey>();
        foreach (var key in new[] { MappingTestKey, new MappedKey(0x5B, 0x5B, true), new MappedKey(0xA4, 0x38, false),
            new MappedKey(0x0D, 0x1C, true), new MappedKey(0x1B, 1, false) })
        {
            capture.Begin(observed.Add);
            var count = observed.Count;
            MappingAssert(capture.Process(key, true, true, callbacks.Enqueue), "Capture leaked down.");
            MappingAssert(capture.Process(key, true, true, callbacks.Enqueue), "Capture leaked repeat.");
            MappingAssert(observed.Count == count && capture.Waiting, "Capture completed before release.");
            MappingAssert(capture.Process(key, false, true, callbacks.Enqueue), "Capture leaked release.");
            callbacks.Dequeue()();
            MappingAssert(observed.Count == count + 1 && observed[^1] == key && !capture.Waiting && !capture.HasHeldKeys,
                "Capture did not transition to a confirmed key.");
        }
        capture.Begin(observed.Add);
        capture.Process(MappingTestKey, true, true, callbacks.Enqueue);
        capture.Process(MappingTestKey, false, true, callbacks.Enqueue);
        var before = observed.Count;
        capture.Cancel();
        capture.Begin(observed.Add);
        callbacks.Dequeue()();
        MappingAssert(observed.Count == before && capture.Waiting, "A stale dispatcher capture completed a newer transaction.");
        // WPF activation can move away from the editor while a physical key
        // is held. The paired release must still complete the transaction.
        capture.Process(MappingTestKey, true, true, callbacks.Enqueue);
        MappingAssert(capture.Process(MappingTestKey, false, false, callbacks.Enqueue),
            "Capture lost a release after focus moved away from the editor.");
        callbacks.Dequeue()();
        MappingAssert(observed.Count == before + 1 && observed[^1] == MappingTestKey && !capture.Waiting,
            "Capture did not complete after focus moved away from the editor.");
        capture.Process(MappingTestKey, true, true, callbacks.Enqueue);
        capture.Cancel();
        MappingAssert(capture.Process(MappingTestKey, false, false, callbacks.Enqueue) && !capture.HasHeldKeys,
            "Deactivation lost the captured key's paired release.");
        var keys = new KeyboardMappingKeyState();
        keys.Process(MappingTestKey, true, false, true, false, true, [MappingEntry()]);
        keys.Disable();
        MappingAssert(keys.Process(MappingTestKey, true, false, false, false, false, []).Suppress,
            "Disabling mappings leaked a suppressed autorepeat before release.");
        MappingAssert(keys.HasSuppressedKeys && keys.Process(MappingTestKey, false, false, false, false, false, []).Suppress &&
            !keys.HasSuppressedKeys, "Disabling mappings leaked a suppressed key's release.");
        keys.Process(MappingTestKey, true, false, true, false, false, [MappingEntry()]);
        keys.Disable();
        MappingAssert(keys.Process(MappingTestKey, true, false, true, false, false, [MappingEntry()]).Mapping is not null,
            "A release while the hook was uninstalled poisoned the next enable.");
        Console.WriteLine("PASS capture release, repeat, Win/Alt/Esc/numpad, stale dispatcher callbacks, cancellation and disable pairing.");
    }

    private static void TestMappingWindowsKeyChords()
    {
        foreach (var win in new[] { new MappedKey(0x5B, 0x5B, true), new MappedKey(0x5C, 0x5C, true) })
        {
            var entry = MappingEntry() with { Key = win };
            var state = new KeyboardMappingWindowsKey();
            var replays = new List<MappedKey[]>();
            bool Replay(MappedKey[] keys) { replays.Add(keys); return true; }
            MappingAssert(state.Process(win, true, true, false, [entry], Replay).Suppress, "Win down opened Start.");
            MappingAssert(state.Process(win, true, true, false, [entry], Replay).Mapping is null, "Win repeat executed.");
            var up = state.Process(win, false, true, false, [entry], Replay);
            MappingAssert(up.Suppress && up.Mapping == entry && !state.HasHeldKey && replays.Count == 0,
                "Standalone Win did not execute once with paired suppression.");
            foreach (var chord in new[] { new MappedKey(0x44, 0x20, false), new MappedKey(0x09, 0x0F, false),
                new MappedKey(0x4C, 0x26, false), new MappedKey(0x41, 0x1E, false) })
            {
                state.Process(win, true, true, false, [entry], Replay);
                var result = state.Process(chord, true, true, true, [entry], Replay);
                MappingAssert(result.Suppress && result.Mapping is null && replays[^1].SequenceEqual(new[] { win, chord }),
                    "Win chord was not handed back as an ordered Windows chord.");
                MappingAssert(!state.Process(chord, false, true, true, [entry], Replay).Suppress, "Chord release was lost.");
                up = state.Process(win, false, true, false, [entry], Replay);
                MappingAssert(!up.Suppress && up.Mapping is null, "Win+D/Tab/L/letter executed a standalone mapping.");
            }
            state.Process(win, true, true, false, [entry], Replay);
            state.Cancel();
            up = state.Process(win, false, false, false, [], Replay);
            MappingAssert(up.Suppress && up.Mapping is null && !state.HasHeldKey, "Focus/disable cancellation lost Win state.");
        }
        Console.WriteLine("PASS left/right Win standalone, repeat, D/Tab/L/letter chord replay policy, cancellation and release. No secure OS shortcut was injected.");
    }

    private static void TestMappingVisualCoordinates()
    {
        var rect = PreviewCoordinateMapper.ContentRect(800, 600, 1000, 2000);
        MappingAssert(rect == new Rect(250, 0, 300, 600), "Portrait letterbox bounds wrong.");
        MappingAssert(PreviewCoordinateMapper.Normalize(249, 300, 800, 600, 1000, 2000) is null,
            "A black bar became a touch position.");
        var center = PreviewCoordinateMapper.Normalize(400, 300, 800, 600, 1000, 2000);
        MappingAssert(center == (.5, .5), "Content center is not device center.");
        foreach (var dimensions in new[] { (1170u, 2532u), (2532u, 1170u), (1668u, 2388u), (2388u, 1668u) })
        foreach (var rotation in new[] { 0, 1, 2, 3 })
        foreach (var direction in Enum.GetValues<BluetoothMouseDirection>())
        foreach (var reverseX in new[] { false, true })
        foreach (var reverseY in new[] { false, true })
        foreach (var dpi in new[] { 1d, 1.25, 1.5, 2 })
        {
            var surface = new MappingPreviewSurface(0, 0, "test", 1, dimensions.Item1, dimensions.Item2,
                rotation, direction, direction, reverseX, reverseY);
            var point = PreviewCoordinateMapper.Project(.23, .71, 800 * dpi, 600 * dpi, surface.Width, surface.Height);
            var normalized = PreviewCoordinateMapper.Normalize(point.X, point.Y, 800 * dpi, 600 * dpi, surface.Width, surface.Height)!.Value;
            var device = surface.ToDevice(normalized.X, normalized.Y);
            var preview = surface.ToPreview(device.X, device.Y);
            MappingAssert(Math.Abs(preview.X - .23) < 1e-10 && Math.Abs(preview.Y - .71) < 1e-10,
                "Mouse/pick/marker roundtrip drifted with rotation, resolution or DPI.");
        }
        foreach (var action in Enum.GetValues<MappedTouchAction>().Where(a => a >= MappedTouchAction.SwipeUp))
        {
            var constrained = KeyboardMappingOverlayWindow.ConstrainEnd(action, (.5, .5), (.2, .8));
            MappingAssert(action switch {
                MappedTouchAction.SwipeUp => constrained == (.5, .5),
                MappedTouchAction.SwipeDown => constrained == (.5, .8),
                MappedTouchAction.SwipeLeft => constrained == (.2, .5),
                _ => constrained == (.5, .5), }, "Directional picking reversed the selected gesture.");
        }
        Console.WriteLine("PASS shared letterbox rejection, content coordinates, iPhone/iPad portrait/landscape, four rotations, direction/reversal, 100/125/150/200% scaling.");
    }

    private static void TestMappingOverlayInteraction(App app, string output)
    {
        var owner = new Window { Width = 820, Height = 680, Content = new Border { Background = Brushes.Black }, ShowInTaskbar = false };
        app.MainWindow = owner;
        owner.Show();
        var handle = new WindowInteropHelper(owner).Handle;
        var surface = new MappingPreviewSurface(handle, handle, "overlay-test", 1, 1000, 2000, 0,
            BluetoothMouseDirection.Up, BluetoothMouseDirection.Right, false, false);
        var overlay = new KeyboardMappingOverlayWindow(surface);
        var inputTrace = new List<string>();
        overlay.MouseLeftButtonDown += (_, _) => inputTrace.Add("down");
        overlay.MouseLeftButtonUp += (_, _) => inputTrace.Add("up");
        overlay.GotMouseCapture += (_, _) => inputTrace.Add("capture");
        overlay.LostMouseCapture += (_, _) => inputTrace.Add("lost");
        overlay.Deactivated += (_, _) => inputTrace.Add("deactivated");
        var entry = MappingEntry() with { DeviceCoordinates = true, X = .23, Y = .71 };
        try
        {
            overlay.Update(surface, [entry]);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
            var hwnd = new WindowInteropHelper(overlay).Handle;
            MappingAssert(!overlay.IsHitTestVisible && (MappingGetStyle(hwnd, -20).ToInt64() & 0x08000020) == 0x08000020,
                "Normal overlay can intercept clicks or activation.");
            MappingAssert(MappingSendMessage(hwnd, 0x84, 0, 0) == -1, "Normal overlay does not return HTTRANSPARENT.");
            SaveWindowRender(overlay, Path.Combine(output, "overlay-persistent.png"));
            var pixels = MappingOverlayPixelCount(overlay);
            MappingAssert(pixels > 20, "Enabled mapping marker was not rendered.");
            overlay.Update(surface, [entry with { Enabled = false }]);
            MappingAssert(MappingOverlayPixelCount(overlay) == 0, "Disabled mapping left visible pixels.");
            overlay.Update(surface, []);
            MappingAssert(MappingOverlayPixelCount(overlay) == 0, "Deleted mapping left visible pixels.");
            overlay.Update(surface, [entry]);
            MappingAssert(MappingOverlayPixelCount(overlay) > 20, "Re-enabled mapping did not render immediately.");
            owner.Width = 1100; owner.Height = 800;
            owner.UpdateLayout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            overlay.Update(surface, [entry]);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
            MappingAssert(overlay.ActualWidth > 1000, "Overlay failed to follow owner client resize.");
            KeyboardMappingEntry? picked = null;
            var completions = 0;
            overlay.BeginPick(new(entry, surface, result => { picked = result; ++completions; }));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            MappingAssert(overlay.IsPicking && overlay.IsHitTestVisible, "Picking did not enable input.");
            SaveWindowRender(overlay, Path.Combine(output, "overlay-picking.png"));
            // Real native mouse messages enter WPF's input pipeline. Coordinates
            // stay below the small toolbar and inside the known video rectangle.
            var x = (int)(overlay.ActualWidth * .5);
            var y = (int)(overlay.ActualHeight * .6);
            var screen = overlay.PointToScreen(new Point(x, y));
            var origin = overlay.PointToScreen(new Point());
            var px = (int)(screen.X - origin.X); var py = (int)(screen.Y - origin.Y);
            MappingSetCursorPos((int)screen.X, (int)screen.Y);
            MappingSendMessage(hwnd, 0x200, 0, (nint)((py << 16) | px));
            DrainDispatcher();
            MappingSendMessage(hwnd, 0x201, 1, (nint)((py << 16) | px));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            MappingSendMessage(hwnd, 0x202, 0, (nint)((py << 16) | px));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            MappingAssert(completions == 1 && picked is { DeviceCoordinates: true } && !overlay.IsPicking,
                $"Native click did not complete and exit visual picking: completions={completions}, picking={overlay.IsPicking}, result={picked}, foreground={MappingGetForeground()}, overlay={hwnd}, trace={string.Join(',', inputTrace)}.");
            MappingAssert(Math.Abs(picked!.X - .5) < .01 && Math.Abs(picked.Y - .6) < .01,
                $"Visual click position differs from the shared mouse coordinates: {picked.X}, {picked.Y}.");
            foreach (var action in Enum.GetValues<MappedTouchAction>())
            {
                inputTrace.Clear();
                var current = entry with { Action = action };
                overlay.BeginPick(new(current, surface, result => picked = result));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                var end = action switch
                {
                    MappedTouchAction.SwipeUp => (.5, .35), MappedTouchAction.SwipeDown => (.5, .8),
                    MappedTouchAction.SwipeLeft => (.3, .6), MappedTouchAction.SwipeRight => (.7, .6),
                    MappedTouchAction.Swipe => (.7, .35), _ => (.5, .6),
                };
                var pointerDown = false;
                void Pointer(int message, double nx, double ny)
                {
                    var point = PreviewCoordinateMapper.Project(nx, ny, (int)KeyboardField(overlay, "_pixelWidth"),
                        (int)KeyboardField(overlay, "_pixelHeight"), surface.Width, surface.Height);
                    var basePoint = overlay.PointToScreen(new Point());
                    MappingSetCursorPos((int)(basePoint.X + point.X), (int)(basePoint.Y + point.Y));
                    if (message == 0x201) pointerDown = true;
                    if (message == 0x202) pointerDown = false;
                    MappingSendMessage(hwnd, message, pointerDown ? 1 : 0,
                        (nint)(((int)point.Y << 16) | (int)point.X));
                }
                // BeginPick changes a click-through HWND into an input surface.
                // Deliver the move/enter transition before the button event,
                // as the normal Windows mouse queue does after moving here.
                Pointer(0x200, .5, .6);
                DrainDispatcher();
                Pointer(0x201, .5, .6);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                Pointer(0x200, end.Item1, end.Item2);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                Pointer(0x202, end.Item1, end.Item2);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
                MappingAssert(!overlay.IsPicking && picked is not null && picked.Action == action && picked.Validate() is null,
                    $"{action} native picking did not finish with a valid mapping: picking={overlay.IsPicking}, result={picked?.Action}, error={picked?.Validate()}, foreground={MappingGetForeground()}, overlay={hwnd}, trace={string.Join(',', inputTrace)}.");
                if (current.IsSwipe)
                    MappingAssert(Math.Abs(picked!.EndX - end.Item1) < .01 && Math.Abs(picked.EndY - end.Item2) < .01 &&
                        picked.DurationMs >= 80, $"{action} picking lost drag endpoint or measured duration.");
            }
            overlay.BeginPick(new(entry, surface, result => { picked = result; ++completions; }));
            overlay.Update(surface with { Rotation = 1 }, [entry]);
            MappingAssert(completions == 2 && picked is null && !overlay.IsPicking, "Rotation left an invalid pick active.");
            overlay.BeginPick(new(entry, surface with { Rotation = 1 }, result => { picked = result; ++completions; }));
            owner.Hide(); overlay.Update(surface with { Rotation = 1 }, []);
            MappingAssert(completions == 3 && !overlay.IsVisible, "Hidden/disconnected surface retained a ghost picker.");
            Console.WriteLine("PASS real owned HWND overlay, rendered enabled/disabled/deleted markers, click-through, resize, native click and all eight action picks, timed drag, automatic exit, rotation cancellation and hidden-owner cleanup.");
        }
        finally { overlay.Close(); owner.Close(); }
    }

    private static int MappingOverlayPixelCount(Window window)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)window.ActualWidth); var height = Math.Max(1, (int)window.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var data = new byte[width * height * 4];
        bitmap.CopyPixels(data, width * 4, 0);
        var count = 0;
        for (var i = 3; i < data.Length; i += 4) if (data[i] > 5) ++count;
        return count;
    }

    private static void TestMappingSurfaceLifecycle(App app)
    {
        var snapshot = app.UpdateSettings.Clone();
        var main = CreateWorkspaceTestWindow(app);
        var vm = (ViewModels.MainViewModel)KeyboardField(main, "_viewModel");
        var sessions = (DeviceSessionManager)KeyboardField(vm, "_sessions");
        var previews = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var windows = (Dictionary<string, NativePreviewWindow>)KeyboardField(previews, "_windows");
        var overlays = (Dictionary<nint, KeyboardMappingOverlayWindow>)KeyboardField(main, "_mappingOverlays");
        var host = (Controls.NativePreviewHost)main.FindName("MainPreviewHost");
        var constructor = typeof(Models.DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        Models.DeviceViewModel Device(string id, string model) => (Models.DeviceViewModel)constructor.Invoke([
            id, id, model, "18", "USB", "", Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
        var phone = Device("mapping-lifecycle-phone", "iPhone15,2");
        var pad = Device("mapping-lifecycle-pad", "iPad14,3");
        using var phoneHandle = new Interop.NativeSessionHandle(123, ownsHandle: false);
        using var padHandle = new Interop.NativeSessionHandle(124, ownsHandle: false);
        var phoneSession = new Models.DeviceCaptureState { Udid = phone.Udid, Handle = phoneHandle };
        var padSession = new Models.DeviceCaptureState { Udid = pad.Udid, Handle = padHandle };
        NativePreviewWindow Shell(string title, uint width, uint height)
        {
            var ctor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
            var values = ctor.GetParameters().Select(p => p.DefaultValue).ToArray();
            object[] required = [width, height, title, (Func<nint, bool>)(_ => true),
                (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
            Array.Copy(required, values, required.Length);
            var shell = (NativePreviewWindow)ctor.Invoke(values);
            KeyboardCall(shell, "ShowInitially");
            return shell;
        }
        NativePreviewWindow? first = null, second = null;
        try
        {
            SetKeyboardField(vm, "_selectedDevice", phone);
            SetKeyboardField(vm, "_isCapturing", true);
            SetKeyboardField(vm, "_sourceVideoWidth", 1170u); SetKeyboardField(vm, "_sourceVideoHeight", 2532u);
            host.Visibility = Visibility.Visible; host.Width = 500; host.Height = 600;
            main.UpdateLayout(); host.SetPresentationVisible(true);
            sessions.Set(phoneSession); sessions.Set(padSession);
            var settings = new KeyboardMappingSettings { Enabled = true, Mappings = [MappingEntry() with { DeviceCoordinates = true }] };
            MappingAssert(KeyboardCall(main, "ApplyKeyboardMapping", settings) is null, "Could not enable mappings.");
            MappingAssert(overlays.Count == 1 && overlays.Values.Single().Surface.Window == host.WindowHandle,
                "Main native preview did not receive its persistent overlay.");
            first = Shell("Mapping phone preview test", 1170, 2532);
            second = Shell("Mapping iPad preview test", 1668, 2388);
            windows.Add(phone.Udid, first); windows.Add(pad.Udid, second);
            KeyboardCall(main, "RefreshMappingOverlays");
            MappingAssert(overlays.Count == 2 && overlays.ContainsKey(first.Handle) && !overlays.ContainsKey(second.Handle),
                "Current-device overlays leaked onto an unrelated independent preview.");
            settings.Mappings[0] = settings.Mappings[0] with { Enabled = false };
            KeyboardCall(main, "ApplyKeyboardMapping", settings);
            MappingAssert(overlays.Values.All(o => MappingOverlayPixelCount(o) == 0), "Disabling a row failed to update both previews.");
            settings.Mappings[0] = settings.Mappings[0] with { Enabled = true, X = .72, Y = .37 };
            KeyboardCall(main, "ApplyKeyboardMapping", settings);
            MappingAssert(overlays.Values.All(o => MappingOverlayPixelCount(o) > 0), "Editing/re-enabling failed to redraw both previews.");
            first.ToggleFullScreen(); KeyboardCall(main, "RefreshMappingOverlays");
            MappingAssert(overlays[first.Handle].IsVisible, "Fullscreen lost the independent overlay.");
            first.ToggleFullScreen();
            SetKeyboardField(vm, "_selectedDevice", pad);
            SetKeyboardField(vm, "_sourceVideoWidth", 1668u); SetKeyboardField(vm, "_sourceVideoHeight", 2388u);
            KeyboardCall(main, "OnMappingContextChanged", nameof(ViewModels.MainViewModel.SelectedDevice));
            MappingAssert(overlays.Count == 2 && !overlays.ContainsKey(first.Handle) && overlays.ContainsKey(second.Handle) &&
                overlays.Values.All(o => o.Surface.Device == pad.Udid && o.Surface.Width == 1668),
                "Switching devices retained the phone's window or resolution.");
            second.SetSourceDimensions(2388, 1668);
            SetKeyboardField(vm, "_sourceVideoWidth", 2388u); SetKeyboardField(vm, "_sourceVideoHeight", 1668u);
            KeyboardCall(main, "RefreshMappingOverlays");
            MappingAssert(overlays.Values.All(o => o.Surface.Width == 2388 && o.Surface.Height == 1668), "Landscape left stale geometry.");
            sessions.Remove(pad.Udid); KeyboardCall(main, "RefreshMappingOverlays");
            MappingAssert(overlays.Count == 0, "Disconnect left ghost overlays despite a missing current session.");
            sessions.Set(padSession); KeyboardCall(main, "RefreshMappingOverlays");
            MappingAssert(overlays.Count == 2, "Reconnect failed to restore current-device overlays.");
            settings.Enabled = false; KeyboardCall(main, "ApplyKeyboardMapping", settings);
            MappingAssert(overlays.Count == 0, "Turning mappings off retained overlays.");
            Console.WriteLine("PASS actual main HwndHost and independent native preview shells, immediate edits/toggles, fullscreen, multi-device selection, orientation, disconnect/reconnect and global-off cleanup (inert renderer/session fixtures).");
        }
        finally
        {
            windows.Clear(); first?.Dispose(); second?.Dispose();
            sessions.Remove(phone.Udid); sessions.Remove(pad.Udid);
            SetKeyboardField(vm, "_isCapturing", false);
            // This fixture creates a real main D3D renderer. CloseWorkspaceTestWindow
            // deliberately bypasses asynchronous app shutdown; release its core
            // here so process teardown cannot strand a live graphics worker.
            AwaitMapping(vm.ShutdownAsync());
            CloseWorkspaceTestWindow(main); app.RestoreUpdateSettings(snapshot);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint MappingGetStyle(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint MappingSendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")] private static extern bool MappingSetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SetFocus")] private static extern nint MappingSetFocus(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool MappingSetForeground(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern nint MappingGetForeground();
    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")] private static extern bool MappingIsEnabled(nint hwnd);
}
