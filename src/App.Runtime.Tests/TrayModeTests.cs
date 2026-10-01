using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunTrayModeTests(string output)
    {
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        using var trace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        MainWindow? main = null;
        try
        {
            TestTraySettingsPersistence(output);
            SetApplicationDisplayMode(app, ApplicationDisplayMode.Tray);
            main = new MainWindow();
            app.MainWindow = main;
            // Exercise real hidden startup and shell registration without starting
            // device enumeration or the wireless receiver in a regression test.
            SetKeyboardField(main, "_startupServicesStarted", true);
            var everVisible = false;
            main.IsVisibleChanged += (_, _) => everVisible |= main.IsVisible;
            KeyboardCall(main, "StartInTray");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            InteractionAssert(!everVisible && !main.ShowInTaskbar, "Tray startup flashed the workspace.");
            InteractionAssert(KeyboardField(main, "_trayIcon") is not null, "Tray icon missing.");
            var panel = (TrayPanelWindow)KeyboardField(main, "_trayPanel");
            var vm = main.DataContext;
            KeyboardCall(main, "ShowTrayPanel");
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            InteractionAssert(panel.IsVisible && !main.IsVisible, "Tray icon did not open only the panel.");
            panel.Close(); // Same Closing path as Alt+F4.
            InteractionAssert(!panel.IsVisible, "Closing the panel must hide it.");
            KeyboardCall(main, "ShowTrayPanel");
            InteractionAssert(panel.IsVisible, "Panel could not reopen after Alt+F4.");
            panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(panel), 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            InteractionAssert(!panel.IsVisible, "Escape did not hide the panel.");

            KeyboardCall(main, "ShowTraySettings");
            var settings = (Window)KeyboardField(main, "_traySettingsWindow");
            var first = TrayTestDevice("tray-test-1", "Ray's iPhone — Long device name for layout validation");
            TrayDevices(vm).Add(first);
            vm.GetType().GetProperty("SelectedDevice")!.SetValue(vm, first);
            InteractionAssert(settings.IsVisible, "Device selection closed the global tray settings.");
            settings.Close();
            TestTrayAudioSettings(app, vm, first);
            TestTrayPanelInteractionAndLayout(vm, output);

            TestTrayMediaStop(main, vm);

            foreach (var mode in new[] { ApplicationDisplayMode.Complete, ApplicationDisplayMode.Lightweight })
            {
                vm.GetType().GetProperty("SelectedApplicationDisplayMode")!.SetValue(vm, mode);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
                InteractionAssert(main.IsVisible && main.ShowInTaskbar && KeyboardField(main, "_trayIcon") is null,
                    $"Leaving tray for {mode} did not restore the workspace.");
                vm.GetType().GetProperty("SelectedApplicationDisplayMode")!.SetValue(vm, ApplicationDisplayMode.Tray);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                InteractionAssert(!main.IsVisible && !main.ShowInTaskbar && KeyboardField(main, "_trayIcon") is not null,
                    $"Returning from {mode} to tray failed.");
            }
            TestTrayStartupUpdates(app, main, vm);
            InteractionAssert(trace.Messages.Count == 0, string.Join(Environment.NewLine, trace.Messages));
            Console.WriteLine("Tray tests passed: hidden startup, shell panel reopening, Escape, focus loss, busy/error handling, audio settings, media stop notification, startup updates, mode switching, settings persistence, and 3 languages × 2 themes.");
            return 0;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            File.WriteAllLines(Path.Combine(output, "binding-diagnostics.txt"), trace.Messages);
            if (main is not null) CloseWorkspaceTestWindow(main);
            app.Shutdown();
        }
    }

    private static DeviceViewModel TrayTestDevice(string udid, string name)
    {
        var constructor = typeof(DeviceViewModel).GetConstructors(KeyboardTestMembers).Single();
        return (DeviceViewModel)constructor.Invoke([udid, name, "iPhone15,2", "18.0", "USB", "",
            Enum.Parse(constructor.GetParameters()[6].ParameterType, "Ready")]);
    }

    private static void TestTraySettingsPersistence(string output)
    {
        var type = typeof(App).Assembly.GetType("IPhoneMirror.App.Updater.UpdateSettingsStore")!;
        var store = Activator.CreateInstance(type, KeyboardTestMembers, null,
            [Path.GetFullPath(Path.Combine(output, "settings-roundtrip.json"))], null)!;
        var settings = KeyboardCall(store, "Load")!;
        var property = settings.GetType().GetProperty("ApplicationDisplayMode")!;
        property.SetValue(settings, ApplicationDisplayMode.Tray);
        KeyboardCall(store, "Save", settings);
        InteractionAssert((ApplicationDisplayMode)property.GetValue(KeyboardCall(store, "Load"))! == ApplicationDisplayMode.Tray,
            "Tray mode did not survive settings reload.");
    }

    private static ObservableCollection<DeviceViewModel> TrayDevices(object vm) =>
        (ObservableCollection<DeviceViewModel>)vm.GetType().GetProperty("Devices")!.GetValue(vm)!;

    private static void TestTrayPanelInteractionAndLayout(object vm, string output)
    {
        var pending = new TaskCompletionSource();
        var startCount = 0;
        var constructor = typeof(TrayPanelWindow).GetConstructors(KeyboardTestMembers).Single();
        var panel = (TrayPanelWindow)constructor.Invoke([vm, (Func<DeviceViewModel, Task>)(_ =>
        { startCount++; return pending.Task; }), (Action)(() => { }), (Action)(() => { })]);
        try
        {
            var language = typeof(App).Assembly.GetType("IPhoneMirror.App.Localization.LocalizationService")!
                .GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!;
            var layoutFindings = new HashSet<string>();
            foreach (var culture in new[] { "zh-CN", "zh-HK", "en-US" })
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                language.Invoke(null, [culture, false, true]);
                InteractionAssert(Application.Current.TryFindResource("TrayStartMirroring") is string,
                    $"Missing tray localization for {culture}.");
                ApplyTheme(typeof(App).Assembly, theme);
                panel.Show();
                panel.Activate();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(300));
                panel.UpdateLayout();
                AssertSelfDrawnWindowCorners(panel);
                AssertVisibleButtonsFit(panel);
                SaveWindowRender(panel, Path.Combine(output, $"{culture}-{theme}-tray.png"));
                ExerciseWorkAreaSizes(panel, $"tray/{culture}/{theme}", layoutFindings);
            }
            InteractionAssert(layoutFindings.Count == 0, string.Join(Environment.NewLine, layoutFindings));
            var start = (Button)panel.FindName("StartButton");
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            InteractionAssert(startCount == 1 && !start.IsEnabled, "Rapid clicks started duplicate projections.");
            pending.SetException(new InvalidOperationException("Expected connection failure for regression test."));
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(start.IsEnabled && ((TextBlock)panel.FindName("FeedbackText")).Text.Contains("Expected connection failure"),
                "Projection failure did not restore controls and display feedback.");
            pending = new TaskCompletionSource();
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pending.SetResult();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
            InteractionAssert(!panel.IsVisible, "Successful projection did not hide the panel.");
            panel.Show();
            panel.Activate();
            ((ListBox)panel.FindName("DeviceList")).Focus();
            var other = new Window { Style = new Style(typeof(Window)), Width = 180, Height = 120, ShowInTaskbar = false };
            other.Show();
            other.Activate();
            AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
            other.Close();
            InteractionAssert(!panel.IsVisible, "Losing focus while a device is selected did not hide the panel.");
            TrayDevices(vm).Clear();
            panel.Show();
            panel.UpdateLayout();
            InteractionAssert(!start.IsEnabled, "Empty device list enabled projection.");
            SaveWindowRender(panel, Path.Combine(output, "tray-empty.png"));
        }
        finally { KeyboardCall(panel, "ClosePermanently"); }
    }
}
