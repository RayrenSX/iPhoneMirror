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
using IPhoneMirror.App.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunDeviceHubFileDeleteTests(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App { IsUiPreviewMode = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SetKeyboardField(app, "_settingsStore", new UpdateSettingsStore(Path.Combine(output, "settings.json")));
        SetApplicationDisplayMode(app, ApplicationDisplayMode.Complete);
        var language = LocalizationService.SelectedLanguage;
        var theme = ThemeService.Preference;
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var vm = (MainViewModel)KeyboardField(window, "_viewModel");
        var calls = new List<(string Device, string Path)>();
        var files = new List<string> { "capture (2).log", "keep.txt" };
        var ready = true;
        var failRefresh = false;
        string? failDelete = null;
        TaskCompletionSource<JsonElement>? pending = null;
        async Task<JsonElement> Request(DeviceViewModel device, string action, object? args, CancellationToken ct)
        {
            var arguments = JsonSerializer.SerializeToElement(args);
            if (action == "delete_file")
            {
                var path = arguments.GetProperty("path").GetString()!;
                calls.Add((device.Udid, path));
                Verify(arguments.GetProperty("root").GetString() == "media" &&
                    arguments.GetProperty("expected").GetProperty("modified").GetInt32() == 123,
                    "Deletion must include the media root and selected listing snapshot.");
                if (pending is not null) return await pending.Task;
                if (failDelete is not null) throw new AutomationException(failDelete, "Fixture deletion failure.");
                files.Remove(Path.GetFileName(path));
                return JsonSerializer.SerializeToElement(new { status = "deleted" });
            }
            ct.ThrowIfCancellationRequested();
            if (action == "list_dir")
            {
                if (failRefresh) { failRefresh = false; throw new IOException("Fixture refresh failure."); }
                var path = arguments.GetProperty("path").GetString()!;
                var entries = files.Select(name => new { name, path = path.Length == 0 ? name : path + "/" + name, type = "file", size = 12, modified = 123 })
                    .Concat(new[] { new { name = "Downloads", path = "Downloads", type = "directory", size = 0, modified = 123 } });
                return JsonSerializer.SerializeToElement(new { entries, nextOffset = (int?)null, total = files.Count + 1 });
            }
            return JsonSerializer.SerializeToElement(new { apps = Array.Empty<object>() });
        }
        using var panel = new DeviceHubPanel(vm, Request, _ => ready, Path.Combine(output, "icons"), (_, _) => Task.CompletedTask);
        SetKeyboardField(window, "_deviceHubPanel", panel);
        ((ContentControl)window.FindName("DeviceHubPanelHost")).Content = panel;
        DeviceViewModel Device(string id) => DeviceViewModel.FromNative(new NativeDeviceInfo
        { Udid = id, Name = "Ray's iPhone", ProductType = "iPhone13,1", OsVersion = "18.7.8", ConnectionType = "USB", State = ConnectionState.Ready });
        var first = Device("file-delete-first"); var second = Device("file-delete-second");
        T Find<T>(string name) where T : FrameworkElement => (T)panel.FindName(name);
        var list = Find<ListBox>("FilesList");
        void Finish()
        {
            AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            WaitReviewTask((Task)KeyboardField(panel, "_operation"));
            FinishWorkspaceReveal(window); window.UpdateLayout();
        }
        void Select(string path) => list.SelectedItem = list.Items.Cast<object>().Single(row =>
            row.GetType().GetProperty("Id")!.GetValue(row)?.ToString() == path);
        ContextMenu Menu() => (ContextMenu)KeyboardCall(panel, "PopulateListMenu", list)!;
        MenuItem DeleteItem() => Menu().Items.OfType<MenuItem>().Single(item => item.Tag?.ToString() == "file-delete");
        void Delete() { DeleteItem().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Finish(); }
        void Refresh() { _ = KeyboardCall(panel, "RefreshAsync"); Finish(); }
        try
        {
            var guidance = (HashSet<string>)KeyboardField(window, "_deviceProfileGuidanceShown");
            guidance.Add(first.Udid); guidance.Add(second.Udid);
            vm.Devices.Add(first); vm.Devices.Add(second); vm.SelectedDevice = first;
            KeyboardCall(window, "SetDeviceHubPanelVisible", true); Finish();
            ((RadioButton)Find<System.Windows.Controls.Primitives.UniformGrid>("PageTabs").Children[3]).IsChecked = true; Finish();
            Select("Downloads");
            Verify(!Menu().Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "file-delete"), "Folders must not offer file deletion.");
            list.SelectedItem = null;
            Verify(!Menu().Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "file-delete"), "Blank space must not reuse an old deletion target.");
            Select("capture (2).log");
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                LocalizationService.SetLanguage(culture); ThemeService.Apply(AppTheme.Dark);
                Exception? promptError = null;
                var shown = false;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
                timer.Tick += (_, _) =>
                {
                    var prompt = app.Windows.OfType<AppPromptWindow>().SingleOrDefault();
                    if (prompt is null) return;
                    timer.Stop(); shown = true;
                    try
                    {
                        Verify(prompt.CancelIsDefault && !prompt.ConfirmIsDefault && prompt.PromptBody.Contains("/capture (2).log") &&
                            prompt.ConfirmText == LocalizationService.Get("DeviceHubPanelDeleteFile"),
                            "Deletion needs a named, destructive confirmation with Cancel as default.");
                        SaveWindowRender(prompt, Path.Combine(output, "delete-confirm-" + culture + ".png"));
                    }
                    catch (Exception error) { promptError = error; }
                    finally { KeyboardCall(prompt, "OnCancelClick", prompt, new RoutedEventArgs()); }
                };
                timer.Start(); Delete(); timer.Stop();
                if (promptError is not null) throw promptError;
                Verify(shown && calls.Count == 0, "Cancelling the real confirmation must not delete a file.");
            }
            LocalizationService.SetLanguage("zh-CN");
            Find<TextBox>("FilePath").Text = "/Downloads";
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-browse"); Finish();
            Select("Downloads/capture (2).log");
            Find<TextBox>("FilePath").Text = "/unfinished-address";
            panel.ConfirmFileDeletion = (owner, body) =>
            {
                Verify(owner == window && body.Contains("capture (2).log") && body.Contains("/Downloads/capture (2).log"),
                    "The confirmation must show the exact filename and path.");
                Select("Downloads/keep.txt"); return true;
            };
            Delete();
            Verify(calls.SequenceEqual(new[] { (first.Udid, "Downloads/capture (2).log") }) && files.SequenceEqual(new[] { "keep.txt" }) &&
                Find<TextBox>("FilePath").Text == "/Downloads" && list.Items.Count == 2 &&
                Find<TextBlock>("StatusText").Text == LocalizationService.Format("DeviceHubPanelFileDeleted", "capture (2).log"),
                "Confirmed deletion must retain the original target, ignore draft paths, and refresh the open folder.");

            panel.ConfirmFileDeletion = (_, _) => true;
            foreach (var code in new[] { "FILE_DELETE_FAILED", "FILE_DELETE_CHANGED" })
            {
                Select("Downloads/keep.txt"); failDelete = code; Delete();
                Verify(files.Contains("keep.txt") && Find<TextBlock>("StatusText").Text == LocalizationService.Get(
                    code == "FILE_DELETE_FAILED" ? "DeviceHubPanelFileDeleteFailed" : "DeviceHubPanelFileDeleteChanged"),
                    "Deletion errors must keep the file and report a useful message.");
            }
            failDelete = null; Select("Downloads/keep.txt"); failRefresh = true; Delete();
            Verify(Find<TextBlock>("StatusText").Text == LocalizationService.Format("DeviceHubPanelFileDeleteRefreshFailed", "keep.txt"),
                "A refresh error must preserve the successful deletion result.");
            files.Add("keep.txt"); Refresh(); Select("Downloads/keep.txt");
            var count = calls.Count;
            var stale = DeleteItem();
            Find<ComboBox>("FileRoot").SelectedIndex = 1; Finish();
            Select("keep.txt");
            Verify(!Menu().Items.OfType<MenuItem>().Any(item => item.Tag?.ToString() == "file-delete"), "Crash reports must remain read-only.");
            stale.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-delete"); Finish();
            Verify(calls.Count == count, "Old menus and direct actions cannot delete crash reports.");
            Find<ComboBox>("FileRoot").SelectedIndex = 0; Finish(); Select("keep.txt");
            ready = false; panel.SetDevice(first, true); Finish();
            Verify(!DeleteItem().IsEnabled, "Disconnected devices must disable deletion.");
            ready = true; panel.SetDevice(first, true); Finish(); Select("keep.txt");
            panel.ConfirmFileDeletion = (_, _) => { vm.SelectedDevice = second; return true; };
            Delete(); Verify(calls.Count == count, "Switching devices during confirmation must cancel deletion.");
            panel.ConfirmFileDeletion = (_, _) => true; Select("keep.txt");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-delete"); AdvanceDispatcher(TimeSpan.FromMilliseconds(30));
            Verify(calls.Count == count + 1 && !DeleteItem().IsEnabled && ((bool)KeyboardField(panel, "_busy")),
                "A pending deletion must prevent a duplicate and report its busy state.");
            _ = KeyboardCall(panel, "InvokeActionAsync", "file-delete");
            Verify(calls.Count == count + 1, "A pending deletion must not dispatch twice.");
            vm.SelectedDevice = first;
            pending.SetResult(JsonSerializer.SerializeToElement(new { status = "deleted" })); Finish(); pending = null;
            Verify(!Find<TextBlock>("StatusText").Text.StartsWith("已删除文件"), "A late deletion response must not update another device.");
            Console.WriteLine("PASS file deletion: confirmation in five languages, exact target, refresh, failures, read-only roots, disconnects and stale actions.");
        }
        finally
        {
            pending?.TrySetCanceled(); panel.Dispose(); window.Close();
            LocalizationService.SetLanguage(language); ThemeService.Apply(theme); app.Shutdown();
        }
        return 0;
    }
}
