using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IPhoneMirror.App;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunPreviewClickThroughTests(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "review-settings.json")));
        typeof(LocalizationService).GetMethod("ApplyLanguage",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, ["zh-CN", false, true]);
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var main = CreateWorkspaceTestWindow(app);
        try
        {
            SetKeyboardField(main, "_startupServicesStarted", true);
            main.Hide();
            string targetPath = Path.GetFullPath(Path.Combine(output, $"target-{Guid.NewGuid():N}"));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--preview-click-target");
            start.ArgumentList.Add(targetPath);
            using var target = Process.Start(start) ?? throw new InvalidOperationException("Cannot start input target.");
            try
            {
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(targetPath) && deadline.ElapsedMilliseconds < 5000)
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
                var targetHwnd = (nint)long.Parse(File.ReadAllText(targetPath));
                InteractionAssert(NativePreviewWindow.TryCreateAndShowForContent(
                    new Border { Background = Brushes.SteelBlue }, 360, 720, "Media preview fixture",
                    () => true, _ => { }, () => 1, () => { }, () => { }, out var media),
                    "Cannot create a managed media preview.");
                using (media)
                    VerifyClickThrough(main, media!, targetHwnd, targetPath, prePinned: true, output);

                var core = (NativeCore)KeyboardField((MainViewModel)KeyboardField(main, "_viewModel"), "_core");
                using var session = core.CreateExternalSession("preview-click-through-fixture", false, 0).Handle ??
                    throw new InvalidOperationException("Cannot create an external stream.");
                var frame = new byte[360 * 720 * 3 / 2];
                frame.AsSpan().Fill(128);
                core.PushExternalFrame(session, 1, 360, 720, frame);
                InteractionAssert(NativePreviewWindow.TryCreateAndShowForSession(session.RawHandle, 360, 720,
                    "Device preview fixture", 0, 1, () => true, () => 1, _ => { }, () => { },
                    _ => { }, () => { }, out var device), "Cannot create a native device preview.");
                using (device)
                {
                    VerifyClickThrough(main, device!, targetHwnd, targetPath, prePinned: false, output, () =>
                    {
                        frame.AsSpan(0, 360 * 720).Fill(196);
                        core.PushExternalFrame(session, 1, 360, 720, frame);
                    });
                    VerifyClickThroughRecoveryWithoutRenderer(main, device!);
                }
                VerifyClickThroughLifecycle(main);
            }
            finally
            {
                if (!target.HasExited) target.Kill();
                target.WaitForExit();
                File.Delete(targetPath);
                File.Delete(targetPath + ".clicked");
                File.Delete(targetPath + ".scrolled");
            }
        }
        finally { CloseWorkspaceTestWindow(main); app.Shutdown(); }
        Console.WriteLine("PASS opacity threshold, cross-process input, renderer-loss recovery, and multi-window recovery lifetime.");
        return 0;
    }

    private static int RunPreviewClickTarget(string path)
    {
        var app = new Application();
        var window = new Window { WindowStyle = WindowStyle.None, Background = Brushes.MintCream,
            ShowActivated = false, Title = "Click-through input target" };
        window.PreviewMouseDown += (_, _) => File.WriteAllText(path + ".clicked", "clicked");
        window.PreviewMouseWheel += (_, _) => File.WriteAllText(path + ".scrolled", "scrolled");
        window.Loaded += (_, _) =>
        {
            File.WriteAllText(path + ".ready", new WindowInteropHelper(window).Handle.ToString());
            File.Move(path + ".ready", path);
        };
        return app.Run(window);
    }

    private static void VerifyClickThroughRecoveryWithoutRenderer(MainWindow main, NativePreviewWindow preview)
    {
        InteractionAssert(preview.ClickThroughRequested?.Invoke(true) == true,
            "The preview must enter click-through before the renderer is lost.");
        var releaseOpacity = preview.BeginTemporarySetting(new SettingShortcutBinding
        {
            Setting = PreviewShortcutSetting.Opacity,
            Trigger = SettingShortcutTrigger.Toggle,
            Modifiers = (int)KeyboardShortcut.Control,
            VirtualKey = 'O',
            Value = 100,
        });
        InteractionAssert(releaseOpacity is not null, "Could not stage an opacity override.");
        NativeCore.DetachDevicePreview(preview.SessionHandle, preview.Handle);
        KeyboardCall(main, "RestorePreviewInteraction");
        const int clickThroughStyles = 0x08000020;
        InteractionAssert(!preview.IsClickThrough && preview.AllowsControlInput &&
            (PreviewClickGetWindowLong(preview.Handle, -20) & clickThroughStyles) == 0 &&
            KeyboardField(main, "_previewInteractionTrayIcon") is null,
            "A missing video renderer must not prevent Windows input recovery.");
        releaseOpacity!();
    }

    private static void VerifyClickThroughLifecycle(MainWindow main)
    {
        NativePreviewWindow Open()
        {
            InteractionAssert(NativePreviewWindow.TryCreateAndShowForContent(
                new Border { Background = Brushes.SteelBlue }, 360, 720, "Click-through lifetime fixture",
                () => true, _ => { }, () => 1, () => { }, () => { }, out var window),
                "Could not create a preview for the lifetime test.");
            KeyboardCall(main, "ConfigurePreviewClickThrough", window!);
            InteractionAssert(KeyboardCall(window!, "SetWindowOpacity", 0.79) is true &&
                window!.ClickThroughRequested?.Invoke(true) == true, "Could not enable the lifetime test preview.");
            return window!;
        }

        using var first = Open();
        using var second = Open();
        var active = (HashSet<NativePreviewWindow>)KeyboardField(main, "_clickThroughWindows");
        InteractionAssert(active.SetEquals([first, second]), "Each click-through preview must be tracked individually.");
        NativePreviewWindow.SetAllBossKeyHidden(true);
        try
        {
            InteractionAssert(!ScreenTextFeedbackWindow.TryReadPreviewBounds(first.Handle, null, out _) &&
                !ScreenTextFeedbackWindow.TryReadPreviewBounds(second.Handle, null, out _) &&
                active.Count == 2, "The boss key must hide both previews and retain their recovery state.");
        }
        finally { NativePreviewWindow.SetAllBossKeyHidden(false); }
        InteractionAssert(first.IsClickThrough && second.IsClickThrough,
            "Unhiding previews must preserve click-through.");
        first.Dispose();
        InteractionAssert(active.SetEquals([second]) &&
            KeyboardField(main, "_previewInteractionHotkeyRegistered") is true,
            "Closing one preview must leave the other preview recoverable.");
        second.Dispose();
        InteractionAssert(active.Count == 0 && KeyboardField(main, "_previewInteractionTrayIcon") is null &&
            KeyboardField(main, "_previewInteractionHotkeyRegistered") is false,
            "Closing the last preview must release both recovery resources.");

        using var preview = Open();
        var oldId = (int)KeyboardField(main, "_previewInteractionHotkeyId");
        var oldIcon = KeyboardField(main, "_previewInteractionTrayIcon");
        var oldTraySource = (HwndSource)KeyboardField(oldIcon, "_source");
        _ = PreviewStyleSendMessage(oldTraySource.Handle, 0x8000 + 91, 0, 0x0400);
        InteractionAssert(preview.ClickThroughRequested?.Invoke(false) == true &&
            preview.ClickThroughRequested?.Invoke(true) == true, "Could not restart click-through.");
        var mainHandle = new WindowInteropHelper(main).Handle;
        _ = PreviewStyleSendMessage(mainHandle, 0x0312, oldId, 0);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        InteractionAssert(preview.IsClickThrough, "A queued callback from a previous activation restored a fresh preview.");
        var id = (int)KeyboardField(main, "_previewInteractionHotkeyId");
        _ = PreviewStyleSendMessage(mainHandle, 0x0312, id, 0);
        InteractionAssert(!preview.IsClickThrough, "The current hotkey registration failed to restore the preview.");
    }

    private static void VerifyClickThrough(MainWindow main, NativePreviewWindow preview, nint target,
        string targetPath, bool prePinned, string output, Action? advanceVideo = null)
    {
        const int exStyleTopmost = 8;
        const int hideWindow = 0x0080;
        bool IsPinned() => (PreviewClickGetWindowLong(preview.Handle, -20) & exStyleTopmost) != 0;
        KeyboardCall(main, "ConfigurePreviewClickThrough", preview);
        if (prePinned) KeyboardCall(preview, "ToggleTopMost");
        WindowStyleSettingsWindow OpenStyle()
        {
            var menu = (MenuItem)KeyboardField(preview, "_windowMenuItem");
            InteractionAssert(menu.Items.OfType<MenuItem>().All(item =>
                !Equals(item.Header, LocalizationService.Get("IndependentWindowClickThrough"))),
                "Click-through should only be exposed in window style settings.");
            ((MenuItem)KeyboardField(preview, "_styleItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return (WindowStyleSettingsWindow)KeyboardField(preview, "_styleWindow");
        }
        CheckBox ClickThroughToggle(Window window) => (CheckBox)window.FindName("ClickThroughCheckBox");
        var style = OpenStyle();
        var slider = (Slider)style.FindName("OpacitySlider");
        var toggle = ClickThroughToggle(style);
        InteractionAssert(toggle.IsVisible && !toggle.IsEnabled && !preview.SetClickThrough(true),
            "The style panel and preview must reject click-through at 100%.");
        slider.Value = 80;
        InteractionAssert(slider.Value == 80 && !toggle.IsEnabled && !preview.SetClickThrough(true),
            "Click-through must remain unavailable at exactly 80%.");
        if (!prePinned)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            SaveWindowRender(style, Path.Combine(output, "01-click-through-at-80.png"));
        }
        slider.Value = 79;
        InteractionAssert(toggle.IsEnabled, "Click-through must become available at 79%.");
        toggle.IsChecked = true;
        InteractionAssert(preview.IsClickThrough && style.IsVisible, "The style toggle did not enable click-through.");
        var releaseOpacityShortcut = preview.BeginTemporarySetting(new SettingShortcutBinding
        {
            Setting = PreviewShortcutSetting.Opacity,
            Modifiers = (int)KeyboardShortcut.Control,
            VirtualKey = 'O',
            Value = 100,
        });
        InteractionAssert(releaseOpacityShortcut is not null &&
            (double)KeyboardField(preview, "_appliedOpacity") < 0.8,
            "An opacity shortcut must not make a click-through window opaque.");
        releaseOpacityShortcut!();
        slider.Value = 80;
        InteractionAssert(!preview.IsClickThrough && !toggle.IsEnabled && toggle.IsChecked == false &&
            KeyboardField(main, "_previewInteractionTrayIcon") is null,
            "Raising opacity to 80% must turn off click-through and release recovery resources.");
        slider.Value = 79;
        InteractionAssert(toggle.IsEnabled && toggle.IsChecked == false, "Lowering opacity must not opt in again.");
        if (!prePinned)
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
            SaveWindowRender(style, Path.Combine(output, "02-click-through-at-79.png"));
        }
        style.Close();

        InteractionAssert(PreviewStyleGetWindowRect(preview.Handle, out var before), "Cannot read preview bounds.");
        InteractionAssert(PreviewStyleSetWindowPos(target, -1, before.Left, before.Top,
            before.Right - before.Left, before.Bottom - before.Top, 0x0050), "Cannot position input target behind preview.");
        preview.Activate();
        InteractionAssert(PreviewStyleSetWindowPos(preview.Handle, -1, 0, 0, 0, 0, 0x0013),
            "Cannot place preview on top of the input fixture.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        _ = PreviewStyleGetWindowRect(preview.Handle, out before);
        var point = new PreviewClickPoint((before.Left + before.Right) / 2, (before.Top + before.Bottom) / 2);
        var initiallyHit = PreviewClickWindowFromPoint(point);
        uint ScreenPixel()
        {
            var dc = PreviewStyleGetDC(0);
            try { return PreviewStyleGetPixel(dc, point.X, point.Y); }
            finally { _ = PreviewStyleReleaseDC(0, dc); }
        }
        var originalPixel = ScreenPixel();
        InteractionAssert(PreviewClickGetAncestor(initiallyHit, 2) == preview.Handle,
            $"Preview must initially receive input. Point {point}, hit {initiallyHit:x}, preview {preview.Handle:x}, target {target:x}, style {PreviewClickGetWindowLong(preview.Handle, -16):x}, ex-style {PreviewClickGetWindowLong(preview.Handle, -20):x}, target ex-style {PreviewClickGetWindowLong(target, -20):x}.");
        if (!prePinned)
            _ = PreviewStyleSetWindowPos(preview.Handle, -2, 0, 0, 0, 0, 0x0013);
        style = OpenStyle();
        ClickThroughToggle(style).IsChecked = true;
        style.Close();
        InteractionAssert(PreviewStyleSetWindowPos(target, preview.Handle, 0, 0, 0, 0, 0x0013),
            "Cannot place test target directly behind the pinned preview.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
        _ = PreviewStyleGetWindowRect(preview.Handle, out var during);
        InteractionAssert(preview.IsClickThrough && !preview.AllowsControlInput && IsPinned() && before.Equals(during),
            $"Click-through must keep the preview pinned in its original bounds and suspend control. Active {preview.IsClickThrough}, input {preview.AllowsControlInput}, pinned {IsPinned()}, bounds {before.Left},{before.Top},{before.Right},{before.Bottom} to {during.Left},{during.Top},{during.Right},{during.Bottom}, source {KeyboardField(main, "_windowSource") is not null}, tray {KeyboardField(main, "_previewInteractionTrayIcon") is not null}.");
        InteractionAssert(KeyboardField(main, "_previewInteractionTrayIcon") is not null &&
            KeyboardField(main, "_previewInteractionHotkeyRegistered") is true, "Both recovery paths must be available.");
        var clickThroughPixel = ScreenPixel();
        InteractionAssert(Enumerable.Range(0, 3).All(channel =>
                Math.Abs((int)((clickThroughPixel >> (channel * 8)) & 255) -
                    (int)((originalPixel >> (channel * 8)) & 255)) <= 2) && originalPixel != uint.MaxValue,
            $"Click-through must preserve the visible preview. Color was {originalPixel:x} and became {clickThroughPixel:x}.");
        if (advanceVideo is not null)
        {
            advanceVideo();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(160));
            InteractionAssert(ScreenPixel() != clickThroughPixel, "The device preview stopped presenting video while passing clicks through.");
        }
        var duringHit = PreviewClickWindowFromPoint(point);
        InteractionAssert(PreviewClickGetAncestor(duringHit, 2) == target, "The test target must receive input at the preview center.");

        File.Delete(targetPath + ".clicked");
        File.Delete(targetPath + ".scrolled");
        _ = PreviewClickGetCursorPos(out var prior);
        try
        {
            _ = PreviewClickSetCursorPos(point.X, point.Y);
            PreviewClickMouseEvent(0x0002, 0, 0, 0, 0);
            PreviewClickMouseEvent(0x0004, 0, 0, 0, 0);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(250));
            InteractionAssert(File.Exists(targetPath + ".clicked"),
                $"An actual mouse click did not reach the separate process. Hit {duringHit:x}, preview {preview.Handle:x}, target {target:x}, style {PreviewClickGetWindowLong(preview.Handle, -16):x}, ex-style {PreviewClickGetWindowLong(preview.Handle, -20):x}.");
            PreviewClickMouseEvent(0x0800, 0, 0, 120, 0);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(130));
            InteractionAssert(File.Exists(targetPath + ".scrolled"), "The mouse wheel did not reach the separate process.");
            foreach (byte key in new byte[] { 0x11, 0x12, 0x10, 0x50 })
                PreviewClickKeyEvent(key, 0, 0, 0);
            foreach (byte key in new byte[] { 0x50, 0x10, 0x12, 0x11 })
                PreviewClickKeyEvent(key, 0, 2, 0);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
        }
        finally { _ = PreviewClickSetCursorPos(prior.X, prior.Y); }

        InteractionAssert(!preview.IsClickThrough && preview.AllowsControlInput && IsPinned() == prePinned &&
            PreviewStyleGetWindowRect(preview.Handle, out var after) && before.Equals(after),
            "The global shortcut must restore input and the original pin state without moving the window.");
        InteractionAssert(KeyboardField(main, "_previewInteractionTrayIcon") is null &&
            KeyboardField(main, "_previewInteractionHotkeyRegistered") is false, "Recovery resources must be released.");

        style = OpenStyle();
        toggle = ClickThroughToggle(style);
        InteractionAssert(toggle.IsChecked == false, "The reopened panel should reflect hotkey recovery.");
        toggle.IsChecked = true;
        toggle.IsChecked = false;
        InteractionAssert(!preview.IsClickThrough, "Unchecking click-through must restore input.");
        toggle.IsChecked = true;
        style.Close();
        if (!prePinned && KeyboardField(main, "_screenTextFeedback") is Window hint)
            SaveWindowRender(hint, Path.Combine(output, "03-click-through-hint.png"));
        if (prePinned)
        {
            var icon = KeyboardField(main, "_previewInteractionTrayIcon");
            var source = (HwndSource)KeyboardField(icon, "_source");
            _ = PreviewStyleSendMessage(source.Handle, 0x8000 + 91, 0, 0x0400);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            InteractionAssert(!preview.IsClickThrough && IsPinned(), "The recovery tray icon did not restore input.");
            return;
        }

        var model = (MainViewModel)KeyboardField(main, "_viewModel");
        _ = PreviewStyleSetWindowPos(target, 0, 0, 0, 0, 0, hideWindow | 0x0017);
        model.SelectedApplicationDisplayMode = ApplicationDisplayMode.Tray;
        AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
        var panel = KeyboardField(main, "_trayPanel") as TrayPanelWindow ??
            throw new InvalidOperationException("Switching to tray mode did not create the tray panel.");
        KeyboardCall(main, "ShowTrayPanel");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
        var restoreButton = (Button)panel.FindName("RestorePreviewInteractionButton");
        InteractionAssert(restoreButton.IsVisible, "The tray panel must show the recovery action.");
        SaveWindowRender(panel, Path.Combine(output, "04-tray-restore.png"));
        restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        InteractionAssert(!preview.IsClickThrough && !IsPinned() &&
            restoreButton.Visibility == Visibility.Collapsed, "The tray panel did not restore input.");
        model.SelectedApplicationDisplayMode = ApplicationDisplayMode.Complete;
        AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PreviewClickPoint(int X, int Y);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint PreviewClickWindowFromPoint(PreviewClickPoint point);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern nint PreviewClickGetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int PreviewClickGetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool PreviewClickGetCursorPos(out PreviewClickPoint point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
    private static extern bool PreviewClickSetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void PreviewClickMouseEvent(uint flags, uint dx, uint dy, uint data, nuint extraInfo);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void PreviewClickKeyEvent(byte key, byte scan, uint flags, nuint extraInfo);
}
