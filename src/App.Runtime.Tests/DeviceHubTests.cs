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
        backend.Hub = (action, args, guard, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!guard()) throw new OperationCanceledException();
            if (action == "launch") launches++;
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
        backend.Generation++;
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/apps/launch", launch)).StatusCode == HttpStatusCode.Conflict && launches == 1,
            "Old control generation launched an app.");
        await http.PostAsync("/api/v1/devices/a/control/release", null);
        http.DefaultRequestHeaders.Remove("X-Control-Session");
        Verify((await http.PostAsJsonAsync("/api/v1/devices/a/coredevice/start", new { audio = false })).IsSuccessStatusCode && management == "coredevice:False",
            "Native capture did not use explicit application management.");
        Verify((await http.PostAsync("/api/v1/devices/a/coredevice/stop", null)).IsSuccessStatusCode && management == "coredevice-stop:False",
            "Native stop did not reach application management.");
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
