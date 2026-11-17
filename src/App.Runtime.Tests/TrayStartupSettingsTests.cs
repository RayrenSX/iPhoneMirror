using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunTrayStartupSettingsTests(string output)
    {
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(Path.GetFullPath(output), "settings.json")));
        using var trace = new AuditTraceListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var mode in new[] { ApplicationDisplayMode.Lightweight, ApplicationDisplayMode.Complete })
            foreach (var savedCategory in new[] { "projection", "control", "application" })
            {
                ApplyTheme(typeof(App).Assembly, theme);
                SetApplicationDisplayMode(app, ApplicationDisplayMode.Tray);
                app.UpdateSettings.SettingsCategory = savedCategory;
                var main = new MainWindow();
                app.MainWindow = main;
                SetKeyboardField(main, "_startupServicesStarted", true);
                try
                {
                    main.StartInTray();
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                    InteractionAssert(!main.IsVisible && !main.IsLoaded, "Fixture did not start hidden in tray.");
                    SelectTrayThemeTestMode(main, mode);
                    var vm = (MainViewModel)main.DataContext;
                    InteractionAssert(!vm.IsCapturing && vm.SelectedDevice is null && vm.Devices.Count == 0,
                        "Settings regression unexpectedly started capture or device discovery.");
                    KeyboardCall(main, "SetSettingsPanelVisible", true);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
                    var categories = new[] { "Application", "Projection", "Control" };
                    var restoredCategory = categories.Single(c => c.ToLowerInvariant() == savedCategory);
                    CheckPage(restoredCategory);
                    foreach (var category in new[] { "Application", "Projection", "Control" })
                    {
                        ((RadioButton)main.FindName(category + "SettingsTab")).IsChecked = true;
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                        main.UpdateLayout();
                        CheckPage(category);
                        SaveWindowRender(main, Path.Combine(output, $"{theme}-{mode}-{savedCategory}-{category}.png"));
                    }
                    KeyboardCall(main, "SetSettingsPanelVisible", false);
                    KeyboardCall(main, "SetSettingsPanelVisible", true);
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(500));
                    CheckPage("Control");
                    vm.SelectedApplicationDisplayMode = ApplicationDisplayMode.Tray;
                    AdvanceDispatcher(TimeSpan.FromMilliseconds(150));
                    SelectTrayThemeTestMode(main, mode);
                    CheckPage("Control");
                    Console.WriteLine($"PASS {theme}/{mode}/saved={savedCategory}: first show, three pages, reopen, tray round trip.");

                    void CheckPage(string category)
                    {
                        var scroll = (ScrollViewer)main.FindName(category + "SettingsScrollViewer");
                        var content = (FrameworkElement)scroll.Content;
                        InteractionAssert(scroll.IsVisible && scroll.ActualWidth > 100 && scroll.ViewportHeight > 100 &&
                            content.ActualHeight > 100 && ReferenceEquals(content.DataContext, vm) &&
                            FindVisualDescendants<Control>(content).Any(c => c.IsVisible),
                            $"Tray startup -> {mode}: {category} settings are blank (saved={savedCategory}).");
                        foreach (var other in categories.Where(c => c != category))
                            InteractionAssert(((ScrollViewer)main.FindName(other + "SettingsScrollViewer")).Visibility == Visibility.Collapsed,
                                "Selecting a settings category left another page overlapping it.");
                    }
                }
                finally { CloseWorkspaceTestWindow(main); }
            }
            InteractionAssert(trace.Messages.Count == 0, string.Join(Environment.NewLine, trace.Messages));
            Console.WriteLine("PASS tray startup settings: dark/light, complete/lightweight, all saved categories, no devices or capture, no binding warnings.");
            return 0;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            File.WriteAllLines(Path.Combine(output, "binding-diagnostics.txt"), trace.Messages);
            foreach (var message in trace.Messages) Console.Error.WriteLine(message);
            app.Shutdown();
        }
    }
}
