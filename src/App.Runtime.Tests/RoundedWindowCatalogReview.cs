using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    // Preview fixtures only: no device discovery, installation, sign-in or downloads.
    private static int RunRoundedWindowCatalogReview(string output, string? driverAssembly = null)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var assembly = driverAssembly is null ? typeof(App).Assembly : Assembly.LoadFrom(Path.GetFullPath(driverAssembly));
        var app = driverAssembly is null ? new App { IsUiPreviewMode = true } :
            (Application)Activator.CreateInstance(assembly.GetType("IPhoneMirror.DriverInstaller.App")!)!;
        if (driverAssembly is not null)
            app.GetType().GetProperty("IsUiPreviewMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, true);
        app.GetType().GetMethod("InitializeComponent")!.Invoke(app, null);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Action<AppTheme> apply;
        Window owner;
        var factories = new Dictionary<string, Func<Window>>();
        if (driverAssembly is null)
        {
            owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
            apply = ThemeService.Apply;
            var main = (MainWindow)owner;
            var vm = (MainViewModel)owner.DataContext;
            foreach (var key in new[] { "about", "advanced-settings", "text-input", "device-binding", "airplay-device-selection",
                "bluetooth-connection", "bluetooth-client-binding", "bluetooth-control-notice", "shortcut-settings",
                "reverse-control-status", "prompt", "capture-error", "capture-recovery", "image-settings", "projection-settings",
                "media-output", "usb-mode", "startup-error", "update", "instance-conflict", "protected-content" })
                factories[key] = () =>
                {
                    main.OpenDeveloperSurface(key);
                    return app.Windows.Cast<Window>().Last(w => w != owner && w.IsVisible);
                };
            factories["developer-tools"] = () => new DeveloperToolsWindow(main);
            factories["window-style"] = () => new WindowStyleSettingsWindow(1, _ => true, true, _ => true);
            factories["component-download"] = () => new ComponentDownloadWindow(previewOnly: true);
            factories["first-run"] = () => new FirstRunSetupWindow(vm,
                new FirstRunSetupStore(Path.Combine(output, "setup-fixture.json")), previewOnly: true);
            factories["tray"] = () => new TrayPanelWindow(vm, _ => Task.CompletedTask, () => { }, () => { });
            factories["mapping"] = () => new KeyboardMappingWindow(new(), _ => null, _ => null, _ => null, () => { }, () => "MappingOff");
            factories["mapping-editor"] = () => new KeyboardMappingEditorWindow(new() { Key = new(0x41, 30, false) },
                [], _ => null, _ => null, () => { }, (_, _) => null);
            factories["toolbar"] = () => new PreviewToolbarSettingsWindow(new() { Items = [] }, [], _ => { }, _ => true);
            factories["ipa-choice"] = () => new PreviewIpaActionWindow("Test iPhone", [@"C:\Fixture\demo.ipa"]);
            factories["ipa-install"] = () => new PreviewIpaInstallWindow("Test iPhone", "fixture", [@"C:\Fixture\demo.ipa"],
                () => true, (_, _) => Task.CompletedTask);
            factories["file-transfer"] = () => new PreviewFileTransferWindow("Test iPhone", [], 0, () => true,
                (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { apps = Array.Empty<object>() })));
            var handle = new WindowInteropHelper(owner).Handle;
            factories["mapping-overlay"] = () => new KeyboardMappingOverlayWindow(new(handle, handle, "fixture", 1, 390, 844, 0,
                BluetoothMouseDirection.Up, BluetoothMouseDirection.Right, false, false));
            factories["mapping-picking-toolbar"] = () => (Window)Activator.CreateInstance(
                typeof(KeyboardMappingOverlayWindow).GetNestedType("PickingToolbarWindow", BindingFlags.NonPublic)!,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [owner, new Border { Width = 180, Height = 40 }], null)!;
            factories["protected-overlay"] = () => ProtectedContentOverlayWindow.ShowFor(handle, "Test audio");
            factories["text-feedback"] = () => new ScreenTextFeedbackWindow(handle, owner, "Test feedback", () => true);
            factories["text-selection"] = () => new ScreenTextSelectionWindow(handle,
                BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16),
                () => new(new Rect(0, 0, 390, 844), new Rect(0, 0, 390, 844)), () => true, _ => { });
        }
        else
        {
            Window Create(string name, params object?[] args) => (Window)Activator.CreateInstance(
                assembly.GetType("IPhoneMirror.DriverInstaller." + name)!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, args, null)!;
            var localization = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverLocalization")!;
            localization.GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new[] { "--language", "zh-CN" }]);
            app.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
                { Source = new Uri("/iPhoneMirror.Driver;component/Localization/Strings.zh-CN.xaml", UriKind.Relative) });
            var service = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverThemeService")!;
            var themeType = assembly.GetType("IPhoneMirror.DriverInstaller.Services.DriverThemeMode")!;
            apply = theme => service.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [Enum.Parse(themeType, theme.ToString()), false]);
            owner = Create("MainWindow", true);
            app.MainWindow = owner;
            owner.Show();
            factories["prompt"] = () => Create("Windows.PromptWindow", "Test", "Review fixture", "OK", "Cancel", true, false, false);
            factories["required-action"] = () => Create("Windows.RequiredActionWindow", "Test", "Review fixture", "Continue");
            factories["failure-help"] = () => Create("Windows.FailureHelpWindow", "Review fixture", @"C:\Fixture\driver.log");
            factories["device-trust"] = () => Create("Windows.DeviceTrustWindow");
            factories["parent-driver"] = () => Create("Windows.ParentDriverWindow", CreateParentAuditDevice(assembly),
                CreateParentAuditChoices(assembly), "Review fixture");
        }
        var failures = new List<string>();
        var samples = new List<object>();
        var covered = new HashSet<Type> { owner.GetType() };
        var cover = new Window { Width = 300, Height = 200, Style = null, ShowInTaskbar = false, Background = Brushes.DarkSlateGray };
        void Check(Window window, string context)
        {
            window.UpdateLayout();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            var result = RoundedRecoveryGetDwmAttribute(source.Handle, 38, out var backdrop, sizeof(int));
            var cornerResult = RoundedRecoveryGetDwmAttribute(source.Handle, 33, out var corner, sizeof(int));
            var alpha = source.CompositionTarget!.BackgroundColor.A;
            var ok = !window.AllowsTransparency || alpha == 0 && (result != 0 || backdrop == 1);
            if (!window.AllowsTransparency && cornerResult == 0)
                ok &= corner == (window.WindowState == WindowState.Normal ? 2 : 1);
            if (window.AllowsTransparency && window.GetType().BaseType?.Name == "RoundedWindow")
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                foreach (var (x, y) in new[] { (0, 0), (bitmap.PixelWidth - 1, 0), (0, bitmap.PixelHeight - 1), (bitmap.PixelWidth - 1, bitmap.PixelHeight - 1) })
                {
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                    ok &= pixel[3] == 0;
                }
            }
            samples.Add(new { context, window = window.GetType().FullName, layered = window.AllowsTransparency, backdrop, result, corner, cornerResult, alpha, ok });
            if (!ok) failures.Add(context + ": opaque compositor or inherited backdrop");
        }
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                apply(theme);
                foreach (var (key, factory) in factories)
                {
                    var context = theme + "/" + key;
                    try
                    {
                        var window = factory();
                        covered.Add(window.GetType());
                        if (!window.IsVisible)
                        {
                            if (new WindowInteropHelper(window).Owner == 0) window.Owner = owner;
                            window.ShowInTaskbar = false;
                            window.Show();
                        }
                        if (driverAssembly is null) ThemeService.Attach(window);
                        var closed = false;
                        window.Closed += (_, _) => closed = true;
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                        Check(window, context + "/show");
                        apply(theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
                        Check(window, context + "/theme");
                        cover.Show();
                        RoundedRecoverySetActiveWindow(new WindowInteropHelper(cover).Handle);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
                        if (closed)
                        {
                            InteractionAssert(window is ScreenTextSelectionWindow, "Unexpected dismissal: " + context);
                            samples.Add(new { context = context + "/dismiss-on-deactivation", ok = true });
                            continue;
                        }
                        if (!window.IsVisible) window.Show(); // Tray panel dismisses on deactivation.
                        if (window.ShowActivated) RoundedRecoverySetActiveWindow(new WindowInteropHelper(window).Handle);
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                        Check(window, context + "/foreground");
                        cover.Hide();
                        window.Hide();
                        apply(theme);
                        window.Show();
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(220));
                        Check(window, context + "/reopen");
                        window.WindowState = WindowState.Minimized;
                        window.WindowState = WindowState.Normal;
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
                        Check(window, context + "/restore");
                        if (window.AllowsTransparency && window.GetType().BaseType?.Name == "RoundedWindow")
                            AssertSelfDrawnWindowCorners(window);
                        Check(window, context + "/corners-resize");
                        SaveWindowRender(window, Path.Combine(output, theme + "-" + key + ".png"));
                    }
                    catch (Exception error) { failures.Add(context + ": " + error.GetBaseException().Message); }
                    finally
                    {
                        foreach (var window in app.Windows.Cast<Window>().Where(w => w != owner && w != cover).ToArray())
                        {
                            if (window is TrayPanelWindow tray) tray.ClosePermanently();
                            else
                            {
                                window.GetType().GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, true);
                                CloseAuditWindow(window);
                            }
                        }
                        AdvanceDispatcher(TimeSpan.FromMilliseconds(25));
                    }
                }
                Check(owner, theme + "/owner");
            }
            foreach (var type in assembly.GetTypes().Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract &&
                t.Namespace?.StartsWith(driverAssembly is null ? "IPhoneMirror.App" : "IPhoneMirror.DriverInstaller", StringComparison.Ordinal) == true))
                if (!covered.Contains(type)) failures.Add("Unreviewed window type: " + type.FullName);
        }
        finally
        {
            cover.Close();
            if (owner is MainWindow main) CloseWorkspaceTestWindow(main); else owner.Close();
            app.Shutdown();
            File.WriteAllText(Path.Combine(output, "catalog-review.json"), JsonSerializer.Serialize(new
                { covered = covered.Select(t => t.FullName).Order(), samples, failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"Window catalog: {covered.Count} types, {samples.Count} checks, {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;
    }
}
