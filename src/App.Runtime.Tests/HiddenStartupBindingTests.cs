using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;
using IPhoneMirror.UI.Controls;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunHiddenStartupBindingTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json")));
        var failures = new List<string>();
        using var trace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        void Check(bool condition, string message) { if (!condition) failures.Add(message); }
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                SetApplicationDisplayMode(app, ApplicationDisplayMode.Tray);
                var main = new MainWindow();
                app.MainWindow = main;
                SetKeyboardField(main, "_startupServicesStarted", true);
                try
                {
                    main.StartInTray();
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    SelectTrayThemeTestMode(main, ApplicationDisplayMode.Lightweight);
                    var vm = (MainViewModel)main.DataContext;
                    vm.Devices.CollectionChanged -= (System.Collections.Specialized.NotifyCollectionChangedEventHandler)
                        Delegate.CreateDelegate(typeof(System.Collections.Specialized.NotifyCollectionChangedEventHandler), main,
                            typeof(MainWindow).GetMethod("OnDevicesCollectionChanged", KeyboardTestMembers)!);
                    var mask = (Popup)main.FindName("PreviewTransitionMask");
                    var preview = (FrameworkElement)main.FindName("PreviewPanel");
                    mask.IsOpen = true;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    var maskBody = (FrameworkElement)mask.Child;
                    Console.WriteLine($"{theme}/mask: target={mask.PlacementTarget?.GetType().Name}, " +
                        $"body={maskBody.ActualWidth}x{maskBody.ActualHeight}, preview={preview.ActualWidth}x{preview.ActualHeight}, " +
                        $"widthBinding={BindingOperations.GetBindingExpression(maskBody, FrameworkElement.WidthProperty)?.Status}, " +
                        $"targetBinding={BindingOperations.GetBindingExpression(mask, Popup.PlacementTargetProperty)?.Status}");
                    Check(ReferenceEquals(mask.PlacementTarget, preview) &&
                        Math.Abs(maskBody.ActualWidth - preview.ActualWidth) < 1 &&
                        Math.Abs(maskBody.ActualHeight - preview.ActualHeight) < 1,
                        $"{theme}: hidden startup left the preview transition mask unanchored or wrongly sized.");
                    main.Height -= 40;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                    Check(Math.Abs(maskBody.ActualHeight - preview.ActualHeight) < 1 &&
                        Math.Abs(maskBody.ActualWidth - preview.ActualWidth) < 1,
                        $"{theme}: the preview mask stopped tracking resize.");
                    mask.IsOpen = false;
                    Check(((TextBlock)main.FindName("VersionLabel")).Text == main.VersionText,
                        theme + ": version badge is empty after hidden startup.");
                    InspectBindings(main, theme + "/main");

                    foreach (var hidden in new[] { false, true })
                    {
                        var windows = new List<Window>
                        {
                            new TrayPanelWindow(vm, _ => Task.CompletedTask, () => { }, () => { }),
                            new ImageSettingsWindow(new(0, 100, 100, 100), _ => (true, ""), _ => (true, ""), _ => (true, "")),
                            new WindowStyleSettingsWindow(.6, _ => true, true, _ => true),
                            AuditFixWindow("TextInputWindow", main, "DeviceBindingRenameTitle", "DeviceBindingRenamePrompt", "Phone"),
                            new ProjectionSettingsWindow(vm, () => Task.CompletedTask, () => Task.CompletedTask,
                                () => Task.CompletedTask, () => Task.CompletedTask, () => { }),
                        };
                        foreach (var window in windows)
                        {
                            var context = $"{theme}/{window.GetType().Name}/hidden={hidden}";
                            window.Owner = main;
                            window.ShowInTaskbar = false;
                            if (hidden)
                            {
                                new WindowInteropHelper(window).EnsureHandle();
                                AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                            }
                            window.Show();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                            window.UpdateLayout();
                            try
                            {
                                InspectBindings(window, context);
                                if (window is TrayPanelWindow tray)
                                {
                                    var empty = Visuals(tray).OfType<TextBlock>().Single(t => t.Text == LocalizationService.Get("TrayNoDevices"));
                                    Check(empty.IsVisible, context + ": empty device guidance is hidden.");
                                    vm.Devices.Add(TrayTestDevice("hidden-startup-fixture", "Offline fixture"));
                                    AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                                    Check(!empty.IsVisible, context + ": empty guidance remained over a device list.");
                                    tray.Hide();
                                    vm.Devices.Clear();
                                    tray.Show();
                                    AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                                    Check(empty.IsVisible, context + ": empty guidance did not return after hiding/removing the device.");
                                }
                                if (window is ImageSettingsWindow or WindowStyleSettingsWindow)
                                {
                                    foreach (var slider in Visuals(window).OfType<Slider>())
                                    {
                                        var label = Visuals(window).OfType<TextBlock>().Single(t =>
                                            BindingOperations.GetBinding(t, TextBlock.TextProperty) is { } b &&
                                            b.Path?.Path == "Value" && (b.ElementName == slider.Name || ReferenceEquals(b.Source, slider)));
                                        foreach (var value in new[] { slider.Minimum + 11, slider.Maximum - 7 })
                                        {
                                            slider.Value = value;
                                            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                                            Check(label.Text.StartsWith(slider.Value.ToString("F0")), context + ": stale numeric label for " + slider.Name);
                                        }
                                    }
                                }
                                if (window.GetType().Name == "TextInputWindow")
                                    Check(ReferenceEquals(AutomationProperties.GetLabeledBy((DependencyObject)window.FindName("InputBox")),
                                        window.FindName("InputLabel")), context + ": input accessibility label detached.");
                                var surface = (Border)window.Template.FindName("RoundedWindowSurface", window);
                                var clip = (RoundedContentClip)window.Template.FindName("RoundedWindowContentClip", window);
                                surface.CornerRadius = new CornerRadius(31);
                                AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                                Check(clip.Radius == 31, context + ": shared template lost its corner-radius binding.");
                                SaveWindowRender(window, Path.Combine(output, context.Replace('/', '-') + ".png"));
                            }
                            finally
                            {
                                if (window is TrayPanelWindow tray) tray.ClosePermanently();
                                else window.Close();
                            }
                            Console.WriteLine("CHECK " + context);
                        }
                    }
                    foreach (var surface in new[]
                    {
                        "workspace-mirroring", "workspace-devices", "workspace-settings", "workspace-output",
                        "about", "advanced-settings", "text-input", "device-binding", "airplay-device-selection",
                        "bluetooth-connection", "bluetooth-client-binding", "bluetooth-control-notice",
                        "shortcut-settings", "reverse-control-status", "prompt", "reverse-control-wired-prerequisite",
                        "reverse-control-wireless-prerequisite", "reverse-control-error", "capture-error", "session-closed",
                        "usb-config-error", "capture-recovery", "image-settings", "projection-settings", "media-output",
                        "usb-mode", "startup-error", "update", "instance-conflict", "protected-content", "native-preview",
                    })
                    {
                        var before = app.Windows.Cast<Window>().ToHashSet();
                        try
                        {
                            main.OpenDeveloperSurface(surface);
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                            foreach (var window in app.Windows.Cast<Window>().Where(w => w.IsVisible).ToArray())
                            {
                                window.UpdateLayout();
                                InspectBindings(window, $"{theme}/catalog/{surface}");
                                foreach (var tabs in Visuals(window).OfType<TabControl>().ToArray())
                                {
                                    var selected = tabs.SelectedIndex;
                                    for (var index = 0; index < tabs.Items.Count; index++)
                                    {
                                        tabs.SelectedIndex = index;
                                        AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
                                        InspectBindings(window, $"{theme}/catalog/{surface}/tab={index}");
                                    }
                                    tabs.SelectedIndex = selected;
                                }
                            }
                        }
                        catch (Exception error) { failures.Add($"{theme}/catalog/{surface}: {error.GetBaseException().Message}"); }
                        finally
                        {
                            foreach (var window in app.Windows.Cast<Window>().Where(w => !before.Contains(w)).ToArray())
                                CloseAuditWindow(window);
                        }
                        Console.WriteLine($"CHECK {theme}/catalog/{surface}");
                    }
                    foreach (var step in Enum.GetValues<MappingWizardStep>())
                    {
                        var editor = new KeyboardMappingEditorWindow(new KeyboardMappingEntry
                        { Key = new MappedKey(0x41, 30, false), Action = MappedTouchAction.LongPress },
                            [], _ => null, _ => null, () => { }, (_, _) => null) { Owner = main };
                        try
                        {
                            while (editor.Wizard.Current != step)
                                if (!editor.Wizard.MoveNext()) throw new InvalidOperationException("Unreachable mapping step: " + step);
                            new WindowInteropHelper(editor).EnsureHandle();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                            editor.Show();
                            AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                            InspectBindings(editor, $"{theme}/mapping/{step}");
                        }
                        finally { CloseAuditWindow(editor); }
                    }
                }
                finally { CloseWorkspaceTestWindow(main); }
            }
            failures.AddRange(trace.Messages);
            File.WriteAllLines(Path.Combine(output, "failures.txt"), failures);
            foreach (var failure in failures) Console.Error.WriteLine(failure);
            Console.WriteLine($"Hidden startup binding audit: {failures.Count} failures.");
            return failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            app.Shutdown();
        }

        void InspectBindings(Window window, string context)
        {
            foreach (var element in Visuals(window).OfType<FrameworkElement>().Where(e => e.IsVisible && e.IsLoaded))
            {
                foreach (var property in element.GetType().GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .Where(f => f.FieldType == typeof(DependencyProperty)).Select(f => (DependencyProperty)f.GetValue(null)!).Distinct())
                {
                    if (BindingOperations.GetBindingExpressionBase(element, property) is { Status: not BindingStatus.Active } binding)
                        failures.Add($"{context}: inactive {element.GetType().Name}#{element.Name}.{property.Name}: {binding.Status}, " +
                            $"path={(binding as BindingExpression)?.ParentBinding.Path?.Path}.");
                }
            }
        }
    }
}
