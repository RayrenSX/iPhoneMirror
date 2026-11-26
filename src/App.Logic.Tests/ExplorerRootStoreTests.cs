using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using IPhoneMirror.App.Services.ExplorerMapping;

internal static class ExplorerRootStoreTests
{
    internal static async Task Run()
    {
        var calls = new List<(string Action, JsonElement Args)>();
        var deny = false;
        Task<JsonElement> Request(string action, object? arguments, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var args = JsonSerializer.SerializeToElement(arguments);
            calls.Add((action, args));
            if (action == "explorer_roots")
            {
                var offset = args.GetProperty("offset").GetInt32();
                object[] roots = offset == 0 ?
                [
                    new { root = "media" }, new { root = "crashes" },
                    new { root = "documents", bundleId = "com.test.documents", name = "Files" },
                    new { root = "documents", bundleId = "com.test.duplicate", name = "files" },
                    new { root = "container", bundleId = "com.test.reserved", name = "CON" },
                    new { root = "documents", bundleId = "com.test.invalid", name = "A/B:*?" },
                    new { root = "coredevice", domain = "temporary", identifier = "" },
                    new { root = "coredevice", domain = "appGroupDataContainer", identifier = "group.test", name = "Group" },
                ] : [new { root = "container", bundleId = "com.test.container", name = "Full app" }];
                return Task.FromResult(JsonSerializer.SerializeToElement(new { roots, total = 5, nextOffset = offset == 0 ? 4 : (int?)null }));
            }
            var path = args.GetProperty("path").GetString()!;
            var root = args.GetProperty("root").GetString();
            var readOnly = path is "locked.bin" or "protected";
            if (action == "stat_file") return Task.FromResult(Metadata(path, path is "" or "nested" or "protected", readOnly));
            if (action == "list_dir")
            {
                var names = root == "crashes" ? new[] { "report.ips" } : new[] { "open.bin", "locked.bin" };
                return Task.FromResult(JsonSerializer.SerializeToElement(new { path, total = names.Length, nextOffset = (int?)null,
                    entries = names.Select(name => Metadata(path.Length == 0 ? name : path + "/" + name, false, name == "locked.bin")) }));
            }
            if (action == "read_file") return Task.FromResult(JsonSerializer.SerializeToElement(new
            { path, offset = 0, size = 3, modified = 1, data = Convert.ToBase64String("abc"u8), eof = true }));
            if (deny) throw new IPhoneMirror.App.Services.Automation.AutomationException("FILE_ACCESS_DENIED", "denied");
            return Task.FromResult(Metadata(action == "move_file" ? args.GetProperty("destination").GetString()! : path, false));
        }
        var store = await DeviceHubExplorerRootStore.CreateAsync(Request, "Mapped folders", "媒体文件", "崩溃日志", "临时文件", "系统崩溃日志", default);
        Require(calls.Count == 2 && calls.All(call => call.Action == "explorer_roots"), "Discovery did not page or opened a file service prematurely.");
        var folders = await store.ListAsync("", default);
        Require(folders.Count == 9 && folders.Select(folder => folder.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 9 &&
            folders.Any(folder => folder.Name == "_CON") && folders.Any(folder => folder.Name == "A_B___") &&
            folders.Any(folder => folder.Name == "files (2)"), "Accessible folders were lost or Windows names collide.");
        Require(calls.Count == 2 && (await store.StatAsync("", default))!.Name == "Mapped folders", "Listing the virtual root opened a phone service.");

        var file = (await store.StatAsync("Files/nested/open.bin", default))!;
        var args = calls.Last().Args;
        Require(file.Path == "Files/nested/open.bin" && args.GetProperty("path").GetString() == "nested/open.bin" &&
            args.GetProperty("root").GetString() == "documents" && args.GetProperty("bundleId").GetString() == "com.test.documents",
            "A mapped prefix reached the phone or the application identity was lost.");
        Require(Encoding.UTF8.GetString((await store.ReadAsync(file, 0, 3, default)).Span) == "abc", "Scoped reads lost their original snapshot.");
        await store.MakeDirectoryAsync("媒体文件/new", default);
        Require(calls.Last().Args.GetProperty("root").GetString() == "media", "Media writes routed into an app.");
        await store.MoveAsync(file, "Files/nested/renamed.bin", null, default);
        Require(calls.Last().Args.GetProperty("destination").GetString() == "nested/renamed.bin", "A rename contained a virtual prefix.");
        var before = calls.Count;
        await Expect<UnauthorizedAccessException>(() => store.MoveAsync(file, "Full app/copied.bin", null, default));
        await Expect<UnauthorizedAccessException>(() => store.DeleteAsync(folders[0], default));
        await Expect<DirectoryNotFoundException>(() => store.MakeDirectoryAsync("other", default));
        await Expect<UnauthorizedAccessException>(() => store.StatAsync("Files/../媒体文件/open.bin", default));
        await Expect<UnauthorizedAccessException>(() => store.ReadAsync(file with { Path = "Full app/open.bin" }, 0, 3, default));
        Require(calls.Count == before, "A service boundary or forged snapshot reached the bridge.");

        var full = await store.ListAsync("Full app", default);
        Require(calls.Last().Args.GetProperty("root").GetString() == "container" &&
            calls.Last().Args.GetProperty("bundleId").GetString() == "com.test.container", "Full application containers used Documents instead.");
        var locked = full.Single(entry => entry.Name == "locked.bin");
        Require(locked.IsReadOnly && (locked.WindowsProperties!.Win32Attributes & 1) != 0, "A phone file's read-only flag was lost.");
        await Expect<UnauthorizedAccessException>(() => store.DeleteAsync(locked, default));
        await Expect<UnauthorizedAccessException>(() => store.UpdatePropertiesAsync(locked, new(Win32Attributes: 32), default));
        await store.StatAsync("Full app/protected", default);
        before = calls.Count;
        await Expect<UnauthorizedAccessException>(() => store.MakeDirectoryAsync("Full app/protected/new", default));
        Require(calls.Count == before, "A child creation reached a known read-only directory.");
        deny = true;
        await Expect<UnauthorizedAccessException>(() => store.DeleteAsync(full.Single(entry => entry.Name == "open.bin"), default));
        deny = false;
        Require((await store.StatAsync("Full app/open.bin", default))!.IsReadOnly, "A phone permission denial did not update mapped attributes.");

        var reports = await store.ListAsync("崩溃日志", default);
        Require(calls.Last().Args.GetProperty("root").GetString() == "crashes" && calls.Last().Args.GetProperty("refresh").GetBoolean() && reports[0].IsReadOnly,
            "Crash refresh or its read-only flag was lost.");
        await store.ReadAsync(reports[0], 0, 3, default);
        before = calls.Count;
        await Expect<UnauthorizedAccessException>(() => store.DeleteAsync(reports[0], default));
        await Expect<UnauthorizedAccessException>(() => store.MakeDirectoryAsync("崩溃日志/new", default));
        Require(calls.Count == before, "A crash-log write reached the phone.");
        await store.ListAsync("临时文件", default);
        Require(calls.Last().Args.GetProperty("domain").GetString() == "temporary", "Temporary files used a different CoreDevice domain.");

        await using var server = ExplorerWebDavServer.Start(store);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), new Uri(server.BaseUri, Uri.EscapeDataString("Full app") + "/"));
        request.Headers.Add("Depth", "1");
        using var reply = await client.SendAsync(request);
        var xml = XDocument.Parse(await reply.Content.ReadAsStringAsync());
        XNamespace dav = "DAV:";
        XNamespace windows = "urn:schemas-microsoft-com:";
        var resource = xml.Descendants(dav + "response").Single(item => item.Descendants(dav + "displayname").Any(name => name.Value == "locked.bin"));
        Require((int)reply.StatusCode == 207 && resource.Descendants(windows + "Win32FileAttributes").Single().Value.EndsWith("1") &&
            !resource.Descendants(dav + "write").Any() && !resource.Descendants(dav + "lockentry").Any(), "WebDAV advertises write access for a read-only file.");
        using var deletion = await client.DeleteAsync(new Uri(server.BaseUri, Uri.EscapeDataString("Full app") + "/locked.bin"));
        Require(deletion.StatusCode == HttpStatusCode.Forbidden, "HTTP DELETE accepted a read-only phone file.");
        using var creation = await client.PutAsync(new Uri(server.BaseUri, Uri.EscapeDataString("崩溃日志") + "/new.bin"), new ByteArrayContent([]));
        Require(creation.StatusCode == HttpStatusCode.Forbidden, "HTTP PUT accepted a crash-log write.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Expect<OperationCanceledException>(() => store.ListAsync("", cancelled.Token));
        await DeferredDiscoveryAsync();
        Console.WriteLine("PASS unified Explorer folders: paged discovery, container/document/domain routing, Windows names, snapshots, individual read-only files and HTTP permissions.");
    }

