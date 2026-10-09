using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Automation;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Runtime.Tests;

internal static partial class Program
{
    private static int RunAutomationSettingsTests(string output)
    {
        System.IO.Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        typeof(App).GetProperty("IsUiPreviewMode", KeyboardTestMembers)!.SetValue(app, true);
        app.InitializeComponent();
        var window = CreateWorkspaceTestWindow(app, includeNativePreview: false);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
        IPhoneMirror.App.Windows.DeveloperToolsWindow? developer = null;
        var keyPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iphoneMirror-ui-key-" + Guid.NewGuid().ToString("N"));
        var settingsPath = keyPath + ".json";
        SetKeyboardField(window, "_automationKeys", new AutomationKeyStore(keyPath));
        app.UpdateSettings.AutomationApi = new AutomationSettings();
        void Verify(bool passed, string message)
        { if (!passed) throw new InvalidOperationException(message); }
        void WaitUntil(Func<bool> ready)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!ready() && timer.Elapsed < TimeSpan.FromSeconds(10)) AdvanceDispatcher(TimeSpan.FromMilliseconds(20));
            Verify(ready(), "API settings operation timed out.");
        }
        void OpenDeveloper()
        {
            for (var i = 0; i < 5; i++) KeyboardCall(window, "OnVersionClick", window, new System.Windows.RoutedEventArgs());
            developer = (IPhoneMirror.App.Windows.DeveloperToolsWindow)KeyboardField(window, "_developerToolsWindow");
            Verify(developer.IsVisible, "Version click entry did not open developer tools.");
        }
        try
        {
            Verify(window.FindName("ApiEnabled") is null, "API controls remain in ordinary settings.");
            OpenDeveloper();
            foreach (var language in new[] { "zh-CN", "zh-HK", "zh-TW", "en-US" })
            {
                DisplayLanguage(language);
                AdvanceDispatcher(TimeSpan.FromMilliseconds(120));
                var status = (System.Windows.Controls.TextBlock)developer!.FindName("ApiStatus");
                Verify(status.Text == DisplayL("AutomationStateStopped"), "API status did not follow UI language.");
                Verify(KeyboardField(window, "_automationHost") is null, "Opening developer tools started an API host.");
                AssertVisibleButtonsFit(developer);
                SaveWindowRender(developer, System.IO.Path.Combine(output, "automation-settings-" + language + ".png"));
            }
            var portBox = (System.Windows.Controls.TextBox)developer!.FindName("ApiPort");
            var enabled = (System.Windows.Controls.CheckBox)developer.FindName("ApiEnabled");
            portBox.Text = "80";
            KeyboardCall(developer, "OnAutomationApply", developer, new System.Windows.RoutedEventArgs());
            Verify(((System.Windows.Controls.TextBlock)developer.FindName("ApiStatus")).Text == DisplayL("AutomationInvalidPort"), "Invalid port was accepted.");
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            portBox.Text = port.ToString(); enabled.IsChecked = true;
            KeyboardCall(developer, "OnAutomationApply", developer, new System.Windows.RoutedEventArgs());
            WaitUntil(() => window.AutomationRunning && app.UpdateSettings.AutomationApi.Enabled);
            var key = new AutomationKeyStore(keyPath).LoadOrCreate();
            using var http = new HttpClient { BaseAddress = new Uri(window.AutomationAddress) };
            Verify(http.GetAsync("/api/docs").GetAwaiter().GetResult().IsSuccessStatusCode, "UI enable did not serve docs.");
            Verify(http.GetAsync("/api/v1/devices").GetAwaiter().GetResult().StatusCode == HttpStatusCode.Unauthorized, "UI-started host bypassed authentication.");
            developer.Close(); OpenDeveloper();
            Verify(window.AutomationRunning && ((System.Windows.Controls.CheckBox)developer!.FindName("ApiEnabled")).IsChecked == true,
                "Closing/reopening developer tools lost the running server or configuration.");
            KeyboardCall(developer, "OnAutomationRegenerate", developer, new System.Windows.RoutedEventArgs());
            WaitUntil(() => window.AutomationRunning && new AutomationKeyStore(keyPath).LoadOrCreate() != key);
            http.DefaultRequestHeaders.Add("X-API-Key", key);
            Verify(http.GetAsync("/api/v1/unknown").GetAwaiter().GetResult().StatusCode == HttpStatusCode.Unauthorized, "Old key survived rotation from UI.");
            http.DefaultRequestHeaders.Remove("X-API-Key");
            http.DefaultRequestHeaders.Add("X-API-Key", new AutomationKeyStore(keyPath).LoadOrCreate());
            Verify(http.GetAsync("/api/v1/unknown").GetAwaiter().GetResult().StatusCode == HttpStatusCode.NotFound, "New key did not authenticate.");
            ((System.Windows.Controls.CheckBox)developer.FindName("ApiEnabled")).IsChecked = false;
            KeyboardCall(developer, "OnAutomationApply", developer, new System.Windows.RoutedEventArgs());
            WaitUntil(() => !window.AutomationRunning && !app.UpdateSettings.AutomationApi.Enabled);
            var rebound = new TcpListener(IPAddress.Loopback, port); rebound.Start(); rebound.Stop();
            Verify(((System.Windows.Controls.TextBlock)developer.FindName("ApiStatus")).Text == DisplayL("AutomationStateStopped"), "Stop status is stale.");

            // Exercise real persistence with an isolated file, then inject a
            // filesystem failure. Never read or replace the user's settings.
            var previousStore = KeyboardField(app, "_settingsStore");
            try
            {
                var store = new IPhoneMirror.App.Updater.UpdateSettingsStore(settingsPath);
                SetKeyboardField(app, "_settingsStore", store);
                app.IsUiPreviewMode = false;
                var configuration = window.AutomationConfiguration;
                configuration.Enabled = true;
                AwaitMapping(window.ConfigureAutomationAsync(configuration, save: true));
                Verify(window.AutomationRunning && store.Load().AutomationApi.Enabled && store.Load().AutomationApi.Port == port,
                    "API configuration did not persist.");
                configuration.Enabled = false;
                AwaitMapping(window.ConfigureAutomationAsync(configuration, save: true));
                Verify(!store.Load().AutomationApi.Enabled, "Disabled setting did not persist.");
                SetKeyboardField(app, "_settingsStore", new IPhoneMirror.App.Updater.UpdateSettingsStore(System.IO.Path.Combine(keyPath, "settings.json")));
                configuration.Enabled = true;
                AwaitMapping(window.ConfigureAutomationAsync(configuration, save: true));
                Verify(!window.AutomationRunning && window.AutomationStatusKey == "AutomationSaveFailed" && !app.UpdateSettings.AutomationApi.Enabled,
                    "Save failure left a running API or changed persisted settings.");
                rebound.Start(); rebound.Stop();
            }
            finally
            {
                app.IsUiPreviewMode = true;
                SetKeyboardField(app, "_settingsStore", previousStore);
            }
        }
        finally
        {
            developer?.Close();
            AwaitMapping((Task)KeyboardCall(window, "ShutdownAutomationAsync")!);
            CloseWorkspaceTestWindow(window); app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (System.IO.File.Exists(keyPath)) System.IO.File.Delete(keyPath);
            if (System.IO.File.Exists(settingsPath)) System.IO.File.Delete(settingsPath);
        }
        Console.WriteLine("Developer API settings: entry, four languages, validation, enable/auth, reopen, rotate, stop/rebind, persistence and save-failure cleanup passed.");
        return 0;
    }

    private static int RunAutomationApiTests()
    {
        RunAutomationApiTestsAsync().GetAwaiter().GetResult();
        Console.WriteLine("Automation API integration tests passed.");
        return 0;
    }
    private static async Task RunAutomationApiTestsAsync()
    {
        _ = AutomationOpenApi.Document();
        await TestAutomationTransportAdmissionAsync();
        await TestAutomationClipboardCorrelationAsync();
        await TestAutomationLeaseRecoveryAsync();
        await TestAutomationScreenshotSharingAsync();
        TestAutomationKeyPersistence();
        var owner = new AutomationOwnership();
        var backend = new AutomationTestBackend(owner);
        var service = new AutomationService(backend, owner, new DeviceInputService());
        await using var host = new AutomationApiHost(service);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var settings = new AutomationSettings { Enabled = true, Port = port, RequestBurst = 500, RequestsPerSecond = 500, ScreenshotsPerSecond = 30 };
        var key = new string('A', 64);
        await host.StartAsync(settings, key);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        void Expect(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task<JsonElement> Json(HttpResponseMessage response) => (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync())).RootElement.Clone();
        async Task Error(HttpResponseMessage response, int status, string code)
        {
            Expect((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Expect((await Json(response)).GetProperty("error").GetProperty("code").GetString() == code, "Wrong error code.");
        }
        await Error(await http.GetAsync("/api/v1/devices"), 401, "UNAUTHORIZED");
        http.DefaultRequestHeaders.Add("X-API-Key", key);
        Expect((await Json(await http.GetAsync("/api/v1/devices"))).GetProperty("devices").GetArrayLength() == 3, "Device list lost sources.");
        Expect((await Json(await http.GetAsync("/api/v1/devices/a"))).GetProperty("id").GetString() == "a", "Wrong device detail.");
        Expect((await Json(await http.GetAsync("/api/v1/devices/a/status"))).GetProperty("capabilities").GetProperty("tap").GetBoolean(), "Capabilities missing.");
        await Error(await http.GetAsync("/api/v1/devices/missing"), 404, "DEVICE_NOT_FOUND");
        backend.Online = false;
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/control/acquire", new { }), 503, "DEVICE_NOT_CONNECTED");
        backend.Online = true;
        backend.HumanBusy = true;
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/control/acquire", new { }), 409, "CONTROL_LOCKED");
        backend.HumanBusy = false;
        var acquired = await Json(await http.PostAsJsonAsync("/api/v1/devices/a/control/acquire", new { }));
        var session = acquired.GetProperty("sessionToken").GetString()!;
        Expect(!owner.HumanAllowed("phone-a"), "UI admission remained open during API lease.");
        try { using var admission = owner.EnterInput("phone-a"); throw new InvalidOperationException("UI transport bypassed lease."); }
        catch (OperationCanceledException) { }
        // Alias and second client must compete for the same physical lock.
        await Error(await http.PostAsJsonAsync("/api/v1/devices/alias/control/acquire", new { }), 409, "CONTROL_LOCKED");
        Expect((await http.PostAsJsonAsync("/api/v1/devices/b/control/acquire", new { })).IsSuccessStatusCode, "Another device was blocked.");
        http.DefaultRequestHeaders.Add("X-Control-Session", session);
        foreach (var (action, body) in new (string, object)[]
        {
            ("tap", new { x = 20, y = 30 }), ("long-press", new { x = 20, y = 30, duration = .05 }),
            ("swipe", new { x1 = 20, y1 = 80, x2 = 20, y2 = 10, duration = .05 }),
            ("touch-path", new { points = new[] { new { x = 10, y = 20 }, new { x = 20, y = 30 }, new { x = 30, y = 10 } }, duration = .05 }),
            ("key", new { key = "ENTER" }), ("text", new { text = "Hello 中文 😀" })
        })
        {
            var response = await http.PostAsJsonAsync("/api/v1/devices/a/input/" + action, body);
            Expect(response.StatusCode == HttpStatusCode.Accepted, action + ": " + await response.Content.ReadAsStringAsync());
        }
        Expect(backend.Events.Count(x => x == "down") == backend.Events.Count(x => x == "up"), "Gestures left held contacts.");
        Expect(backend.Events.Contains("move") && backend.Events.Contains("key:40"), "Existing routes were not used.");
        var held = await Json(await http.PostAsJsonAsync("/api/v1/devices/a/input/touch-down", new { x = 10, y = 20 }));
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/input/tap", new { x = 10, y = 20 }), 409, "CONTROL_LOCKED");
        Expect((await http.PostAsJsonAsync("/api/v1/devices/a/input/touch-up", new { touchId = held.GetProperty("touchId").GetString() })).StatusCode == HttpStatusCode.Accepted, "Touch release failed.");
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/input/tap", new { x = 100, y = 20 }), 422, "INVALID_COORDINATE");
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/input/long-press", new { x = 10, y = 20, duration = 0 }), 422, "INVALID_DURATION");
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/input/touch-path", new { points = new[] { new { x = 10, y = 20 } }, duration = .1 }), 400, "INVALID_REQUEST");
        await Error(await http.PostAsync("/api/v1/devices/a/input/touch-path", new StringContent("{\"points\":[null,{}],\"duration\":0.1}", Encoding.UTF8, "application/json")), 422, "INVALID_COORDINATE");
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/input/text", new { text = new string('x', 65537) }), 400, "INVALID_REQUEST");
        await Error(await http.PostAsync("/api/v1/devices/a/input/tap", new StringContent("{broken", Encoding.UTF8, "application/json")), 400, "INVALID_REQUEST");
        var before = backend.Events.Count;
        Expect((await http.PutAsJsonAsync("/api/v1/devices/a/clipboard", new { text = "clipboard-only" })).StatusCode == HttpStatusCode.NoContent, "Clipboard write failed.");
        Expect(backend.Events.Count == before && backend.ClipboardText == "clipboard-only", "Clipboard write generated input.");
        Expect((await Json(await http.GetAsync("/api/v1/devices/a/clipboard"))).GetProperty("text").GetString() == "clipboard-only", "Clipboard read wrong.");
        var png = await http.GetAsync("/api/v1/devices/a/screenshot");
        var bytes = await png.Content.ReadAsByteArrayAsync();
        Expect(png.Content.Headers.ContentType?.MediaType == "image/png" && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Screenshot is not PNG.");
        backend.Supported = false;
        await Error(await http.GetAsync("/api/v1/devices/a/clipboard"), 422, "CAPABILITY_NOT_SUPPORTED");
        backend.Supported = true;
        var spec = await Json(await http.GetAsync("/api/v1/openapi.json"));
        Expect(spec.GetProperty("openapi").GetString() == "3.1.0" && spec.GetProperty("paths").EnumerateObject().Count() == 15, "Incomplete OpenAPI document.");
        Expect((await http.GetStringAsync("/api/docs")).Contains("capture"), "Local docs missing.");
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/devices"))
        { crossOrigin.Headers.Add("Origin", "https://example.org"); await Error(await http.SendAsync(crossOrigin), 403, "FORBIDDEN"); }
        // Revocation cancels a running gesture and its finally must release.
        var longPress = http.PostAsJsonAsync("/api/v1/devices/a/input/long-press", new { x = 20, y = 20, duration = 5 });
        await Task.Delay(100);
        var release = await http.PostAsync("/api/v1/devices/a/control/release", null);
        Expect(release.IsSuccessStatusCode, "Release did not drain input.");
        Expect(!(await longPress).IsSuccessStatusCode && owner.HumanAllowed("phone-a"), "Cancelled lease kept ownership or reported success.");
        Expect(backend.Events.Count(x => x == "down") == backend.Events.Count(x => x == "up"), "Cancellation lost touch-up.");
        await host.StopAsync();
        settings.Permissions = ["device.read"];
        await host.StartAsync(settings, key);
        await Error(await http.PostAsJsonAsync("/api/v1/devices/a/control/acquire", new { }), 403, "FORBIDDEN");
        await host.StopAsync();
        settings.Permissions = ["device.read"]; settings.RequestBurst = 1; settings.RequestsPerSecond = 1;
        await host.StartAsync(settings, key);
        Expect((await http.GetAsync("/api/v1/devices")).IsSuccessStatusCode, "Rate bucket did not initialize.");
        await Error(await http.GetAsync("/api/v1/devices"), 429, "RATE_LIMITED");
        await host.StopAsync();
        var rebound = new TcpListener(IPAddress.Loopback, port); rebound.Start(); rebound.Stop();
        settings.RequestBurst = 100;
        var rotatedKey = new string('B', 64);
        await host.StartAsync(settings, rotatedKey);
        await Error(await http.GetAsync("/api/v1/devices"), 401, "UNAUTHORIZED");
        http.DefaultRequestHeaders.Remove("X-API-Key");
        http.DefaultRequestHeaders.Add("X-API-Key", rotatedKey);
        Expect((await http.GetAsync("/api/v1/devices")).IsSuccessStatusCode, "Rotated key was rejected.");
        await host.StopAsync();
        rebound.Start();
        try
        {
            try { await host.StartAsync(settings, key); throw new InvalidOperationException("Occupied port was accepted."); }
            catch (System.IO.IOException) { }
            Expect(host.State == "Failed", "Bind failure did not update host state.");
        }
        finally { rebound.Stop(); }
        await host.StartAsync(settings, key);
        await host.DisposeAsync();
        try { await host.StartAsync(settings, key); throw new InvalidOperationException("Disposed host restarted."); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task TestAutomationLeaseRecoveryAsync()
    {
        var owner = new AutomationOwnership();
        var backend = new AutomationTestBackend(owner);
        await using var service = new AutomationService(backend, owner, new DeviceInputService());
        service.Start();
        async Task WaitReleased()
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!owner.HumanAllowed("phone-a") && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            if (!owner.HumanAllowed("phone-a")) throw new InvalidOperationException("Lease cleanup stalled.");
        }
        async Task<AutomationOwnership.Lease> Hold()
        {
            await service.AcquireAsync("a", "client", null, 30, default);
            var lease = owner.Snapshot().Single();
            await service.InputAsync("a", "client", lease.Token, "touch-down", new() { X = 10, Y = 20 }, default);
            return lease;
        }
        var expired = await Hold();
        expired.Deadline = 0;
        await WaitReleased();
        if (backend.Events.Count(x => x == "up") != 1) throw new InvalidOperationException("Expiry lost touch-up.");
        await Hold();
        backend.Geometry++;
        await WaitReleased();
        if (backend.Events.Count(x => x == "up") != 2) throw new InvalidOperationException("Geometry change lost cleanup on the same session.");
        await Hold();
        backend.Generation++;
        await WaitReleased();
        if (backend.Events.Count(x => x == "up") != 2) throw new InvalidOperationException("Old touch-up reached a replacement session.");

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            try { await service.AcquireAsync("a", "client-" + i, null, 30, default); return true; }
            catch (AutomationException e) when (e.Code == "CONTROL_LOCKED") { return false; }
        }));
        if (attempts.Count(x => x) != 1) throw new InvalidOperationException("Concurrent clients acquired the same phone.");
        var active = owner.Snapshot().Single();
        try
        {
            await service.InputAsync("a", active.Principal, "wrong", "tap", new() { X = 1, Y = 1 }, default);
            throw new InvalidOperationException("Wrong lease token was accepted.");
        }
        catch (AutomationException e) when (e.Code == "CONTROL_LOCKED") { }
        using var cancel = new CancellationTokenSource();
        var gesture = service.InputAsync("a", active.Principal, active.Token, "long-press", new() { X = 10, Y = 20, Duration = 5 }, cancel.Token);
        await Task.Delay(50);
        cancel.Cancel();
        try { await gesture; throw new InvalidOperationException("Cancelled input reported success."); }
        catch (OperationCanceledException) { }
        if (backend.Events.Last() != "up") throw new InvalidOperationException("Request cancellation left a held contact.");
        await service.RevokeAllAsync();
        await service.AcquireAsync("a", "client", null, 30, default);
        active = owner.Snapshot().Single();
        backend.FailUp = true;
        try
        {
            await service.InputAsync("a", "client", active.Token, "tap", new() { X = 10, Y = 20 }, default);
            throw new InvalidOperationException("Failed touch-up reported success.");
        }
        catch (AutomationException e) when (e.Code == "INPUT_FAILED") { }
        if (active.Touch is null || owner.HumanAllowed("phone-a")) throw new InvalidOperationException("Failed touch-up lost cleanup ownership.");
        backend.FailUp = false;
        await service.RevokeAllAsync();
        if (!owner.HumanAllowed("phone-a") || active.Touch is not null) throw new InvalidOperationException("Touch cleanup did not recover.");
        var reservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeReserve = () => reservation.Task;
        var delayedAcquire = service.AcquireAsync("a", "client", null, 30, default);
        service.Suspend();
        await service.RevokeAllAsync();
        reservation.SetResult();
        try { await delayedAcquire; throw new InvalidOperationException("Queued acquire survived server stop."); }
        catch (AutomationException e) when (e.Code == "CONTROL_NOT_AVAILABLE") { }
        if (owner.Snapshot().Length != 0) throw new InvalidOperationException("Server stop leaked a late lease.");
    }

    private static async Task TestAutomationScreenshotSharingAsync()
    {
        var owner = new AutomationOwnership();
        var backend = new AutomationTestBackend(owner);
        await using var service = new AutomationService(backend, owner, new DeviceInputService());
        var a = new TaskCompletionSource<VideoFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<VideoFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        backend.CaptureFrame = (id, _) => { Interlocked.Increment(ref count); return id == "b" ? b.Task : a.Task; };
        using var cancellation = new CancellationTokenSource();
        var abandoned = service.ScreenshotAsync("a", cancellation.Token);
        var shared = service.ScreenshotAsync("a", default);
        var isolated = service.ScreenshotAsync("b", default);
        cancellation.Cancel();
        try { await abandoned; throw new InvalidOperationException("Cancelled screenshot waiter completed."); }
        catch (OperationCanceledException) { }
        a.SetResult(new VideoFrame(1, 1, 4, 1, [0, 0, 255, 255]));
        b.SetResult(new VideoFrame(1, 1, 4, 1, [255, 0, 0, 255]));
        var images = await Task.WhenAll(shared, isolated);
        if (count != 2 || images[0].SequenceEqual(images[1])) throw new InvalidOperationException("Screenshot work was duplicated or devices shared pixels.");
        await Task.Delay(30);
        await service.ScreenshotAsync("a", default);
        if (count != 3) throw new InvalidOperationException("Completed screenshot was never evicted.");
    }

    private static void TestAutomationKeyPersistence()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iphoneMirror-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AutomationKeyStore(path);
            var key = store.LoadOrCreate();
            if (key.Length != 64 || new AutomationKeyStore(path).LoadOrCreate() != key ||
                Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(path)).Contains(key))
                throw new InvalidOperationException("API key was not persisted as DPAPI ciphertext.");
            var rotated = store.Regenerate();
            if (rotated == key || store.LoadOrCreate() != rotated) throw new InvalidOperationException("Key rotation failed.");
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    private static async Task TestAutomationTransportAdmissionAsync()
    {
        foreach (var transport in new[] { "usb", "wireless" })
        {
            var ownership = new AutomationOwnership();
            var backend = new AutomationTestBackend(ownership);
            var target = await backend.ResolveAsync("a", default);
            using var bytes = new System.IO.MemoryStream();
            using var writer = new System.IO.StreamWriter(bytes, leaveOpen: true);
            var bridge = new DirectUsbInputBridge { InputAdmission = () => ownership.EnterInput("phone-a") };
            SetKeyboardField(bridge, "_stdin", writer);
            SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
            var sendLock = (SemaphoreSlim)KeyboardField(bridge, "_sendLock");
            await sendLock.WaitAsync();
            var queuedUi = bridge.SendKeyboardAsync([4]);
            var oldHuman = ownership.CaptureHumanGuard("phone-a");
            var lease = ownership.Acquire(target, "client", null, 30);
            sendLock.Release();
            try { await queuedUi; throw new InvalidOperationException(transport + ": queued UI input bypassed acquire."); }
            catch (OperationCanceledException) { }
            if (bytes.Length != 0) throw new InvalidOperationException("Rejected input wrote an IPC frame.");
            using (ownership.Use(lease)) await bridge.SendKeyboardAsync([0x28]);
            if (bytes.Length == 0) throw new InvalidOperationException("Lease owner did not reach the existing bridge.");
            ownership.BeginRelease(lease);
            using (ownership.Use(lease)) await bridge.SendKeyboardAsync([], releaseAll: true);
            ownership.FinishRelease(lease);
            if (oldHuman()) throw new InvalidOperationException("Old UI generation revived after release.");
            using var pending = ownership.EnterInput("phone-a");
            try { ownership.Acquire(target, "next", null, 30); throw new InvalidOperationException("Acquire overlapped an admitted write."); }
            catch (AutomationException error) when (error.Code == "CONTROL_LOCKED") { }
        }
    }

    private static async Task TestAutomationClipboardCorrelationAsync()
    {
        using var bytes = new System.IO.MemoryStream();
        using var writer = new System.IO.StreamWriter(bytes, leaveOpen: true);
        var bridge = new DirectUsbInputBridge();
        SetKeyboardField(bridge, "_stdin", writer);
        SetKeyboardField(bridge, "<IsReady>k__BackingField", true);
        SetKeyboardField(bridge, "<SupportsAutomationClipboard>k__BackingField", true);
        JsonElement Frame(int offset)
        {
            var packet = bytes.ToArray();
            var size = BitConverter.ToInt32(packet, offset);
            return JsonDocument.Parse(packet.AsMemory(offset + 4, size)).RootElement.Clone();
        }
        void Reply(string id, long generation, bool success, string? text = null)
        {
            var response = JsonSerializer.SerializeToElement(new { requestId = id, generation, success, text });
            KeyboardCall(bridge, "CompleteAutomationClipboard", response);
        }
        var write = bridge.AutomationClipboardAsync("write_clipboard", "private value", () => true, default);
        var request = Frame(0);
        var id = request.GetProperty("requestId").GetString()!;
        var generation = request.GetProperty("generation").GetInt64();
        Reply("unknown", generation, true);
        Reply(id, generation + 1, true);
        if (write.IsCompleted || request.GetProperty("kind").GetString() != "write_clipboard")
            throw new InvalidOperationException("Clipboard correlation accepted an obsolete response.");
        Reply(id, generation, true);
        await write;
        var offset = (int)bytes.Length;
        using var cancellation = new CancellationTokenSource();
        var paste = bridge.AutomationClipboardAsync("paste_text", "private paste", () => true, cancellation.Token);
        request = Frame(offset);
        id = request.GetProperty("requestId").GetString()!;
        offset = (int)bytes.Length;
        cancellation.Cancel();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (bytes.Length == offset && timer.Elapsed < TimeSpan.FromSeconds(1)) await Task.Delay(5);
        if (bytes.Length == offset || Frame(offset).GetProperty("kind").GetString() != "cancel_clipboard")
            throw new InvalidOperationException("Clipboard cancellation was not sent through the existing bridge.");
        Reply(id, generation, false);
        try { await paste; throw new InvalidOperationException("Cancelled paste reported success."); }
        catch (OperationCanceledException) { }
        if (!bridge.IsReady) throw new InvalidOperationException("Acknowledged clipboard cleanup retired a healthy session.");
    }

    private sealed class AutomationTestBackend(AutomationOwnership ownership) : IAutomationBackend
    {
        internal bool Online = true, Supported = true, HumanBusy, FailUp;
        internal int Geometry = 1, Generation = 1;
        internal Func<string, CancellationToken, Task<VideoFrame?>>? CaptureFrame;
        internal Func<Task>? BeforeReserve;
        internal string ClipboardText = "initial";
        internal readonly ConcurrentQueue<string> Events = new();
        private readonly object _inputSync = new();
        private readonly object _keyboardSession = new();
        public async Task<IReadOnlyList<AutomationDevice>> GetDevicesAsync(CancellationToken cancellation) =>
            await Task.WhenAll(new[] { "a", "alias", "b" }.Select(async id => (await ResolveAsync(id, cancellation)).Device));
        public Task<AutomationTarget> ResolveAsync(string id, CancellationToken cancellation)
        {
            if (id is not ("a" or "alias" or "b")) throw new AutomationException("DEVICE_NOT_FOUND", "Missing device.", 404);
            var physical = id == "b" ? "phone-b" : "phone-a";
            var generation = Generation;
            var geometry = Geometry;
            bool SessionCurrent() => Online && generation == Generation;
            Task Send(string action, double x, double y, CancellationToken token)
            {
                using var admission = ownership.EnterInput(physical);
                if (!SessionCurrent()) throw new OperationCanceledException();
                if (action == "up" && FailUp) throw new System.IO.IOException("simulated release failure");
                token.ThrowIfCancellationRequested(); Events.Enqueue(action); return Task.CompletedTask;
            }
            var keyboard = new DirectKeyboardRoute(id, "WiredDirect", _keyboardSession, 1, () => Online,
                (modifiers, usages, guard) =>
                {
                    using var admission = ownership.EnterInput(physical);
                    if (guard?.Invoke() != false) Events.Enqueue("key:" + string.Join(',', usages));
                    return Task.CompletedTask;
                });
            var device = new AutomationDevice(id, "Test phone", "iPhone", "26", "usb", Online, true, null,
                "WiredDirect", true, new(100, 200, geometry + ":" + generation), new(Supported, Supported, Supported, Supported,
                    Supported, Supported, Supported, Supported, Supported, Supported, Supported));
            return Task.FromResult(new AutomationTarget(device, id, physical, () => SessionCurrent() && geometry == Geometry, p => p,
                guard => new(id, guard, Send, (x, y) => (x, y)), keyboard,
                (kind, text, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (kind != "read_clipboard") ClipboardText = text!;
                    if (kind == "paste_text") Events.Enqueue("paste");
                    return Task.FromResult<string?>(ClipboardText);
                }, token => CaptureFrame?.Invoke(id, token) ?? Task.FromResult<VideoFrame?>(new VideoFrame(2, 2, 8, 1, new byte[16])))
                { SessionCurrent = SessionCurrent });
        }
        public Task<bool> IsHumanInputBusyAsync(string identity, CancellationToken cancellation) => Task.FromResult(HumanBusy);
        public async Task ReserveAsync(string identity, Action reserve, CancellationToken cancellation)
        {
            if (BeforeReserve is not null) await BeforeReserve();
            lock (_inputSync)
            {
                if (HumanBusy) throw new AutomationException("CONTROL_LOCKED", "Human input is active.");
                reserve();
            }
        }
    }
}
