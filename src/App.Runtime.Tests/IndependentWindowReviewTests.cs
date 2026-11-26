using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PixelRect = System.Drawing.Rectangle;
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
        VerifyLastLayoutMigration(output);
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
            VerifyAutomaticWindowPlacement();
            using var preview = CreateIndependentReviewPreview(usbControl: true);
            windows.Add(phone.Udid, preview);
            try
            {
                KeyboardCall(preview, "ShowWithInitialLayout", MultiDevicePreviewManager.GetStartupLayout(phone.Udid));
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                preview.ToggleFixedWindow();
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
            app.RestoreUpdateSettings(store.Load());
            KeyboardCall(reopened, "ShowWithInitialLayout", MultiDevicePreviewManager.GetStartupLayout(phone.Udid));
            reopened.ToggleFixedWindow();
            VerifyIndependentReviewBounds(reopened);
            using var fixedOnly = CreateIndependentReviewPreview();
            KeyboardCall(fixedOnly, "ShowWithInitialLayout", new IndependentWindowLayout(Fixed: true));
            InteractionAssert(UnfixGetWindowRect(fixedOnly.Handle, out var before), "Automatic layout did not provide window bounds.");
            fixedOnly.SetSourceDimensions(844, 390);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(UnfixGetWindowRect(fixedOnly.Handle, out var after) && after.Equals(before),
                "A locked auto-sized window moved or resized when the source changed.");
            using var automaticControl = CreateIndependentReviewPreview(usbControl: true);
            KeyboardCall(automaticControl, "ShowInitially");
            automaticControl.PrepareForUsbControl();
            var automaticLock = (MenuItem)KeyboardField(automaticControl, "_fixedItem");
            InteractionAssert(!automaticLock.IsChecked, "Automatic reverse control must not report that the window bounds are locked.");
            InteractionAssert(UnfixGetWindowRect(automaticControl.Handle, out var controlBefore),
                "The automatically fixed control window has no bounds.");
            automaticControl.SetSourceDimensions(844, 390);
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(UnfixGetWindowRect(automaticControl.Handle, out var controlAfter) &&
                controlAfter.Right - controlAfter.Left != controlBefore.Right - controlBefore.Left,
                "Automatic reverse control must retain its existing source auto-resize behavior.");
            automaticControl.ToggleFixedWindow();
            InteractionAssert(!automaticLock.IsChecked && !automaticControl.AllowsControlInput,
                "The shortcut must release automatic control even while the bounds are movable.");
            automaticControl.PrepareForUsbControl();
            automaticLock.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            InteractionAssert(automaticLock.IsChecked && automaticControl.AllowsControlInput &&
                automaticControl.GetCurrentLayout()?.Fixed == true,
                "The context menu must lock an automatically controlled window without suspending phone input.");
            automaticControl.ToggleFullScreen();
            InteractionAssert(automaticLock.IsChecked && !automaticLock.IsEnabled,
                "Fullscreen must retain the saved window lock and disable changes to it.");
            automaticControl.ToggleFullScreen();
            InteractionAssert(automaticLock.IsChecked && automaticLock.IsEnabled,
                "Leaving fullscreen must restore the checked, usable window lock.");
            automaticLock.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            InteractionAssert(!automaticLock.IsChecked && !automaticControl.AllowsControlInput &&
                automaticControl.GetCurrentLayout()?.Fixed == false,
                "The context menu must unlock the same bounds and suspend phone input.");
            using var media = CreateIndependentReviewPreview(managed: true);
            KeyboardCall(media, "ShowInitially");
            media.ToggleFixedWindow();
            InteractionAssert(UnfixGetWindowRect(media.Handle, out var mediaBefore), "The media preview has no bounds.");
            _ = MappingSendMessage(media.Handle, 0x0112, (nint)0xF030, 0);
            InteractionAssert(UnfixGetWindowRect(media.Handle, out var mediaAfter) && mediaAfter.Equals(mediaBefore),
                "Maximizing a locked media window changed the bounds.");
            Console.WriteLine("PASS independent-window startup restored after reopen, source rotation and fullscreen roundtrip.");
            VerifyRememberedWindowLayout(main, app, phone.Udid, store, windows);
            return 0;
        }
        finally
        {
            app.IsUiPreviewMode = true;
            CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static void VerifyLastLayoutMigration(string output)
    {
        var path = Path.Combine(Path.GetFullPath(output), "last-layout-migration.json");
        File.WriteAllText(path, """
            {"IndependentWindowStartupMode":0,"IndependentWindowLayoutHistoryByDevice":{
              "phone":{"LastLayout":{"X":100,"Y":120,"Width":420,"Height":620,"Fixed":false},"AutoFix":true},
              "tablet":{"LastLayout":{"X":200,"Y":220,"Width":600,"Height":400,"Fixed":true},"AutoFix":false}
            }}
            """);
        var store = new UpdateSettingsStore(path);
        var loaded = store.Load();
        InteractionAssert(new UpdateSettings().IndependentWindowStartupMode == IndependentWindowLayoutMode.Last &&
            loaded.IndependentWindowStartupMode == IndependentWindowLayoutMode.Last &&
            loaded.IndependentWindowLayoutSchema == 1 &&
            loaded.IndependentWindowLayoutHistoryByDevice["phone"] is { AutoFix: null, LastLayout.Fixed: false } &&
            loaded.IndependentWindowLayoutHistoryByDevice["tablet"] is { AutoFix: null, LastLayout.Fixed: true },
            "Upgrade must default to Last and retain real per-device lock history without old startup overrides.");
        loaded.IndependentWindowStartupMode = IndependentWindowLayoutMode.Automatic;
        store.Save(loaded);
        InteractionAssert(store.Load().IndependentWindowStartupMode == IndependentWindowLayoutMode.Automatic &&
            store.Load().Clone().IndependentWindowLayoutSchema == 1,
            "An explicit automatic choice after upgrade must survive reload and cloning.");
        Console.WriteLine("PASS last-layout migration: new default, obsolete override removal, per-device fixed history and explicit-choice persistence.");
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

        KeyboardCall(main, "RestoreSettingsCategory", "projection");
        KeyboardCall(main, "SetSettingsPanelVisible", true);
        KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
        var layout = Find<StackPanel>("IndependentWindowLayoutEditor");
        var mode = Find<ComboBox>("IndependentLayoutModeSelector");
        var fields = Find<StackPanel>("IndependentLayoutCustomFields");
        var saveCurrent = Find<Button>("IndependentLayoutSaveCurrentButton");
        InteractionAssert(main.FindName("IndependentLayoutFixed") is null, "Startup locking must follow window history without an extra checkbox.");
        var help = Find<TextBlock>("IndependentLayoutModeHelp");
        InteractionAssert(Equals(mode.SelectedValue, IndependentWindowLayoutMode.Last) &&
            fields.Visibility == Visibility.Collapsed && saveCurrent.Visibility == Visibility.Collapsed &&
            help.Text == LocalizationService.Get("IndependentWindowLayoutUseLastHelp"),
            "A device with no saved settings must default to restoring its last layout.");
        // Selecting an item exercises the same Selector path as the dropdown,
        // including the timing of SelectedValue and SelectionChanged.
        mode.SelectedItem = mode.Items.OfType<ComboBoxItem>()
            .Single(item => Equals(item.Tag, IndependentWindowLayoutMode.Custom));
        InteractionAssert(fields.Visibility == Visibility.Visible && saveCurrent.Visibility == Visibility.Collapsed &&
            help.Visibility == Visibility.Collapsed,
            "Custom mode must show only the manual fields and shared checkbox.");
        var customInputs = new[] { "IndependentLayoutX", "IndependentLayoutY", "IndependentLayoutWidth", "IndependentLayoutHeight" }
            .Select(Find<TextBox>).ToArray();
        InteractionAssert(customInputs.All(input => input.IsEnabled && input.Text.Length == 0) &&
            Find<TextBlock>("IndependentLayoutFallbackHint").Visibility == Visibility.Visible,
            "A new device must show blank, editable custom inputs alongside the fallback hint.");
        var projection = Find<ScrollViewer>("ProjectionSettingsScrollViewer");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
        layout.BringIntoView();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        projection.ScrollToVerticalOffset(projection.VerticalOffset +
            ((FrameworkElement)layout.Parent).TranslatePoint(new Point(), projection).Y - 6);
        CapturePanel("92-custom-layout-first-use.png");
        Find<TextBox>("IndependentLayoutX").Text = "100";
        Find<TextBox>("IndependentLayoutY").Text = "180";
        Find<TextBox>("IndependentLayoutWidth").Text = "420";
        Find<TextBox>("IndependentLayoutHeight").Text = "620";
        app.IsUiPreviewMode = false;
        try {
            var field = Find<TextBox>("IndependentLayoutHeight");
            field.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, Environment.TickCount, field, mode)
                { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
        }
        finally { app.IsUiPreviewMode = true; }
        var settings = store.Load();
        var history = settings.IndependentWindowLayoutHistoryByDevice[phone.Udid];
        InteractionAssert(history.CustomLayout == new IndependentWindowLayout(100, 180, 420, 620) &&
            history.AutoFix is null && !settings.IndependentWindowLayouts.ContainsKey(phone.Udid),
            "Saving custom mode did not persist manual bounds and auto-lock separately from the captured preset.");
        AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
        layout.BringIntoView();
        AdvanceDispatcher(TimeSpan.FromMilliseconds(200));
        projection.ScrollToVerticalOffset(projection.VerticalOffset +
            ((FrameworkElement)layout.Parent).TranslatePoint(new Point(), projection).Y - 6);
        CapturePanel("92-custom-layout.png");
        mode.SelectedValue = IndependentWindowLayoutMode.Last;
        InteractionAssert(fields.Visibility == Visibility.Collapsed && saveCurrent.Visibility == Visibility.Collapsed &&
            help.Text == LocalizationService.Get("IndependentWindowLayoutUseLastHelp"),
            "Last layout mode must show its own explanation and preserve the shared checkbox.");
        CapturePanel("92-restore-last-layout.png");
        mode.SelectedValue = IndependentWindowLayoutMode.Preset;
        InteractionAssert(fields.Visibility == Visibility.Collapsed && saveCurrent.Visibility == Visibility.Visible &&
            help.Visibility == Visibility.Collapsed,
            "Preset mode must show its capture button and hide manual fields.");
        CapturePanel("92-preset-layout.png");
        mode.SelectedValue = IndependentWindowLayoutMode.Automatic;
        CapturePanel("92-automatic-layout.png");
        mode.SelectedValue = IndependentWindowLayoutMode.Custom;

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
            allow.Text = "Win\nAlt+Tab\nCtrl+Shift+F8\nAlt+F4";
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
            InteractionAssert(!editor.IsVisible && saved.PreviewWindowsHotkeyAllowList.Split('\n').Length == 4 &&
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
        {
            arguments[Array.FindIndex(ctor.GetParameters(), parameter => parameter.Name == "isUsbControlEnabled")] =
                (Func<bool>)(() => true);
            arguments[Array.FindIndex(ctor.GetParameters(), parameter => parameter.Name == "pointerInput")] =
                (Action<Controls.PreviewPointerEventArgs>)(_ => { });
        }
        if (managed)
            arguments[Array.FindIndex(ctor.GetParameters(), parameter => parameter.Name == "managedContent")] =
                new Border { Background = Brushes.Black };
        return (NativePreviewWindow)ctor.Invoke(arguments);
    }

    private static void VerifyAutomaticWindowPlacement()
    {
        var work = new PixelRect(-1920, 40, 1920, 1040);
        var preferred = new PixelRect(-1120, 185, 340, 750);
        var second = IndependentWindowPlacement.FindRoom(work, preferred, [preferred], 16);
        var third = IndependentWindowPlacement.FindRoom(work, preferred, [preferred, second], 16);
        InteractionAssert(work.Contains(second) && work.Contains(third) &&
            IndependentWindowPlacement.Overlap(third, [preferred, second], 16) == 0,
            "Automatic placement did not find space on a display with negative coordinates.");
        var portraitDisplay = new PixelRect(-914, 0, 914, 1415);
        var largePortrait = new PixelRect(-711, 156, 508, 1100);
        var firstPortrait = IndependentWindowPlacement.Place(portraitDisplay, largePortrait, [],
            24, 144, allowCompact: true);
        var compactPortrait = IndependentWindowPlacement.Place(portraitDisplay, largePortrait,
            [firstPortrait], 24, 144, allowCompact: true);
        InteractionAssert(portraitDisplay.Contains(compactPortrait) && compactPortrait.Width < largePortrait.Width &&
            compactPortrait.Left >= firstPortrait.Right + 24,
            "The second phone did not fit alongside the first on a narrow monitor.");
        var smallWork = new PixelRect(0, 40, 700, 420);
        var oversized = new PixelRect(-1050, -800, 1200, 600);
        var recovered = IndependentWindowPlacement.FitOnResizedDisplay(smallWork, oversized);
        InteractionAssert(smallWork.Contains(recovered) && recovered.Size == new System.Drawing.Size(700, 350),
            "Recovery on a smaller display did not fit the window or preserve its ratio.");
        var crowded = IndependentWindowPlacement.FindRoom(smallWork,
            new PixelRect(170, 85, 320, 350), [smallWork], 16);
        InteractionAssert(smallWork.Contains(crowded),
            "Automatic placement left a crowded display instead of placing a reachable window.");
        var exactSlot = new PixelRect(0, 0, 1000, 420);
        var flush = IndependentWindowPlacement.Place(exactSlot, new PixelRect(100, 0, 300, 420),
            [new(0, 0, 350, 420), new(650, 0, 350, 420)], 16, 96, allowCompact: true);
        InteractionAssert(flush == new PixelRect(350, 0, 300, 420),
            "A preferred gap caused shrinking or overlap even though an exact-sized slot was available.");

        using var first = CreateIndependentReviewPreview();
        KeyboardCall(first, "ShowInitially");
        var firstBounds = ReadBounds(first);
        using var next = CreateIndependentReviewPreview();
        KeyboardCall(next, "ShowInitially");
        var nextBounds = ReadBounds(next);
        var firstAfter = ReadBounds(first);
        InteractionAssert(firstBounds == firstAfter && !firstBounds.IntersectsWith(nextBounds),
            $"Opening a second native preview moved or overlapped the first preview: {firstBounds}, {firstAfter}, {nextBounds}.");

        _ = MappingSendMessage(next.Handle, 0x0231, 0, 0);
        next.SetSourceDimensions(844, 390);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        InteractionAssert(ReadBounds(next) == nextBounds, "A source rotation resized the window while the user was dragging.");
        var propose = Marshal.AllocHGlobal(Marshal.SizeOf<UnfixRect>());
        try
        {
            Marshal.StructureToPtr(new UnfixRect
            {
                Left = nextBounds.Left, Top = nextBounds.Top, Right = nextBounds.Right + 10, Bottom = nextBounds.Bottom,
            }, propose, false);
            InteractionAssert(MappingSendMessage(next.Handle, 0x0214, 2, propose) == 1,
                "The native edge did not constrain the interactive resize.");
            var sizing = Marshal.PtrToStructure<UnfixRect>(propose);
            InteractionAssert(sizing.Bottom - sizing.Top > sizing.Right - sizing.Left,
                "The live sizing cursor changed aspect before the interaction finished.");
        }
        finally { Marshal.FreeHGlobal(propose); }
        _ = MappingSendMessage(next.Handle, 0x0232, 0, 0);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        var landscape = ReadBounds(next);
        InteractionAssert(landscape.Width > landscape.Height, "A rotation during drag was not applied on release.");

        next.SetSourceDimensions(18000, 1000);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        next.SetSourceDimensions(390, 844);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        var rotatedBack = ReadBounds(next);
        InteractionAssert(Math.Abs(rotatedBack.Width - nextBounds.Width) <= 2 &&
            Math.Abs(rotatedBack.Height - nextBounds.Height) <= 2,
            "Constraining a wide orientation permanently shrank the preferred portrait size.");

        _ = MappingSendMessage(next.Handle, 0x0231, 0, 0);
        InteractionAssert(InteractionSetWindowPos(next.Handle, 0, -20000, -20000,
            rotatedBack.Width, rotatedBack.Height, 0x14), "Could not simulate a display removed mid-drag.");
        _ = MappingSendMessage(next.Handle, 0x007E, 0, 0);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        InteractionAssert((bool)KeyboardField(next, "_displayRecoveryPending") && ReadBounds(next).Left == -20000,
            "Display change interrupted the pointer interaction or lost its pending recovery.");
        _ = MappingSendMessage(next.Handle, 0x0232, 0, 0);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        InteractionAssert(ReadBounds(next).Left > -20000 && !(bool)KeyboardField(next, "_displayRecoveryPending"),
            "Releasing the drag did not apply pending display recovery.");

        next.ToggleFixedWindow();
        InteractionAssert(InteractionSetWindowPos(next.Handle, 0, -20000, -20000,
            landscape.Width, landscape.Height, 0x14), "Could not simulate a removed monitor.");
        _ = MappingSendMessage(next.Handle, 0x007E, 0, 0);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        var reachable = ReadBounds(next);
        InteractionAssert(reachable.Left > -20000 && reachable.Top > -20000 &&
            (bool)KeyboardField(next, "_isFixed"), "The display-change message did not rescue a locked preview.");
        _ = MappingSendMessage(next.Handle, 0x0112, (nint)0xF020, 0); // SC_MINIMIZE
        _ = MappingSendMessage(next.Handle, 0x007E, 0, 0);
        AdvanceDispatcher(TimeSpan.FromMilliseconds(50));
        InteractionAssert((bool)KeyboardField(next, "_displayRecoveryPending"),
            "A minimized window discarded the display change.");
        _ = MappingSendMessage(next.Handle, 0x0112, (nint)0xF120, 0); // SC_RESTORE
        AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
        InteractionAssert(!(bool)KeyboardField(next, "_displayRecoveryPending"),
            "Taskbar restore did not apply pending display recovery.");
        Console.WriteLine("PASS automatic placement, exact-fit gaps, drag-safe sizing, stable rotation size, and display recovery after drag and minimize.");

        static PixelRect ReadBounds(NativePreviewWindow window)
        {
            InteractionAssert(UnfixGetWindowRect(window.Handle, out var rect), "Could not read preview bounds.");
            return PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
    }

    private static void VerifyRememberedWindowLayout(MainWindow main, App app, string udid,
        UpdateSettingsStore store, IDictionary windows)
    {
        var mode = (ComboBox)main.FindName("IndependentLayoutModeSelector");
        var custom = (StackPanel)main.FindName("IndependentLayoutCustomFields");
        var customX = (TextBox)main.FindName("IndependentLayoutX");
        var saveCurrent = (Button)main.FindName("IndependentLayoutSaveCurrentButton");
        var status = (TextBlock)main.FindName("IndependentLayoutStatus");

        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var phone = vm.SelectedDevice!;
        var otherPhone = DeviceViewModel.FromNative(new NativeDeviceInfo
        {
            Udid = udid + "-other", Name = "iPad · 测试设备", ProductType = "iPad13,4",
            State = ConnectionState.Ready,
        }, DeviceAppearance.Unknown);
        var savedCustom = MultiDevicePreviewManager.GetLayoutHistory(udid)!.CustomLayout;
        var firstBounds = new IndependentWindowLayout(145, 125, 450, 575);
        var fullscreenBounds = new IndependentWindowLayout(160, 130, 470, 600, Fixed: true);
        var minimizedBounds = new IndependentWindowLayout(175, 145, 480, 610);
        var capturedBounds = new IndependentWindowLayout(190, 160, 490, 615, Fixed: true);

        app.IsUiPreviewMode = false;
        try
        {
            Select(IndependentWindowLayoutMode.Automatic);
            Save();
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(udid) is null,
                "Automatic layout without window history must not introduce a fixed state.");
            var settings = app.UpdateSettings;
            var autoHistory = settings.IndependentWindowLayoutHistoryByDevice[udid];
            settings.IndependentWindowLayoutHistoryByDevice[udid] = autoHistory with { AutoFix = null };
            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            Select(IndependentWindowLayoutMode.Last);
            customX.Text = "invalid";
            Save();
            InteractionAssert(!custom.IsEnabled && MultiDevicePreviewManager.GetLayoutHistory(udid)?.CustomLayout == savedCustom &&
                MultiDevicePreviewManager.GetStartupLayout(udid) is null,
                "Restore mode did not hide custom inputs, retain manual settings, or fall back to automatic placement.");

            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            Select(IndependentWindowLayoutMode.Custom);
            Save();
            InteractionAssert(custom.IsEnabled && MultiDevicePreviewManager.GetStartupLayout(udid) == savedCustom,
                "Switching back did not restore the saved custom layout.");
            using (var first = Open())
            {
                Move(first, firstBounds);
            }
            InteractionAssert(store.Load().IndependentWindowLayoutHistoryByDevice[udid] ==
                new IndependentWindowLayoutHistory(LastLayout: firstBounds, CustomLayout: savedCustom),
                "Closing in custom mode did not save the actual bounds separately.");

            Select(IndependentWindowLayoutMode.Last);
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
                var lockItem = (MenuItem)KeyboardField(fromFullscreen, "_fixedItem");
                InteractionAssert(lockItem.IsCheckable && lockItem.IsChecked &&
                    (string)lockItem.Header == LocalizationService.Get("IndependentWindowFix"),
                    "The context menu did not show the restored lock state.");
                lockItem.ApplyTemplate();
                var check = (FrameworkElement)lockItem.Template.FindName("CheckIndicator", lockItem);
                InteractionAssert(check.Visibility == Visibility.Visible, "The restored lock has no visible check mark.");
                lockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                InteractionAssert(!lockItem.IsChecked && !(bool)KeyboardField(fromFullscreen, "_isFixed") &&
                    check.Visibility == Visibility.Collapsed, "Unlocking from the context menu did not update its check mark.");
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
            {
                AssertBounds(restored, minimizedBounds);
                restored.PrepareForUsbControl();
            }
            InteractionAssert(store.Load().IndependentWindowLayoutHistoryByDevice[udid].LastLayout == minimizedBounds,
                "Temporary reverse-control locking overwrote the remembered fixed-window preference.");

            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            InteractionAssert(Equals(mode.SelectedValue, IndependentWindowLayoutMode.Last) && !custom.IsEnabled,
                "Selection did not restore the remembered mode.");
            Select(IndependentWindowLayoutMode.Automatic);
            customX.Text = "invalid";
            Save();
            app.RestoreUpdateSettings(store.Load());
            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            InteractionAssert(Equals(mode.SelectedValue, IndependentWindowLayoutMode.Automatic) && custom.Visibility == Visibility.Collapsed &&
                MultiDevicePreviewManager.GetStartupLayout(udid) is null &&
                MultiDevicePreviewManager.GetLayoutHistory(udid)?.CustomLayout == savedCustom &&
                store.Load().IndependentWindowLayoutHistoryByDevice[udid].LastLayout == minimizedBounds,
                "Automatic mode did not survive reload or preserved settings were lost.");

            Select(IndependentWindowLayoutMode.Preset);
            CaptureCurrent();
            InteractionAssert(status.Text == LocalizationService.Get("IndependentWindowLayoutCurrentUnavailable") &&
                store.Load().IndependentWindowStartupMode == IndependentWindowLayoutMode.Preset,
                "Capturing without a window did not explain why or disturbed the globally saved mode.");
            using (var current = Open())
            {
                windows.Add(udid, current);
                try
                {
                    Move(current, capturedBounds);
                    current.ToggleFixedWindow();
                    current.ToggleFullScreen();
                    CaptureCurrent();
                    InteractionAssert(status.Text == LocalizationService.Get("IndependentWindowLayoutCurrentUnavailable") &&
                        !store.Load().IndependentWindowLayouts.ContainsKey(udid),
                        "A fullscreen capture overwrote the preset.");
                    current.ToggleFullScreen();
                    CaptureCurrent();
                    InteractionAssert(store.Load().IndependentWindowLayouts[udid] == capturedBounds &&
                        MultiDevicePreviewManager.GetLayoutMode() == IndependentWindowLayoutMode.Preset &&
                        MultiDevicePreviewManager.GetLayoutHistory(udid)?.CustomLayout == savedCustom,
                        "The capture button did not save this device’s current normal bounds and fixed state.");
                    current.ToggleFixedWindow();
                    Move(current, firstBounds);
                }
                finally { windows.Remove(udid); }
            }
            app.RestoreUpdateSettings(store.Load());
            using (var fromPreset = Open())
                AssertBounds(fromPreset, capturedBounds with { Fixed = false });

            using (var locked = Open())
            {
                AssertBounds(locked, capturedBounds with { Fixed = false });
                locked.ToggleFixedWindow();
            }
            app.RestoreUpdateSettings(store.Load());
            using (var unlocked = Open())
            {
                AssertBounds(unlocked, capturedBounds);
                unlocked.ToggleFixedWindow();
            }
            Save();
            app.RestoreUpdateSettings(store.Load());
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(udid) == capturedBounds with { Fixed = false },
                "Unlocking the actual window must determine the next startup fixed state.");
            using (var preview = Open())
                Move(preview, firstBounds);

            SelectDevice(otherPhone);
            InteractionAssert(Equals(mode.SelectedValue, IndependentWindowLayoutMode.Preset) &&
                MultiDevicePreviewManager.GetStartupLayout(otherPhone.Udid) is null &&
                ((TextBlock)main.FindName("IndependentLayoutFallbackHint")).Visibility == Visibility.Visible,
                "A second device borrowed the first device’s preset or lost the global selection.");
            var otherPreset = new IndependentWindowLayout(240, 170, 500, 630);
            using (var other = Open(otherPhone.Udid))
            {
                windows.Add(otherPhone.Udid, other);
                try
                {
                    Move(other, otherPreset);
                    CaptureCurrent();
                }
                finally { windows.Remove(otherPhone.Udid); }
            }
            InteractionAssert(store.Load().IndependentWindowLayouts[otherPhone.Udid] == otherPreset &&
                store.Load().IndependentWindowLayouts[udid] == capturedBounds,
                "Saving the second device’s preset changed the first device’s preset.");
            SelectDevice(null);
            InteractionAssert(mode.IsEnabled && !saveCurrent.IsEnabled,
                "The global dropdown must be available without a device and device controls must be disabled.");
            Select(IndependentWindowLayoutMode.Custom);
            InteractionAssert(store.Load().IndependentWindowStartupMode == IndependentWindowLayoutMode.Custom &&
                !custom.IsEnabled && MultiDevicePreviewManager.GetStartupLayout(udid) == savedCustom,
                "Custom mode did not save globally without a connected device.");
            SelectDevice(otherPhone);
            InteractionAssert(custom.IsEnabled && customX.Text.Length == 0 &&
                MultiDevicePreviewManager.GetStartupLayout(otherPhone.Udid) is null,
                "An unconfigured device borrowed another device’s custom coordinates.");
            customX.Text = "260";
            ((TextBox)main.FindName("IndependentLayoutY")).Text = "210";
            customX.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, Environment.TickCount, customX, mode)
                { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
            app.RestoreUpdateSettings(store.Load());
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(otherPhone.Udid) == new IndependentWindowLayout(260, 210) &&
                MultiDevicePreviewManager.GetStartupLayout(udid) == savedCustom,
                "The second device’s custom bounds or the global policy did not survive reload.");
            SelectDevice(phone);
            InteractionAssert(Equals(mode.SelectedValue, IndependentWindowLayoutMode.Custom) &&
                customX.Text == savedCustom?.X.ToString(), "Switching devices did not load its own custom fields.");
            var invalidStore = new UpdateSettingsStore(Path.Combine(Path.GetDirectoryName(
                Path.GetFullPath((string)KeyboardField(store, "_path")))!, "invalid-settings-target"));
            var invalidPath = (string)KeyboardField(invalidStore, "_path");
            Directory.CreateDirectory(invalidPath);
            SetKeyboardField(app, "_settingsStore", invalidStore);
            try
            {
                Select(IndependentWindowLayoutMode.Last);
                InteractionAssert(Equals(mode.SelectedValue, IndependentWindowLayoutMode.Custom) &&
                    MultiDevicePreviewManager.GetLayoutMode() == IndependentWindowLayoutMode.Custom &&
                    status.Text == LocalizationService.Get("SetupSaveFailed"),
                    "A failed write did not restore the global policy and dropdown selection.");
            }
            finally { SetKeyboardField(app, "_settingsStore", store); }
            Select(IndependentWindowLayoutMode.Last);
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(otherPhone.Udid) == otherPreset &&
                MultiDevicePreviewManager.GetStartupLayout(udid) == firstBounds,
                "Global restore-last did not resolve each device’s own normal window bounds.");

            // Upgrade both configurations that agree and configurations whose saved policies conflict.
            var legacy = store.Load();
            legacy.IndependentWindowStartupMode = null;
            legacy.IndependentWindowLayoutHistoryByDevice[udid] = new(false, minimizedBounds);
            store.Save(legacy);
            app.RestoreUpdateSettings(store.Load());
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(udid) == capturedBounds with { Fixed = false },
                "An existing preset was lost when loading settings without a mode.");
            legacy.IndependentWindowLayoutHistoryByDevice[udid] = new(true, minimizedBounds);
            store.Save(legacy);
            app.RestoreUpdateSettings(store.Load());
            InteractionAssert(MultiDevicePreviewManager.GetLayoutMode() == IndependentWindowLayoutMode.Last &&
                MultiDevicePreviewManager.GetStartupLayout(udid) == minimizedBounds &&
                MultiDevicePreviewManager.GetLayoutHistory(udid)?.LastLayout == minimizedBounds &&
                MultiDevicePreviewManager.GetSavedLayout(otherPhone.Udid) == otherPreset,
                "Upgrading mixed policies should keep device data and restore the last layout.");

            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
            Select(IndependentWindowLayoutMode.Preset);
            Save();
            InteractionAssert(MultiDevicePreviewManager.GetStartupLayout(udid) == capturedBounds with { Fixed = false } &&
                store.Load().IndependentWindowLayoutHistoryByDevice[udid].LastLayout == minimizedBounds,
                "Preset mode did not take precedence over the stored last bounds.");
            Console.WriteLine("PASS global startup mode, two independent device layouts, no-device settings, legacy migration and disk reload.");
        }
        finally { app.IsUiPreviewMode = true; }

        void Save() => KeyboardCall(main, "OnSaveIndependentWindowLayout", main, new RoutedEventArgs());
        void Select(IndependentWindowLayoutMode selected) => mode.SelectedValue = selected;
        void CaptureCurrent() => saveCurrent.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        void SelectDevice(DeviceViewModel? device)
        {
            SetKeyboardField(vm, "_selectedDevice", device);
            KeyboardCall(main, "RefreshIndependentWindowLayoutEditor");
        }

        NativePreviewWindow Open(string? deviceId = null)
        {
            deviceId ??= udid;
            var window = CreateIndependentReviewPreview();
            KeyboardCall(window, "ShowWithInitialLayout", MultiDevicePreviewManager.GetStartupLayout(deviceId));
            MultiDevicePreviewManager.TrackLayoutHistory(deviceId, window);
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
            preview.WindowsHotkeysFor = main.PreviewWindowsHotkeysFor;
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
            VerifyClickThroughRecoveryWithDirectKeyboard(main, preview, packets);
            VerifyShellHotkeyForwarding(main);
            SendReviewKeys([(0x75, true), (0x75, false)]);
            InteractionAssert(!(bool)KeyboardField(preview, "_isFixed"), "Native F6 did not unlock the independent window.");
            SendReviewKeys([(0x75, true), (0x75, false)]);
            InteractionAssert((bool)KeyboardField(preview, "_isFixed"), "Native F6 did not lock the independent window.");
            ActivateMappingTestWindow(main);
            SendChord(blocked);
            InteractionAssert(blockedCount == 2, "The blocked chord remained blocked after leaving the independent window.");
            preview.Activate();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(MappingGetForeground() == preview.Handle, "Unable to reactivate the test preview for Alt+F4.");
            InteractionAssert((bool)KeyboardField(preview, "IsPointerInputActive"),
                "The independent preview was not intercepting window keyboard messages for reverse control.");
            SendReviewKeys([(0xA4, true), (0x73, true), (0x73, false), (0xA4, false)]);
            InteractionAssert(preview.Handle == 0, "The native preview swallowed an allowed Alt+F4.");
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

    private static void VerifyClickThroughRecoveryWithDirectKeyboard(MainWindow main, NativePreviewWindow interactive, MemoryStream packets)
    {
        using var watching = CreateIndependentReviewPreview(managed: true);
        KeyboardCall(watching, "ShowInitially");
        KeyboardCall(main, "ConfigurePreviewClickThrough", watching);
        InteractionAssert(KeyboardCall(watching, "SetWindowOpacity", 0.60) is true &&
            watching.ClickThroughRequested?.Invoke(true) == true, "Could not enable the watching window's click-through.");
        var policy = KeyboardField(main, "_previewWindowsHotkeys");
        try
        {
            SetKeyboardField(main, "_previewWindowsHotkeys",
                PreviewWindowsHotkeyPolicy.Parse(null, "Ctrl+Alt+Shift+P", out _));
            interactive.Activate();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
            InteractionAssert(MappingGetForeground() == interactive.Handle && watching.IsClickThrough,
                "The interactive window must have keyboard focus while the other window stays click-through.");
            packets.SetLength(0);
            SendReviewKeys([(0xA2, true), (0xA4, true), (0xA0, true), (0x50, true),
                (0x50, false), (0xA0, false), (0xA4, false), (0xA2, false)]);
            WaitReviewTask(Task.WhenAll((HashSet<Task>)KeyboardField(main, "_keyboardSends")));
            var usages = MappingFrames(packets).Where(frame => frame.TryGetProperty("usages", out _))
                .Select(frame => frame.GetProperty("usages").ToString()).ToArray();
            InteractionAssert(!watching.IsClickThrough && usages.All(keys => keys == "[]"),
                $"Recovery chord failed under direct keyboard control. Click-through active={watching.IsClickThrough}, HID={string.Join(" | ", usages)}.");
        }
        finally { SetKeyboardField(main, "_previewWindowsHotkeys", policy); }
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