    private static async Task DeferredDiscoveryAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        async Task<JsonElement> Request(string action, object? arguments, CancellationToken cancellation)
        {
            var args = JsonSerializer.SerializeToElement(arguments);
            if (action != "explorer_roots") return Metadata(args.GetProperty("path").GetString()!, false);
            if (args.TryGetProperty("initialOnly", out var initial) && initial.GetBoolean())
                return JsonSerializer.SerializeToElement(new { roots = new object[] {
                    new { root = "media" }, new { root = "crashes" }, new { root = "system" }, new { root = "symbols" },
                    new { root = "coredevice", domain = "temporary" }, new { root = "coredevice", domain = "systemCrashLogs" },
                    new { root = "documents", bundleId = "com.test.noName" } }, total = 0, nextOffset = 0, pending = true });
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { interrupted.TrySetResult(); throw; }
            }
            return JsonSerializer.SerializeToElement(new { roots = new[] {
                new { root = "container", bundleId = "com.test.later", name = "Later app" } }, total = 1, nextOffset = (int?)null });
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = await DeviceHubExplorerRootStore.CreateAsync(Request, "Folders", "Media", "Crashes", "Temp", "Logs", timeout.Token, deferDiscovery: true);
        Require(attempts == 0 && (await store.ListAsync("", timeout.Token)).Count == 7,
            "Startup waited for optional probes or a descriptor without name caused failure.");
        var discovery = store.DiscoverRemainingAsync(timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var file = await store.StatAsync("Media/test.bin", timeout.Token);
        await interrupted.Task.WaitAsync(timeout.Token);
        Require(file is not null, "A foreground read could not interrupt background discovery.");
        await discovery;
        var folders = await store.ListAsync("", timeout.Token);
        Require(attempts == 2 && folders.Count == 8 && folders.Any(item => item.Name == "Later app") &&
            folders.Single(item => item.Name == "System shared libraries").IsReadOnly,
            "Interrupted discovery lost a page, existing folders or read-only attributes.");
        Task<JsonElement> Invalid(string action, object? arguments, CancellationToken cancellation) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { roots = new[] { new { name = "Missing service" } }, total = 0, nextOffset = (int?)null }));
        await Expect<IOException>(() => DeviceHubExplorerRootStore.CreateAsync(Invalid, "Folders", "Media", "Crashes", "Temp", "Logs", timeout.Token));
        Console.WriteLine("PASS immediate Explorer startup, nameless service descriptors, background continuation and foreground preemption.");
    }

    private static JsonElement Metadata(string path, bool directory, bool readOnly = false) => JsonSerializer.SerializeToElement(new
    { path, name = path[(path.LastIndexOf('/') + 1)..], type = directory ? "directory" : "file", size = directory ? 0 : 3, modified = 1,
      // Omit a false flag to simulate AFC versions that do not report permissions.
      extras = 0 }).WithReadOnly(readOnly);

    private static JsonElement WithReadOnly(this JsonElement value, bool readOnly)
    {
        if (!readOnly) return value;
        var fields = value.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone());
        fields["readOnly"] = JsonSerializer.SerializeToElement(true);
        return JsonSerializer.SerializeToElement(fields);
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
