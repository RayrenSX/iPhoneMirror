using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using IPhoneMirror.App.Automation;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Windows;
using IPhoneMirror.App.ViewModels;
using System.Windows;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static void Verify(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static int RunCoreDeviceReplayTest(string path)
    {
        async Task Test()
        {
            using var core = new NativeCore();
            var session = core.CreateExternalSession("replay-test-" + Guid.NewGuid().ToString("N"), false, 0);
            Verify(session.Success && session.Handle is not null, session.Message);
            using var handle = session.Handle!;
            await using (var feed = new CoreDeviceCaptureFeed(core, handle, null))
            {
                feed.StartReplay(path);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (feed.Frames < 18 && watch.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
                Verify(feed.Frames == 18, "Packaged RTP replay did not deliver all 18 frames: " + feed.Error);
                var status = core.GetDeviceSessionStatus(handle);
                Verify(status.Width == 256 && status.Height == 144 && status.VideoFrames == 18,
                    "Packaged media pipe did not update the native capture geometry/counters.");
                Verify(core.GetDeviceOutputFrame(handle, 0, 0, owned: true) is { Width: 256, Height: 144 },
                    "CoreDevice frame was unavailable to the existing screenshot/recording output path.");
            }
            core.StopDeviceSession(handle);
        }
        Test().GetAwaiter().GetResult();
        Console.WriteLine("Packaged bridge → RTP reassembly → patched decoder → private pipe → native capture → screenshot output passed (18 frames, rotation, no phone).");
        return 0;
    }

    private static int RunDeviceHubUiTest(string output)
    {
        System.IO.Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        var owner = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        try
        {
            var vm = (MainViewModel)KeyboardField(owner, "_viewModel");
            var hub = new DeviceHubWindow(vm) { Owner = owner };
            hub.Show(); AdvanceDispatcher(TimeSpan.FromMilliseconds(350));
            foreach (var culture in new[] { "zh-CN", "en-US", "zh-TW", "zh-HK", "ja-JP" })
            {
                IPhoneMirror.App.Localization.LocalizationService.SetLanguage(culture);
                hub.Width = 560; hub.Height = 580; hub.UpdateLayout();
                AdvanceDispatcher(TimeSpan.FromMilliseconds(100));
                Verify(!string.IsNullOrWhiteSpace(hub.Title) && !hub.Title.Contains("DeviceHubTitle"), "Device Hub localization is missing.");
                SaveWindowRender(hub, System.IO.Path.Combine(output, culture + ".png"));
                var pending = new Queue<DependencyObject>(); pending.Enqueue(hub);
                System.Windows.Controls.TabControl? tabs = null;
                while (pending.TryDequeue(out var node))
                {
                    if (node is System.Windows.Controls.TabControl candidate && candidate.Items.Count == 8) { tabs = candidate; break; }
                    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                        pending.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
                }
                Verify(tabs is not null, "Device Hub management pages are missing.");
                if (culture == "zh-CN")
                    for (var i = 0; i < tabs!.Items.Count; i++)
                    {
                        tabs.SelectedIndex = i; hub.UpdateLayout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                        SaveWindowRender(hub, System.IO.Path.Combine(output, "zh-CN-page-" + i + ".png"));
                    }
                if (culture == "zh-CN")
                {
                    var status = (System.Windows.Controls.TextBlock)KeyboardField(hub, "_status");
                    status.Text = string.Join(Environment.NewLine, Enumerable.Repeat("Device operation completed · diagnostic detail", 30));
                    foreach (var i in new[] { 0, 5, 7 })
                    {
                        tabs!.SelectedIndex = i; hub.UpdateLayout(); AdvanceDispatcher(TimeSpan.FromMilliseconds(80));
                        Verify(((System.Windows.Controls.TabItem)tabs.Items[i]).Content is System.Windows.Controls.ScrollViewer,
                            "Long status output can conceal Device Hub actions at the minimum window size.");
                        SaveWindowRender(hub, System.IO.Path.Combine(output, "zh-CN-status-page-" + i + ".png"));
                    }
                    status.Text = "";
                }
                tabs!.SelectedIndex = 0;
            }
            hub.Close();
        }
        finally { CloseWorkspaceTestWindow(owner); app.Shutdown(); }
        Console.WriteLine("Device Hub UI rendered at minimum width in five languages.");
        return 0;
    }
    private static async Task TestDeviceHubApiAsync()
    {
        var ownership = new AutomationOwnership();
        var backend = new AutomationTestBackend(ownership);
        var launches = 0;
        var hubCalls = new Dictionary<string, int>();
        string? uploadPath = null;
        backend.Hub = (action, args, guard, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!guard()) throw new OperationCanceledException();
            if (action == "launch") launches++;
            hubCalls[action] = hubCalls.GetValueOrDefault(action) + 1;
            if (action is "install_app" or "install_profile")
            {
                uploadPath = JsonSerializer.SerializeToElement(args).GetProperty("path").GetString();
                Verify(System.IO.File.Exists(uploadPath) && System.IO.File.ReadAllText(uploadPath!) == "test-package",
                    "Upload was not staged completely before installation.");
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { action, running = true, apps = new[] { new { bundleId = "com.apple.Preferences", name = "Settings" } } }));
        };
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var service = new AutomationService(backend, ownership, new DeviceInputService());
        var management = "";
        await using var host = new AutomationApiHost(service, (action, _, audio, _) =>
        { management = action + ":" + audio; return Task.FromResult<object>(new { started = true }); });
        await host.StartAsync(new AutomationSettings { Enabled = true, Port = port }, new string('A', 64));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        Verify((await http.GetAsync("/api/v1/devices/a/apps")).StatusCode == HttpStatusCode.Unauthorized, "Hub bypassed API authentication.");
        http.DefaultRequestHeaders.Add("X-API-Key", new string('A', 64));
        Verify((await http.GetAsync("/api/v1/devices/a/apps")).IsSuccessStatusCode, "App enumeration failed.");
        Verify((await http.GetAsync("/api/v1/devices/a/coredevice/status")).IsSuccessStatusCode, "Media status failed.");
        var catalog = await (await http.GetAsync("/api/v1/hub/actions")).Content.ReadFromJsonAsync<JsonElement>();
        var actions = catalog.GetProperty("actions");
        Verify(actions.EnumerateObject().Count() == 27 && !actions.TryGetProperty("install_app", out _) &&
            actions.GetProperty("get_settings").GetProperty("readOnly").GetBoolean(), "Public action catalog exposes private host paths or wrong policy.");
        foreach (var action in DeviceHubContract.Actions.Where(p => p.Value.Public && p.Key != "launch"))
        {
            var before = hubCalls.GetValueOrDefault(action.Key);
            object args = action.Key is "get_app_icon" or "uninstall_app" ? new { bundleId = "com.apple.Preferences" } : new { };
            var response = await http.PostAsJsonAsync("/api/v1/devices/a/hub/" + action.Key, args);
            Verify(action.Value.ReadOnly ? response.IsSuccessStatusCode : response.StatusCode == HttpStatusCode.Conflict,
                "Device Hub action bypassed its read/control admission: " + action.Key);
            Verify(hubCalls.GetValueOrDefault(action.Key) - before == (action.Value.ReadOnly ? 1 : 0),
                "An unleased mutation reached the bridge: " + action.Key);
        }
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/hub/install_app", new { path = "C:/private.ipa" })).StatusCode == HttpStatusCode.UnprocessableEntity,
            "Generic Hub API accepted a server-side install path.");
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/hub/erase", new { })).StatusCode == HttpStatusCode.UnprocessableEntity,
            "An unknown device action was admitted.");
        var launch = new { bundleId = "com.apple.Preferences" };
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/apps/launch", launch)).StatusCode == HttpStatusCode.Conflict && launches == 0,
            "App launch bypassed exclusive control.");
        var acquire = await http.PostAsJsonAsync("/api/v1/devices/a/control/acquire", new { });
        var token = (await acquire.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionToken").GetString();
        http.DefaultRequestHeaders.Add("X-Control-Session", token);
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/apps/launch", launch)).IsSuccessStatusCode && launches == 1,
            "Leased launch was not dispatched once.");
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/apps/launch", new { bundleId = "bad/id" })).StatusCode == HttpStatusCode.UnprocessableEntity,
            "An invalid app identity reached the bridge.");
        foreach (var action in DeviceHubContract.Actions.Where(p => p.Value.Public && !p.Value.ReadOnly && p.Key != "launch"))
        {
            object args = action.Key == "uninstall_app" ? new { bundleId = "com.apple.Preferences" } : new { };
            Verify((await http.PostAsJsonAsync("/api/v1/devices/a/hub/" + action.Key, args)).IsSuccessStatusCode &&
                hubCalls.GetValueOrDefault(action.Key) == 1, "A leased mutation was not dispatched exactly once: " + action.Key);
        }
        foreach (var (kind, fileName) in new[] { ("apps", "../../test.ipa"), ("profiles", "test.mobileprovision"), ("profiles", "test.mobileconfig") })
        {
            using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/a/" + kind + "/install")
            { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("test-package")) };
            upload.Content.Headers.ContentType = new("application/octet-stream");
            upload.Headers.Add("X-File-Name", fileName);
            Verify((await http.SendAsync(upload)).IsSuccessStatusCode, "Package upload/install failed.");
            Verify(uploadPath is not null && !System.IO.File.Exists(uploadPath) &&
                System.IO.Path.GetDirectoryName(uploadPath)!.EndsWith("HubUploads"), "Upload path escaped staging or was not removed after completion.");
        }
        using (var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/devices/a/apps/install")
        { Content = new ByteArrayContent([1, 2, 3]) })
        {
            invalid.Content.Headers.ContentType = new("application/octet-stream");
            invalid.Headers.Add("X-File-Name", "test.exe");
            var before = hubCalls.GetValueOrDefault("install_app");
            Verify((await http.SendAsync(invalid)).StatusCode == HttpStatusCode.UnprocessableEntity &&
                hubCalls.GetValueOrDefault("install_app") == before, "An invalid upload reached the bridge.");
        }
        backend.Generation++;
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/apps/launch", launch)).StatusCode == HttpStatusCode.Conflict && launches == 1,
            "Old control generation launched an app.");
        await http.PostAsync("/api/v1/devices/a/control/release", null);
        http.DefaultRequestHeaders.Remove("X-Control-Session");
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/coredevice/start", new { audio = false })).IsSuccessStatusCode && management == "coredevice:False",
            "Native capture did not use explicit application management.");
        Verify((await http.PostAsync("/api/v1/devices/a/coredevice/stop", null)).IsSuccessStatusCode && management == "coredevice-stop:False",
            "Native stop did not reach application management.");
        await host.StopAsync();
        await host.StartAsync(new AutomationSettings { Enabled = true, Port = port, Permissions = ["device.read"] }, new string('A', 64));
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/hub/get_settings", new { })).IsSuccessStatusCode,
            "Read-only credentials could not inspect phone settings.");
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/hub/set_setting", new { key = "voiceOver", value = true })).StatusCode == HttpStatusCode.Forbidden,
            "Read-only credentials changed a phone setting.");
    }

    private static async Task TestDeviceHubCorrelationAsync()
    {
        using var bytes = new System.IO.MemoryStream();
        using var writer = new System.IO.StreamWriter(bytes, leaveOpen: true);
        var bridge = new DirectUsbInputBridge();
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(bridge, "<SupportsDeviceHub>k__BackingField", true);
        var pending = bridge.DeviceHubAsync("apps", null, () => true, default);
        var packet = bytes.ToArray();
        var request = JsonDocument.Parse(packet.AsMemory(4, BitConverter.ToInt32(packet))).RootElement;
        var id = request.GetProperty("requestId").GetString();
        var generation = request.GetProperty("generation").GetInt64();
        void Reply(long gen) => KeyboardCall(bridge, "CompleteDeviceHub",
            JsonSerializer.SerializeToElement(new { requestId = id, generation = gen, success = true, result = new { apps = Array.Empty<object>() } }));
        Reply(generation + 1);
        Verify(!pending.IsCompleted, "An obsolete bridge generation completed a Hub request.");
        Reply(generation);
        Verify((await pending).GetProperty("apps").GetArrayLength() == 0, "Hub lost correlated response data.");
        var disconnected = bridge.DeviceHubAsync("media_status", null, () => true, default);
        KeyboardCall(bridge, "CancelDeviceHub");
        try { await disconnected; throw new InvalidOperationException("Hub request survived disconnect."); }
        catch (AutomationException error) when (error.Code == "SESSION_CHANGED") { }
    }
}
