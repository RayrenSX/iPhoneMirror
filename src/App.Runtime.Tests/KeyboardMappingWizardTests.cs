using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;
using IPhoneMirror.App.Windows.MappingWizard;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunMappingPickingTests(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        try
        {
            TestMappingVisualCoordinates();
            TestMappingOverlayInteraction(app, output);
            TestMappingSurfaceLifecycle(app);
            return 0;
        }
        finally { app.Shutdown(); }
    }
    private static int RunMappingWizardTests(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, IsUiPreviewMode = true };
        app.InitializeComponent();
        try
        {
            TestMappingWizardState();
            TestMappingWizardLifecycle(app, output);
            TestWizardEditorInputGate(app);
            TestWizardExternalPickTransaction(app);
            TestWizardOwnerClose(app);
            TestWizardMainWindowClose(app);
            TestWizardInvalidPositionFeedback(app, output);
            TestMappingWindows(app, output);
            TestMappingPickPreservesDuration();
            TestMappingWizardLayouts(app, output);
            Console.WriteLine("PASS mapping wizard state, navigation, draft preservation, errors, capture/pointer/timer cleanup, preview recovery, themes/languages/layout scales.");
            return 0;
        }
        finally { app.Shutdown(); }
    }
    private static void WizardNext(KeyboardMappingEditorWindow editor) =>
        ((Button)editor.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void WizardBack(KeyboardMappingEditorWindow editor) =>
        ((Button)editor.FindName("PreviousButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void WizardTo(KeyboardMappingEditorWindow editor, MappingWizardStep step)
    {
        for (var i = 0; editor.Wizard.Current != step && i < 5; i++) WizardNext(editor);
        MappingAssert(editor.Wizard.Current == step, $"Wizard could not reach {step}: {editor.Wizard.Current}, {editor.Wizard.ErrorFor(editor.Wizard.Current)}");
    }
    private static void DiscardWizard(KeyboardMappingEditorWindow editor)
    { if (!editor.IsEditorClosed) KeyboardCall(editor, "OnDiscardClick", editor, new RoutedEventArgs()); }
    private static void TestMappingWizardState()
    {
        var state = new KeyboardMappingWizardState(null, [], _ => null);
        MappingAssert(!state.IsDirty && !state.CanNext && !state.MoveNext(), "Empty key advanced or new draft is dirty.");
        state.SetKey(MappingTestKey);
        MappingAssert(state.MoveNext() && !state.CanNext, "Missing action was accepted.");
        foreach (var action in Enum.GetValues<MappedTouchAction>().Where(a => a <= MappedTouchAction.HoldUntilRelease).ToArray())
        {
            state = new(null, [], _ => null);
            state.SetKey(MappingTestKey); state.MoveNext(); state.SelectAction(action);
            var needsParameters = action is MappedTouchAction.LongPress or MappedTouchAction.DoubleTap || state.IsSwipe;
            MappingAssert(state.Steps.Count == (needsParameters ? 5 : 4), $"Wrong dynamic steps for {action}.");
            state.MoveNext();
            if (needsParameters) state.MoveNext();
            MappingAssert(state.Current == MappingWizardStep.Position && !state.CanNext &&
                !state.Visit(MappingWizardStep.Confirmation), "Unpicked target bypassed validation.");
            state.SetPosition(MappingEntry(action) with { X = .4, Y = .6, EndX = .6, EndY = .4, DeviceCoordinates = true });
            state.MovePrevious(); state.MoveNext();
            MappingAssert(state.HasPosition && state.Draft.X == .4 && state.Draft.Key == MappingTestKey, "Back lost draft data.");
            state.MoveNext();
            MappingAssert(state.Current == MappingWizardStep.Confirmation && state.BuildEntry()?.Action == action, "Action could not save.");
            MappingAssert(state.Visit(MappingWizardStep.Key), "Completed key could not be revisited.");
        }
        var original = MappingEntry(MappedTouchAction.LongPress) with { Enabled = false, DurationMs = 600 };
        state = new(original, [original], _ => null);
        MappingAssert(!state.IsDirty && state.CanVisit(MappingWizardStep.Position), "Edit did not preload completed steps.");
        state.Duration = "NaN";
        MappingAssert(state.BuildEntry() is null && !state.CanVisit(MappingWizardStep.Position), "Invalid duration bypassed progress.");
        state.Duration = "1200";
        state.SetPosition(original with { X = .2, Y = .3 });
        MappingAssert(state.BuildEntry() is { DurationMs: 1200, Enabled: false, X: .2 }, "Pick lost duration or enable state.");
        state.SelectAction(MappedTouchAction.SwipeUp);
        MappingAssert(!state.HasPosition, "Changing gesture reused incompatible geometry.");
        var duplicate = MappingEntry();
        state = new(null, [duplicate], _ => null); state.SetKey(duplicate.Key!);
        MappingAssert(!state.CanNext, "Duplicate was not blocked at key step.");
        state.AcceptReplacement(); MappingAssert(state.CanNext && state.ReplacementId == duplicate.Id, "Explicit replacement not accepted.");
        state.SetKey(new(0x57, 0x11, false));
        MappingAssert(state.ReplacementId is null, "Replacement consent survived a key change.");
        state = new(null, [], _ => "MappingShortcutConflict"); state.SetKey(MappingTestKey);
        MappingAssert(!state.CanNext, "Shortcut conflict advanced.");
        Console.WriteLine("PASS all nine actions, conditional parameters, edit identity/enabled state, dirty tracking, back/indicator gating, duplicate and shortcut conflicts.");
    }
    private static MappingPreviewSurface WizardSurface() => new(1, 1, "wizard-test-device", 1, 300, 600, 0,
        BluetoothMouseDirection.Up, BluetoothMouseDirection.Up, false, false);
    private static void TestMappingWizardLifecycle(App app, string output)
    {
        MappingPreviewSurface? surface = WizardSurface();
        var ended = 0; var cancelled = 0;
        Action<MappedKey>? captured = null;
        Action<KeyboardMappingEntry?>? finishPick = null;
        var saveFails = true; var saves = 0;
        var editor = new KeyboardMappingEditorWindow(null, [], _ => null,
            callback => { captured = callback; return null; }, () => ended++,
            (_, _) => { saves++; return saveFails ? "Test save error" : null; },
            (_, complete) => { finishPick = complete; return null; }, () => cancelled++, () => surface);
        app.MainWindow = editor; editor.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
        try
        {
            MappingAssert(editor.Wizard.Capturing && captured is not null, "Capture did not start automatically.");
            captured!(MappingTestKey); WizardNext(editor);
            captured!(new(0x57, 0x11, false));
            MappingAssert(editor.Wizard.Draft.Key == MappingTestKey && ended > 0, "Late capture mutated next step or capture did not end.");
            ((ListBox)editor.FindName("ActionBox")).SelectedValue = MappedTouchAction.LongPress;
            WizardNext(editor);
            ((TextBox)editor.FindName("DurationBox")).Text = "wrong";
            MappingAssert(!((Button)editor.FindName("SaveButton")).IsEnabled, "Invalid parameter enabled next.");
            ((TextBox)editor.FindName("DurationBox")).Text = "1200";
            editor.HandleNavigationKey(Key.Enter, ModifierKeys.None); editor.UpdateLayout();
            MappingAssert(editor.Wizard.Current == MappingWizardStep.Position && !editor.Wizard.CanNext, "Unpicked position advanced.");
            ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MappingAssert(editor.State == MappingEditorState.PickingPosition && finishPick is not null, "Original preview pick did not begin.");
            finishPick!(editor.Wizard.Draft with { X = .3, Y = .4, DeviceCoordinates = true });
            MappingAssert(editor.Wizard.HasPosition && editor.Wizard.Draft.X == .3, "Original preview result was lost.");
            editor.HandleNavigationKey(Key.Left, ModifierKeys.Alt);
            MappingAssert(editor.Wizard.Current == MappingWizardStep.Parameters && editor.Wizard.Duration == "1200", "Alt+Left lost duration.");
            editor.HandleNavigationKey(Key.Right, ModifierKeys.Alt);
            MappingAssert(editor.Wizard.Current == MappingWizardStep.Position && editor.Wizard.HasPosition, "Alt+Right lost point.");
            surface = null; KeyboardCall(editor, "OnPreviewStatusTick", editor, EventArgs.Empty);
            MappingAssert(!((Button)editor.FindName("SaveButton")).IsEnabled, "Disconnect enabled next.");
            surface = WizardSurface() with { Session = 2 }; KeyboardCall(editor, "OnPreviewStatusTick", editor, EventArgs.Empty);
            MappingAssert(editor.Wizard.HasPosition && ((Button)editor.FindName("SaveButton")).IsEnabled, "Reconnect failed to restore draft.");
            WizardNext(editor);
            finishPick!(editor.Wizard.Draft with { X = .9 });
            MappingAssert(editor.Wizard.Draft.X == .3, "Consumed pick callback mutated another step.");
            WizardNext(editor);
            MappingAssert(saves == 1 && !editor.IsEditorClosed && editor.Wizard.Duration == "1200", "Save error closed editor or lost draft.");
            editor.HandleNavigationKey(Key.Escape, ModifierKeys.None);
            MappingAssert(((Border)editor.FindName("DiscardPanel")).Visibility == Visibility.Visible, "Esc skipped dirty confirmation.");
            KeyboardCall(editor, "OnContinueEditingClick", editor, new RoutedEventArgs());
            saveFails = false; WizardNext(editor);
            MappingAssert(((Border)editor.FindName("SuccessPanel")).Visibility == Visibility.Visible && !editor.IsEditorClosed, "Success did not remain visible.");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(60));
            SaveWindowRender(editor, Path.Combine(output, "wizard-saved.png"));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(800));
            MappingAssert(editor.IsEditorClosed && !((DispatcherTimer)KeyboardField(editor, "_statusTimer")).IsEnabled && cancelled > 0,
                "Completed editor retained a timer or failed to cancel picking.");
            captured!(new(0x57, 0x11, false));
            MappingAssert(editor.Wizard.Draft.Key == MappingTestKey, "Closed editor accepted capture callback.");
        }
        finally { DiscardWizard(editor); }
        var clean = new KeyboardMappingEditorWindow(MappingEntry(), [], _ => null, _ => null, () => { }, (_, _) => null);
        clean.Show(); clean.Close(); MappingAssert(clean.IsEditorClosed, "Unchanged edit prompted on close.");
        var dirty = new KeyboardMappingEditorWindow(null, [], _ => null, _ => null, () => { }, (_, _) => null);
        dirty.Show(); dirty.Wizard.SetKey(MappingTestKey); dirty.Close();
        MappingAssert(!dirty.IsEditorClosed, "Changed entry discarded silently.");
        DiscardWizard(dirty); MappingAssert(dirty.IsEditorClosed, "Discard did not close.");
        Console.WriteLine("PASS real window capture, late callback rejection, validation, original-preview result, navigation keys, disconnect/reconnect, save/discard and cleanup.");
    }
    private static void TestMappingWizardLayouts(App app, string output)
    {
        var languageMethod = typeof(LocalizationService).GetMethod("ApplyLanguage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var language in new[] { "zh-CN", "zh-TW", "zh-HK", "en-US" })
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            languageMethod.Invoke(null, [language, false, false]); ThemeService.Apply(theme);


            var editor = new KeyboardMappingEditorWindow(MappingEntry(MappedTouchAction.Swipe), [], _ => null,
                _ => null, () => { }, (_, _) => null, previewSurface: WizardSurface) { ShowInTaskbar = false };
            app.MainWindow = editor; editor.Show();
            try
            {
                foreach (var step in editor.Wizard.Steps)
                {
                    WizardTo(editor, step); AdvanceDispatcher(TimeSpan.FromMilliseconds(180));
                    SaveWindowRender(editor, Path.Combine(output, $"wizard-{language}-{theme}-{step}.png"));
                    foreach (var scale in new[] { 1d, 1.25, 1.5 })
                    {
                        // Exercise WPF layout at the effective DIP viewport of each scale,
                        // without changing the user's Windows display settings.
                        editor.Width = 480 * scale; editor.Height = 560 * scale;
                        ((FrameworkElement)editor.Content).LayoutTransform = new ScaleTransform(scale, scale);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40)); editor.UpdateLayout(); CheckWizardNavigation(editor);
                        SaveWindowRender(editor, Path.Combine(output, $"wizard-small-{language}-{theme}-{step}-{scale:0.00}.png"));
                    }
                    ((FrameworkElement)editor.Content).LayoutTransform = Transform.Identity;
                    editor.Width = 680; editor.Height = 760; editor.UpdateLayout();
                }
                editor.WindowState = WindowState.Maximized; editor.UpdateLayout(); CheckWizardNavigation(editor);
            }
            finally { DiscardWizard(editor); }
        }
    }
    private static void TestWizardEditorInputGate(App app)
    {
        var main = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(main, "_viewModel");
        var hwnd = new WindowInteropHelper(main).Handle;
        var ctor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        var device = (DeviceViewModel)ctor.Invoke(["wizard-input-fixture", "Wizard fixture", "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(ctor.GetParameters()[6].ParameterType, "Ready")]);
        SetKeyboardField(vm, "_selectedDevice", device);
        SetKeyboardField(main, "_keyboardForegroundWindow", (Func<nint>)(() => hwnd));
        SetKeyboardField(main, "_keyboardFocusedWindow", (Func<nint>)(() => (nint)123));
        var preview = (NativePreviewHost)main.FindName("MainPreviewHost");
        SetKeyboardField(preview, "_window", (nint)123);
        SetKeyboardField(main, "_keyboardFocusSuspended", false);
        var manager = new KeyboardMappingWindow(new(), _ => null, _ => null, _ => null, () => { }, () => "MappingOff") { Owner = main };
        SetKeyboardField(main, "_mappingWindow", manager);
        manager.Show(); SetKeyboardField(main, "_keyboardFocusSuspended", false);
        try
        {
            MappingAssert((bool)KeyboardCall(main, "CanForwardControlKeyboard", device.Udid, hwnd)! &&
                (bool)KeyboardCall(main, "MappingFocusAllows")!, "Input gate fixture is not initially open.");
            KeyboardCall(manager, "OnAddClick", manager, new RoutedEventArgs());
            var editor = (KeyboardMappingEditorWindow)KeyboardField(manager, "_editor");
            MappingAssert(!(bool)KeyboardCall(main, "CanForwardControlKeyboard", device.Udid, hwnd)! &&
                !(bool)KeyboardCall(main, "MappingFocusAllows")!, "Open wizard allowed keyboard input through a refocused preview.");
            editor.Wizard.SetKey(MappingTestKey); WizardNext(editor);
            MappingAssert(!(bool)KeyboardCall(main, "CanForwardControlKeyboard", device.Udid, hwnd)!, "Leaving capture reopened direct input.");
            DiscardWizard(editor);
            SetKeyboardField(main, "_keyboardFocusSuspended", false);
            MappingAssert((bool)KeyboardCall(main, "CanForwardControlKeyboard", device.Udid, hwnd)! &&
                (bool)KeyboardCall(main, "MappingFocusAllows")!, "Closing wizard left its input guard active.");
        }
        finally
        {
            manager.Close(); SetKeyboardField(main, "_mappingWindow", null);
            SetKeyboardField(preview, "_window", (nint)0); CloseWorkspaceTestWindow(main);
        }
        Console.WriteLine("PASS wizard blocks mapping and all direct-keyboard entry points, including refocused native preview, and releases the gate on close.");
    }
    private static void TestWizardExternalPickTransaction(App app)
    {
        Action<KeyboardMappingEntry?>? complete = null;
        Action<MappedKey>? capture = null;
        var manager = new KeyboardMappingWindow(new(), _ => null, _ => null,
            callback => { capture = callback; return null; }, () => { }, () => "MappingReady",
            beginPick: (_, callback) => { complete = callback; return null; },
            cancelPick: () => complete?.Invoke(null), previewSurface: WizardSurface);
        app.MainWindow = manager; manager.Show();
        KeyboardCall(manager, "OnAddClick", manager, new RoutedEventArgs());
        var editor = (KeyboardMappingEditorWindow)KeyboardField(manager, "_editor");
        try
        {
            capture!(MappingTestKey); WizardNext(editor);
            ((ListBox)editor.FindName("ActionBox")).SelectedValue = MappedTouchAction.Swipe;
            WizardTo(editor, MappingWizardStep.Position);
            ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            MappingAssert(!manager.IsVisible && !editor.IsVisible && editor.State == MappingEditorState.PickingPosition,
                "Wizard obscures the original preview during picking.");
            var result = editor.Wizard.Draft with { X = .2, Y = .7, EndX = .2, EndY = .3, DurationMs = 500, DeviceCoordinates = true };
            complete!(result);
            MappingAssert(manager.IsVisible && editor.IsVisible && editor.Wizard.HasPosition && editor.Wizard.Draft.DurationMs == 500,
                "Original-preview completion did not restore the wizard and gesture.");
            ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            complete!(null);
            MappingAssert(editor.IsVisible && editor.Wizard.Draft == result, "Cancelled repick lost the previous gesture.");
            ((Button)editor.FindName("PickButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DiscardWizard(editor); complete!(result);
            MappingAssert(editor.IsEditorClosed && !editor.IsVisible, "Late pick result reopened the closed editor.");
        }
        finally { DiscardWizard(editor); manager.Close(); }
        Console.WriteLine("PASS original-preview handoff, hide/restore, recorded duration, cancelled repick and late completion after close.");
    }
    private static void CheckWizardNavigation(Window editor)
    {
        var nav = (Grid)editor.FindName("NavigationBar");
        foreach (var button in ComponentVisuals<Button>(nav).Where(b => b.IsVisible))
        {
            var bounds = button.TransformToAncestor(editor).TransformBounds(new Rect(button.RenderSize));
            MappingAssert(bounds.Left >= 0 && bounds.Right <= editor.ActualWidth + 1 && bounds.Bottom <= editor.ActualHeight + 1,
                $"Wizard navigation clipped: {button.Content}, {bounds}, window {editor.ActualWidth}×{editor.ActualHeight}");
        }
    }
}
