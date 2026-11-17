using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IPhoneMirror.App.Controls;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Updater;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceHubFileImportTests(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var imported = new List<(string Device, string Directory, string Source)>();
        var visible = new List<string>();
        var ready = true;
        string? failSource = null;
        var failRefresh = false;
        TaskCompletionSource<JsonElement>? pending = null;
        async Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken ct)
        {
            var arguments = JsonSerializer.SerializeToElement(args);
            if (action == "upload_file")
            {
                imported.Add((device.Udid, arguments.GetProperty("path").GetString()!, arguments.GetProperty("sourcePath").GetString()!));
                Verify(arguments.GetProperty("root").GetString() == "media", "Imports must use the media file service.");
                if (pending is not null) return await pending.Task; // Deliberately model a late response.
                if (imported[^1].Source == failSource) throw new AutomationException("FILE_IMPORT_FAILED", "Fixture disk full.");
                visible.Add(Path.GetFileName(imported[^1].Source));
                return JsonSerializer.SerializeToElement(new { status = "imported" });
            }
            ct.ThrowIfCancellationRequested();
            if (action == "list_dir")
            {
                if (failRefresh) { failRefresh = false; throw new IOException("Fixture refresh failure."); }
                var directory = arguments.GetProperty("path").GetString()!;
                return JsonSerializer.SerializeToElement(new
                {
                    entries = visible.Select(name => new { name, path = directory.Length == 0 ? name : directory + "/" + name, type = "file", size = 12 }),
                    nextOffset = (int?)null, total = visible.Count
                });
            }
            return JsonSerializer.SerializeToElement(new { apps = Array.Empty<object>() });
        }
        using var panel = new DeviceHubPanel(vm, Request, _ => ready, Path.Combine(output, "icons"), (_, _) => Task.CompletedTask);
        SetKeyboardField(window, "_deviceHubPanel", panel);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = panel;
        DeviceViewModel Device(string id) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = "Ray's iPhone", ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = ConnectionState.Ready });
        var first = Device("file-import-first");
        var second = Device("file-import-second");
        T Find<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        void Finish()
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(40));
            WaitReviewTask((Task)KeyboardField(panel, "_operation"));
            FinishWorkspaceReveal(window); window.UpdateLayout();
        }
        void Import()
        {
            Find<Button>("FileImport").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Finish();
        }
        var language = LocalizationService.SelectedLanguage;
        var theme = ThemeService.Preference;
        try
        {
            var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
            guidance.Add(first.Udid); guidance.Add(second.Udid);
            vm.Devices.Add(first); vm.Devices.Add(second);
            vm.SelectedDevice = first;
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            ((RadioButton)Find<System.Windows.Controls.Primitives.UniformGrid>("PageTabs").Children[3]).IsChecked = true; Finish();
            Verify(Find<Button>("FileImport").IsVisible && Find<Button>("FileImport").IsEnabled, "Media files need a visible import button.");
            Find<TextBox>("FilePath").Text = "/Downloads";
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-browse"); Finish();
            Find<TextBox>("FilePath").Text = "/unfinished-address";
            panel.PickImportFiles = _ => ["C:/fixtures/资料.txt", "C:/fixtures/video.mp4"];
            Import();
            Verify(imported.Count == 2 && imported.All(call => call.Device == first.Udid && call.Directory == "Downloads"),
                "Multi-file import must keep the original device and open folder, ignoring draft address edits.");
            Verify(Find<ListBox>("FilesList").Items.Count == 2 && Find<TextBox>("FilePath").Text == "/Downloads" &&
                Find<TextBlock>("StatusText").Text == LocalizationService.Format("DeviceHubPanelImported", 2, "/Downloads"),
                "Successful imports must refresh the open folder and report the completed count.");
            var menu = (ContextMenu)KeyboardCall(panel, "PopulateListMenu", Find<ListBox>("FilesList"))!;
            Verify(menu.Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "file-import" && item.IsEnabled),
                "The media folder context menu must offer import.");

            panel.PickImportFiles = _ => null;
            Import(); Verify(imported.Count == 2 && Find<TextBlock>("StatusText").Text.Length == 0, "Cancelling the picker must not upload anything.");
            panel.PickImportFiles = _ => ["C:/fixtures/first.txt", "C:/fixtures/full.txt", "C:/fixtures/never.txt"];
            failSource = "C:/fixtures/full.txt";
            Import();
            Verify(imported.Count == 4 && visible.Count == 3 && Find<ListBox>("FilesList").Items.Count == 3 &&
                Find<TextBlock>("StatusText").Text == LocalizationService.Format("DeviceHubPanelImportFailed", 1, 3,
                    LocalizationService.Get("DeviceHubPanelFileImportFailed")),
                "A failed batch must stop, refresh completed files, and report partial success.");
            failSource = null;
            panel.PickImportFiles = _ => ["C:/fixtures/refresh.txt"];
            failRefresh = true; Import();
            Verify(Find<TextBlock>("StatusText").Text == LocalizationService.Format("DeviceHubPanelImportRefreshFailed", 1, "/Downloads"),
                "A refresh error must not claim a completed transfer failed.");
            _ = KeyboardCall(panel, "RefreshAsync"); Finish();

            Find<ComboBox>("FileRoot").SelectedIndex = 1; Finish();
            Verify(!Find<Button>("FileImport").IsVisible, "Crash reports must remain read-only.");
            menu = (ContextMenu)KeyboardCall(panel, "PopulateListMenu", Find<ListBox>("FilesList"))!;
            Verify(!menu.Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "file-import"), "Crash report menus must not offer import.");
            var before = imported.Count;
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-import"); Finish();
            Verify(imported.Count == before, "The import action must reject the crash-report root.");
            Find<ComboBox>("FileRoot").SelectedIndex = 0; Finish();
            ready = false; panel.SetDevice(first, true); Finish();
            Verify(!Find<Button>("FileImport").IsEnabled, "Disconnected devices must disable imports.");
            ready = true; panel.SetDevice(first, true); Finish();

            panel.PickImportFiles = _ => ["C:/fixtures/pending.txt", "C:/fixtures/must-not-follow.txt"];
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-import"); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            Verify(!Find<Button>("FileImport").IsEnabled && Find<TextBlock>("StatusText").Text.Contains("pending.txt"), "Imports must show text progress and prevent duplicates.");
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-import");
            Verify(imported.Count == before + 1, "A second import must not start during a transfer.");
            vm.SelectedDevice = second;
            pending.SetResult(JsonSerializer.SerializeToElement(new { status = "imported" })); Finish(); pending = null;
            Verify(imported.Count == before + 1 && !Find<TextBlock>("StatusText").Text.Contains("pending.txt"),
                "A late import response must not refresh another device or upload the rest of the old batch.");
            panel.PickImportFiles = _ => { vm.SelectedDevice = first; return ["C:/fixtures/stale-picker.txt"]; };
            Import(); Verify(imported.Count == before + 1, "A picker opened for another device must not dispatch after selection changes.");

            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture);
                ThemeService.Apply(AppTheme.Dark);
                // Exercise the narrow inspector size, including the longest label.
                panel.Width = 400; Finish();
                var button = Find<Button>("FileImport");
                var root = Find<ComboBox>("FileRoot");
                Verify(button.ActualWidth > 60 && root.ActualWidth > 100 && button.TranslatePoint(new Point(button.ActualWidth, 0), panel).X <= 400,
                    $"The import button and file source must fit the inspector in {culture}: button={button.ActualWidth}, root={root.ActualWidth}, end={button.TranslatePoint(new Point(button.ActualWidth, 0), panel).X}, panel={panel.ActualWidth}.");
            }
            LocalizationService.SetLanguage("zh-CN");
            panel.Width = double.NaN;
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            SaveWindowRender(window, Path.Combine(output, "file-import-dark.png"));
            Console.WriteLine("PASS file imports: multi-select, open-folder routing, refresh, partial failure, read-only roots, disconnects, stale responses and five languages.");
        }
        finally
        {
            pending?.TrySetCanceled();
            panel.Dispose();
            window.Close();
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme);
            app.Shutdown();
        }
        return 0;
    }
}
