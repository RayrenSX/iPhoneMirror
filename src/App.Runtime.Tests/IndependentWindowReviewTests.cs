using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunIndependentWindowReview(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        LocalizationService.SetLanguage("zh-CN");
        ThemeService.Apply(AppTheme.Light);
        var store = new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json"));
        SetKeyboardField(app, "_settingsStore", store);
        app.UpdateSettings.SettingShortcuts = [new()
        {
            Setting = PreviewShortcutSetting.FixWindow, Trigger = SettingShortcutTrigger.Toggle, VirtualKey = 0x75, Value = 0,
        }];
        store.Save(app.UpdateSettings);
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var phone = DeviceViewModel.FromNative(new NativeDeviceInfo
        {
            Udid = "independent-window-review", Name = "iPhone · 测试设备", ProductType = "iPhone15,2",
            State = ConnectionState.Ready,
        }, DeviceAppearance.Unknown);
        SetKeyboardField(vm, "_selectedDevice", phone);
        var manager = (MultiDevicePreviewManager)KeyboardField(main, "_secondaryMirrors");
        var windows = (IDictionary)KeyboardField(manager, "_windows");
        try
        {
            CaptureIndependentWindowEntries(main, app, phone, store, output);
            using var preview = CreateIndependentReviewPreview(usbControl: true);
            windows.Add(phone.Udid, preview);
            try
            {
                KeyboardCall(preview, "ShowWithInitialLayout", MultiDevicePreviewManager.GetSavedLayout(phone.Udid));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                VerifyIndependentReviewBounds(preview);
                preview.SetSourceDimensions(844, 390);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                VerifyIndependentReviewBounds(preview);
                preview.ToggleFullScreen();
                preview.ToggleFullScreen();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                VerifyIndependentReviewBounds(preview);
                VerifyIndependentDesktopShortcuts(main, app, phone, preview);
            }
            finally { windows.Remove(phone.Udid); }
            using var reopened = CreateIndependentReviewPreview();
            KeyboardCall(reopened, "ShowWithInitialLayout", store.Load().IndependentWindowLayouts[phone.Udid]);
            VerifyIndependentReviewBounds(reopened);
            using var fixedOnly = CreateIndependentReviewPreview();
            KeyboardCall(fixedOnly, "ShowWithInitialLayout", new IndependentWindowLayout(Fixed: true));
            InteractionAssert(UnfixGetWindowRect(fixedOnly.Handle, out var before), "Automatic layout did not provide window bounds.");
            fixedOnly.SetSourceDimensions(844, 390);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(UnfixGetWindowRect(fixedOnly.Handle, out var after) && after.Equals(before),
                "A locked auto-sized window moved or resized when the source changed.");
            using var automaticControl = CreateIndependentReviewPreview();
            KeyboardCall(automaticControl, "ShowInitially");
            automaticControl.PrepareForUsbControl();
            InteractionAssert(UnfixGetWindowRect(automaticControl.Handle, out var controlBefore),
                "The automatically fixed control window has no bounds.");
            automaticControl.SetSourceDimensions(844, 390);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(UnfixGetWindowRect(automaticControl.Handle, out var controlAfter) &&
                controlAfter.Right - controlAfter.Left != controlBefore.Right - controlBefore.Left,
                "Automatic reverse control must retain its existing source auto-resize behavior.");
            using var media = CreateIndependentReviewPreview(managed: true);
            KeyboardCall(media, "ShowInitially");
            media.ToggleFixedWindow();
            InteractionAssert(UnfixGetWindowRect(media.Handle, out var mediaBefore), "The media preview has no bounds.");
            _ = MappingSendMessage(media.Handle, 0x0112, (nint)0xF030, 0);
            InteractionAssert(UnfixGetWindowRect(media.Handle, out var mediaAfter) && mediaAfter.Equals(mediaBefore),
                "Maximizing a locked media window changed the bounds.");
            Console.WriteLine("PASS independent-window startup restored after reopen, source rotation and fullscreen roundtrip.");
            VerifyRememberedWindowLayout(main, app, phone.Udid, store);
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static void CaptureIndependentWindowEntries(MainWindow main, App app, DeviceViewModel phone,
        UpdateSettingsStore store, string output)
    {
        T Find<T>(string name) where T : FrameworkElement => (T)main.FindName(name);
        void CapturePanel(string name)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            var panel = Find<Border>("ControlPanelContent");
            var image = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen())
                drawing.DrawRectangle(main.TryFindResource("WindowBackgroundBrush") as Brush ?? Brushes.White,
                    null, new Rect(panel.RenderSize));
            image.Render(background);
            image.Render(panel);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(output, name));
            encoder.Save(stream);
        }

        KeyboardCall(main, "OnOpenProjectionSettingsClick", main, new RoutedEventArgs());
        KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
        var layout = Find<StackPanel>("IndependentWindowLayoutEditor");
        Find<TextBox>("IndependentLayoutX").Text = "100";
        Find<TextBox>("IndependentLayoutY").Text = "180";
        Find<TextBox>("IndependentLayoutWidth").Text = "420";
        Find<TextBox>("IndependentLayoutHeight").Text = "620";
        Find<CheckBox>("IndependentLayoutFixed").IsChecked = true;
        app.IsUiPreviewMode = false;
        try { Click(FindVisualDescendants<Button>(layout).Single()); }
        finally { app.IsUiPreviewMode = true; }
        InteractionAssert(store.Load().IndependentWindowLayouts[phone.Udid] == new IndependentWindowLayout(100, 180, 420, 620, true),
            "The layout save button did not persist the review fixture.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
        layout.BringIntoView();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        var projection = Find<ScrollViewer>("ProjectionSettingsScrollViewer");
        projection.ScrollToVerticalOffset(projection.VerticalOffset +
            ((FrameworkElement)layout.Parent).TranslatePoint(new Point(), projection).Y - 6);
        CapturePanel("92-startup-layout.png");
        var useLast = Find<CheckBox>("IndependentLayoutUseLast");
        useLast.IsChecked = true;
        useLast.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        CapturePanel("92-restore-last-layout.png");
        useLast.IsChecked = false;

        Find<RadioButton>("ControlSettingsTab").IsChecked = true;
        AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
        Find<ScrollViewer>("ControlSettingsScrollViewer").ScrollToTop();
        var open = Find<Button>("OpenShortcutSettingsButton");
        open.BringIntoView();
        CapturePanel("shortcut-entry.png");
        Click(open);
        var editor = (ShortcutSettingsWindow)KeyboardField(main, "_shortcutSettingsWindow");
        try
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            var moreSettings = (TextBlock)editor.FindName("MoreSettingsHeading");
            var scroller = (ScrollViewer)editor.FindName("ShortcutListScrollViewer");
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + moreSettings.TranslatePoint(new Point(), scroller).Y - 6);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            SaveWindowRender(editor, Path.Combine(output, "91-lock-shortcut.png"));
            var allow = (TextBox)editor.FindName("PreviewHotkeysAllow");
            var block = (TextBox)editor.FindName("PreviewHotkeysBlock");
            var save = (Button)editor.FindName("SaveButton");
            allow.Text = "Win\nAlt+Tab\nCtrl+Shift+F8";
            block.Text = "alt+tab";
            Click(save);
            InteractionAssert(editor.IsVisible && editor.StatusText == LocalizationService.Format("PreviewWindowsHotkeysInvalid", "alt+tab") &&
                store.Load().PreviewWindowsHotkeyBlockList.Length == 0, "A duplicate rule closed the editor or modified the saved hotkeys.");
            block.Text = "Ctrl+Shift+F7";
            using var externalBinding = new HwndSource(new HwndSourceParameters("Existing global F9")
                { ParentWindow = new nint(-3) });
            var registered = ReviewRegisterHotKey(externalBinding.Handle, 1, 0, KeyboardShortcut.Default.VirtualKey);
            app.IsUiPreviewMode = false;
            try { Click(save); }
            finally
            {
                app.IsUiPreviewMode = true;
                if (registered) _ = ReviewUnregisterHotKey(externalBinding.Handle, 1);
            }
            var saved = store.Load();
            InteractionAssert(!editor.IsVisible && saved.PreviewWindowsHotkeyAllowList.Split('\n').Length == 3 &&
                saved.Clone().PreviewWindowsHotkeyBlockList == "Ctrl+Shift+F7" &&
                saved.SettingShortcuts.Single().Setting == PreviewShortcutSetting.FixWindow,
                $"The shortcut dialog did not save both types of shortcuts: {editor.StatusText}.");
        }
        finally { if (editor.IsVisible) editor.Close(); }

        Click(open);
        var reopened = (ShortcutSettingsWindow)KeyboardField(main, "_shortcutSettingsWindow");
        try
        {
            var block = (TextBox)reopened.FindName("PreviewHotkeysBlock");
            InteractionAssert(block.Text == "Ctrl+Shift+F7", "Reopening the shortcut dialog lost the saved hotkey rules.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            ((ScrollViewer)reopened.FindName("ShortcutListScrollViewer")).ScrollToEnd();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            SaveWindowRender(reopened, Path.Combine(output, "93-windows-hotkeys.png"));
            block.Text = "PrintScreen";
        }
        finally { reopened.Close(); }
        InteractionAssert(store.Load().PreviewWindowsHotkeyBlockList == "Ctrl+Shift+F7", "Closing the dialog persisted an unsaved edit.");

        static void Click(Button button)
        {
            InteractionAssert(button.IsEnabled, "The review control is disabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private static NativePreviewWindow CreateIndependentReviewPreview(bool usbControl = false, bool managed = false)
    {
        var ctor = typeof(NativePreviewWindow).GetConstructors(KeyboardTestMembers).Single();
        var arguments = ctor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
        object[] required = [390u, 844u, "iPhoneMirror — 独立窗口验证", (Func<nint, bool>)(_ => true),
            (Action<nint>)(_ => { }), (Func<nint, bool>)(_ => true), 0UL, 0d, 1d];
        Array.Copy(required, arguments, required.Length);
        if (usbControl)
            arguments[Array.FindIndex(ctor.GetParameters(), parameter => parameter.Name == "isUsbControlEnabled")] =
                (Func<bool>)(() => true);
        if (managed)
            arguments[Array.FindIndex(ctor.GetParameters(), parameter => parameter.Name == "managedContent")] =
                new Border { Background = Brushes.Black };
        return (NativePreviewWindow)ctor.Invoke(arguments);
    }

    private static void VerifyRememberedWindowLayout(MainWindow main, App app, string udid, UpdateSettingsStore store)
    {
        var useLast = (CheckBox)main.FindName("IndependentLayoutUseLast");
        var custom = (StackPanel)main.FindName("IndependentLayoutCustomFields");
        var customX = (TextBox)main.FindName("IndependentLayoutX");
        var preset = store.Load().IndependentWindowLayouts[udid];
        var firstBounds = new IndependentWindowLayout(145, 125, 450, 575);
        var fullscreenBounds = new IndependentWindowLayout(160, 130, 470, 600, Fixed: true);
        var minimizedBounds = new IndependentWindowLayout(175, 145, 480, 610);

        app.IsUiPreviewMode = false;
        try
        {
            useLast.IsChecked = true;
            customX.Text = "invalid";
            Save();
            InteractionAssert(!custom.IsEnabled && store.Load().IndependentWindowLayouts[udid] == preset &&
                MultiDevicePreviewManager.GetStartupLayout(udid) is null,
                "Restore mode did not disable custom inputs, retain the preset, or fall back to automatic placement.");

            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            useLast.IsChecked = false;
            Save();
            InteractionAssert(custom.IsEnabled && MultiDevicePreviewManager.GetStartupLayout(udid) == preset,
                "Switching back did not restore the saved custom layout.");
            using (var first = Open())
            {
                first.ToggleFixedWindow();
                Move(first, firstBounds);
            }
            InteractionAssert(store.Load().IndependentWindowLayoutHistoryByDevice[udid] == new IndependentWindowLayoutHistory(false, firstBounds),
                "Closing in custom mode did not save the actual bounds separately.");

            useLast.IsChecked = true;
            Save();
            app.RestoreUpdateSettings(store.Load());
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(udid.ToUpperInvariant()) == firstBounds &&
                MultiDevicePreviewManager.GetStartupLayout(udid + "-other") is null,
                "Remembered per-device mode and bounds did not survive a disk reload.");
            using (var fromHistory = Open())
            {
                AssertBounds(fromHistory, firstBounds);
                Move(fromHistory, fullscreenBounds);
                fromHistory.ToggleFixedWindow();
                fromHistory.ToggleFullScreen();
                InteractionAssert(fromHistory.IsFullScreen, "The test window was not full screen.");
            }
            InteractionAssert(store.Load().IndependentWindowLayoutHistoryByDevice[udid].LastLayout == fullscreenBounds,
                "Closing full screen did not keep the normal bounds and lock state.");
            using (var fromFullscreen = Open())
            {
                AssertBounds(fromFullscreen, fullscreenBounds);
                fromFullscreen.ToggleFixedWindow();
                Move(fromFullscreen, minimizedBounds);
                _ = MappingSendMessage(fromFullscreen.Handle, 0x0112, (nint)0xF020, 0);
                InteractionAssert((bool)typeof(NativePreviewWindow).GetMethod("IsIconic",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null, [fromFullscreen.Handle])!, "The test window was not minimized.");
                fromFullscreen.HideForShutdown();
            }
            InteractionAssert(store.Load().IndependentWindowLayoutHistoryByDevice[udid].LastLayout == minimizedBounds,
                "Closing a minimized and hidden preview overwrote its normal bounds.");
            using (var restored = Open())
                AssertBounds(restored, minimizedBounds);

            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            InteractionAssert(useLast.IsChecked == true && !custom.IsEnabled, "Selection did not restore the remembered mode.");
            useLast.IsChecked = false;
            Save();
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(udid) == preset &&
                store.Load().IndependentWindowLayoutHistoryByDevice[udid].LastLayout == minimizedBounds,
                "Custom mode did not take precedence over the stored last bounds.");
            Console.WriteLine("PASS independent-window layout mode: mutually exclusive fields, automatic fallback, disk reload, two device keys, normal/fullscreen/minimized closure.");
        }
        finally { app.IsUiPreviewMode = true; }

        void Save() => KeyboardCall(main, "OnSaveIndependentWindowLayout", main, new RoutedEventArgs());

        NativePreviewWindow Open()
        {
            var window = CreateIndependentReviewPreview();
            KeyboardCall(window, "ShowWithInitialLayout", MultiDevicePreviewManager.GetStartupLayout(udid));
            MultiDevicePreviewManager.TrackLayoutHistory(udid, window);
            return window;
        }

        static void Move(NativePreviewWindow window, IndependentWindowLayout target) =>
            InteractionAssert(InteractionSetWindowPos(window.Handle, 0,
                target.X!.Value, target.Y!.Value, target.Width!.Value, target.Height!.Value, 0x14),
                "Could not move the test window.");

        static void AssertBounds(NativePreviewWindow window, IndependentWindowLayout target) =>
            InteractionAssert(UnfixGetWindowRect(window.Handle, out var rect) && rect.Left == target.X && rect.Top == target.Y &&
                rect.Right - rect.Left == target.Width && rect.Bottom - rect.Top == target.Height &&
                (bool)KeyboardField(window, "_isFixed") == target.Fixed, "Opening did not restore the actual saved layout.");
    }

    private static void VerifyIndependentReviewBounds(NativePreviewWindow preview)
    {
        InteractionAssert(UnfixGetWindowRect(preview.Handle, out var rect) &&
            rect.Left == 100 && rect.Top == 180 && rect.Right - rect.Left == 420 && rect.Bottom - rect.Top == 620 &&
            (bool)KeyboardField(preview, "_isFixed"),
            $"Expected the locked 420×620 preset at (100,180), got {rect.Left},{rect.Top},{rect.Right},{rect.Bottom}.");
    }

    private static void VerifyIndependentDesktopShortcuts(MainWindow main, App app, DeviceViewModel phone, NativePreviewWindow preview)
    {
        using var hotkeySink = new HwndSource(new HwndSourceParameters("Independent preview hotkey receiver") { ParentWindow = new nint(-3) });
        const int allowedId = 1, blockedId = 2;
        const uint allowed = 0x77, blocked = 0x76;
        const uint modifiers = KeyboardShortcut.Control | KeyboardShortcut.Shift;
        var allowedCount = 0;
        var blockedCount = 0;
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var controls = (IDictionary)KeyboardField(vm, "_deviceControls");
        using var packets = new MemoryStream();
        using var writer = new StreamWriter(packets, leaveOpen: true);
        var host = new UsbTouchBridgeHost();
        var bridge = (DirectUsbInputBridge)KeyboardField(host, "_bridge");
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(host, "<State>k__BackingField", ReverseControlState.Ready);
        var control = new DeviceControlSession(phone.Udid)
        {
            AppleUdid = phone.Udid, WiredBridge = host, WiredEnabled = true, WiredConnected = true,
        };
        control.Router.Begin(phone.Udid, ReverseControlMode.Usb);
        hotkeySink.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message != 0x0312) return 0;
            if (wParam == allowedId) allowedCount++;
            if (wParam == blockedId) blockedCount++;
            handled = true;
            return 0;
        });
        InteractionAssert(ReviewRegisterHotKey(hotkeySink.Handle, allowedId, modifiers, allowed) &&
            ReviewRegisterHotKey(hotkeySink.Handle, blockedId, modifiers, blocked), "Windows hotkey test registration failed.");
        try
        {
            app.IsUiPreviewMode = false;
            ActivateMappingTestWindow(main);
            // Baseline from the real OS with the independent-window policy inactive.
            SendChord(allowed);
            SendChord(blocked);
            InteractionAssert(allowedCount == 1 && blockedCount == 1, "Windows did not deliver the baseline registered hotkeys.");
            controls[phone.Udid] = control;
            preview.Activate();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(MappingGetForeground() == preview.Handle, "Unable to activate the independent native window.");
            KeyboardCall(main, "FocusControlDevice", phone.Udid, preview.Handle);
            KeyboardCall(main, "ReconcileKeyboardHook");
            KeyboardCall(main, "TryEnterDirectKeyboardInputMode");
            WaitReviewTask((Task)KeyboardField(main, "_keyboardHandoff"));
            var router = (KeyboardInputRouter)KeyboardField(main, "_keyboardRouter");
            InteractionAssert((nint)KeyboardField(main, "_keyboardHook") != 0 &&
                (bool)KeyboardCall(main, "CanForwardControlKeyboard", phone.Udid, preview.Handle)! &&
                router.Mode == KeyboardInputMode.Direct && vm.CaptureDirectKeyboardRoute(phone.Udid) is not null,
                "Production hook was not installed on the foreground device window.");
            SendReviewKeys([(0x41, true), (0x41, false)]);
            WaitReviewTask(Task.WhenAll((HashSet<Task>)KeyboardField(main, "_keyboardSends")));
            InteractionAssert(MappingFrames(packets).Any(frame => frame.TryGetProperty("usages", out var usages) &&
                usages.EnumerateArray().Any(usage => usage.GetInt32() == 4)), "The USB bridge fixture did not receive the ordinary A key.");
            packets.SetLength(0);
            SendChord(allowed);
            SendChord(blocked);
            WaitReviewTask(Task.WhenAll((HashSet<Task>)KeyboardField(main, "_keyboardSends")));
            InteractionAssert(allowedCount == 2 && blockedCount == 1,
                $"Expected one allow and zero block messages, counts are {allowedCount}, {blockedCount}.");
            InteractionAssert(!MappingFrames(packets).Any(frame => frame.TryGetProperty("usages", out var usages) &&
                usages.GetArrayLength() != 0), "An explicitly allowed or blocked chord sent input to the phone.");
            SendReviewKeys([(0xA2, true), (0xA0, true), ((ushort)allowed, true), ((ushort)allowed, false),
                (0x41, true), (0x41, false), (0xA0, false), (0xA2, false)]);
            WaitReviewTask(Task.WhenAll((HashSet<Task>)KeyboardField(main, "_keyboardSends")));
            var ordinaryChord = MappingFrames(packets)
                .Where(frame => frame.TryGetProperty("usages", out _))
                .Select(frame => frame.GetProperty("usages").EnumerateArray().Select(usage => usage.GetInt32()).ToHashSet()).ToArray();
            InteractionAssert(allowedCount == 3 && ordinaryChord.Any(usages => usages.SetEquals([4, 224, 225])),
                $"The hotkey followed by Ctrl+Shift+A failed: Windows count={allowedCount}, HID={string.Join(" | ", ordinaryChord.Select(keys => string.Join(",", keys)))}.");
            VerifyShellHotkeyForwarding(main);
            SendReviewKeys([(0x75, true), (0x75, false)]);
            InteractionAssert(!(bool)KeyboardField(preview, "_isFixed"), "Native F6 did not unlock the independent window.");
            SendReviewKeys([(0x75, true), (0x75, false)]);
            InteractionAssert((bool)KeyboardField(preview, "_isFixed"), "Native F6 did not lock the independent window.");
            ActivateMappingTestWindow(main);
            SendChord(blocked);
            InteractionAssert(blockedCount == 2, "The blocked chord remained blocked after leaving the independent window.");
            Console.WriteLine("PASS real Windows hook and WM_HOTKEY during direct USB input: allow, block, phone isolation, focus scope and F6 lock/unlock.");
        }
        finally
        {
            app.IsUiPreviewMode = true;
            KeyboardCall(main, "ReconcileKeyboardHook");
            controls.Remove(phone.Udid);
            SetKeyboardField(bridge, "_stdin", null);
            _ = ReviewUnregisterHotKey(hotkeySink.Handle, allowedId);
            _ = ReviewUnregisterHotKey(hotkeySink.Handle, blockedId);
        }

        static void SendChord(uint key) =>
            SendReviewKeys([(0xA2, true), (0xA0, true), ((ushort)key, true), ((ushort)key, false), (0xA0, false), (0xA2, false)]);
    }

    private static void VerifyShellHotkeyForwarding(MainWindow main)
    {
        const int win = 0x5B, alt = 0xA4, tab = 0x09;
        var received = new List<(int Key, bool Down)>();
        ReviewHookProc observer = (code, message, pointer) =>
        {
            if (code >= 0 && Marshal.ReadInt32(pointer) is win or alt or tab)
            {
                received.Add((Marshal.ReadInt32(pointer), message is 0x0100 or 0x0104));
                return 1; // observe the shell sequence without leaving Start or the window switcher open
            }
            return ReviewCallNextHook(0, code, message, pointer);
        };
        var hook = ReviewSetHook(13, observer, ReviewGetModule(null), 0);
        InteractionAssert(hook != 0, "Couldn't observe Windows-bound Win and Alt+Tab sequences.");
        try
        {
            KeyboardCall(main, "RefreshKeyboardHook"); // place production in front of the observer
            SendReviewKeys([(win, true), (win, false)]);
            SendReviewKeys([(alt, true), (tab, true), (tab, false), (alt, false)]);
            InteractionAssert(received.SequenceEqual([(win, true), (win, false), (alt, true), (tab, true), (tab, false), (alt, false)]),
                $"Windows-bound Win/Alt+Tab events arrived out of order: {string.Join(", ", received)}.");
        }
        finally
        {
            _ = ReviewUnhook(hook);
            GC.KeepAlive(observer);
        }
    }

    private static void SendReviewKeys((ushort Key, bool Down)[] events)
    {
        var inputs = events.Select(entry => new ReviewKeyInput
        {
            Type = 1, Key = entry.Key, Flags = (entry.Down ? 0u : 2u) | (entry.Key == 0x5B ? 1u : 0u),
        }).ToArray();
        var sending = Task.Run(() => ReviewSendInput((uint)inputs.Length, inputs, Marshal.SizeOf<ReviewKeyInput>()));
        WaitReviewTask(sending);
        InteractionAssert(sending.Result == inputs.Length, "Windows rejected the review key sequence.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(160));
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct ReviewKeyInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort Key;
        [FieldOffset(12)] internal uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput")]
    private static extern uint ReviewSendInput(uint count, ReviewKeyInput[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey")]
    private static extern bool ReviewRegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    private static extern bool ReviewUnregisterHotKey(nint window, int id);

    private delegate nint ReviewHookProc(int code, nint message, nint pointer);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint ReviewGetModule(string? name);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static extern nint ReviewSetHook(int kind, ReviewHookProc callback, nint module, uint threadId);

    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern nint ReviewCallNextHook(nint hook, int code, nint message, nint pointer);

    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
    private static extern bool ReviewUnhook(nint hook);
}
