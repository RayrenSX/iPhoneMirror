using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Services.ExplorerMapping;

internal static class ExplorerTreeCacheTests
{
    internal static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "iPhoneMirror-TreeCacheTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await VerifyAsync(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task VerifyAsync(string directory)
    {
        var requests = 0;
        var revision = 1;
        var listingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockListing = false;
        decimal Modified() => revision == 1 ? 1.0000000001m : 1.0000000002m;
        JsonElement Metadata(string path, bool folder = false, bool locked = false) => JsonSerializer.SerializeToElement(new
        { path, name = path[(path.LastIndexOf('/') + 1)..], type = folder ? "directory" : "file", size = folder ? 0 : 3,
          modified = Modified(), readOnly = locked });
        async Task<JsonElement> Request(string action, object? arguments, CancellationToken cancellation)
        {
            Interlocked.Increment(ref requests);
            cancellation.ThrowIfCancellationRequested();
            var args = JsonSerializer.SerializeToElement(arguments);
            if (action == "explorer_roots")
                return JsonSerializer.SerializeToElement(new { roots = new object[] { new { root = "media" }, new { root = "crashes" } },
                    total = 0, nextOffset = (int?)null });
            var path = args.GetProperty("path").GetString()!;
            if (action == "stat_file") return Metadata(path, path is "" or "folder", path == "locked.bin");
            if (action == "list_dir")
            {
                if (blockListing)
                {
                    listingStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                var children = path.Length == 0
                    ? revision == 1 ? new[] { Metadata("folder", true), Metadata("locked.bin", locked: true), Metadata("old.bin") }
                                    : new[] { Metadata("folder", true), Metadata("new.bin") }
                    : new[] { Metadata("folder/file.bin") };
                return JsonSerializer.SerializeToElement(new { path, entries = children, total = children.Length, nextOffset = (int?)null });
            }
            if (action == "read_file") return JsonSerializer.SerializeToElement(new
            { path, size = 3, modified = Modified(), offset = 0, data = "YWJj", eof = true });
            if (action == "delete_mapped_file" && args.GetProperty("expected").GetProperty("modified").GetDecimal() != Modified())
                throw new AutomationException("FILE_MAPPING_CONFLICT", "Changed");
            return Metadata(path);
        }
        Task<DeviceHubExplorerRootStore> Open(ExplorerTreeCache cache) => DeviceHubExplorerRootStore.CreateAsync(Request,
            "Ray's iPhone", "Media", "Crashes", "Temp", "Logs", default, deferDiscovery: true, cache: cache);

        var cache = new ExplorerTreeCache(directory, "device-A", "iPhone\niOS-26");
        var first = await Open(cache);
        Require((await first.StatAsync("", default))!.Name == "Ray's iPhone", "The mapped folder did not use the device name.");
        await first.ListAsync("Media", default);
        await first.StatFreshAsync("Media/folder", default);
        await first.ListAsync("Media/folder", default);
        await cache.FlushAsync(force: true);
        var saved = Directory.GetFiles(directory, "*.json").Single();
        Require(!Path.GetFileName(saved).Contains("device-A", StringComparison.Ordinal), "Raw device identity leaked into the cache filename.");
        Require(!File.ReadAllText(saved).Contains("YWJj", StringComparison.Ordinal), "File contents were persisted with the tree.");
        Require(new ExplorerTreeCache(directory, "device-B", "iPhone\niOS-26").Roots.Length == 0 &&
            new ExplorerTreeCache(directory, "device-A", "iPhone\niOS-27").Roots.Length == 0,
            "A different phone or system version reused this tree.");

        var loaded = new ExplorerTreeCache(directory, "device-A", "iPhone\niOS-26");
        requests = 0;
        var watch = Stopwatch.StartNew();
        var second = await Open(loaded);
        var roots = await second.ListAsync("", default);
        var preview = await second.ListAsync("Media", default);
        var nested = await second.StatAsync("Media/folder", default);
        await second.ListAsync("Media/folder", default);
        Require(requests == 0 && roots.Count == 2 && preview.Count == 3 && nested!.IsDirectory,
            "Opening a saved tree or browsed folder waited for the phone.");
        Require(preview.Single(item => item.Name == "locked.bin").IsReadOnly && roots.Single(item => item.Name == "Crashes").IsReadOnly,
            "Saved per-file or service read-only flags were lost.");
        Console.WriteLine($"PASS saved tree displayed in {watch.ElapsedMilliseconds}ms with zero phone requests.");

        // A timestamp change smaller than one DateTime tick must still fail a
        // cached snapshot precondition. Persist the wire value without rounding.
        revision = 2;
        var stale = preview.Single(item => item.Name == "old.bin");
        await Expect<ExplorerFileConflictException>(() => second.ReadAsync(stale, 0, 3, default));
        await Expect<ExplorerFileConflictException>(() => second.DeleteAsync(stale, default));
        var afterMutation = requests;
        var fresh = await second.ListAsync("Media", default);
        Require(requests > afterMutation && fresh.Any(item => item.Name == "new.bin") && fresh.All(item => item.Name != "old.bin"),
            "A mutation/conflict left its parent tree cached.");
        // Saving the successful replacement must remove ghosts after restart.
        await loaded.FlushAsync(force: true);
        var reloaded = new ExplorerTreeCache(directory, "device-A", "iPhone\niOS-26");
        var third = await Open(reloaded);
        Require((await third.ListAsync("Media", default)).All(item => item.Name != "old.bin"), "Deleted names survived a directory replacement.");

        // Slow refresh cannot block an already cached listing or a foreground
        // stat, and cancellation must not commit a partial directory.
        blockListing = true;
        using (var maintenanceLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var maintenance = third.MaintainCacheAsync(maintenanceLifetime.Token);
            await listingStarted.Task.WaitAsync(maintenanceLifetime.Token);
            var beforePreview = requests;
            await third.ListAsync("Media", maintenanceLifetime.Token).WaitAsync(TimeSpan.FromSeconds(1));
            Require(requests == beforePreview, "A cache hit sent another directory request while refresh was blocked.");
            await third.StatFreshAsync("Media/new.bin", maintenanceLifetime.Token).WaitAsync(TimeSpan.FromSeconds(2));
            maintenanceLifetime.Cancel();
            await Expect<OperationCanceledException>(() => maintenance);
        }
        blockListing = false;
        await reloaded.FlushAsync(force: true);

        // A successful complete catalogue removes sources no longer exposed.
        async Task<JsonElement> MediaOnly(string action, object? arguments, CancellationToken cancellation) => action == "explorer_roots"
            ? JsonSerializer.SerializeToElement(new { roots = new[] { new { root = "media" } }, total = 0, nextOffset = (int?)null })
            : await Request(action, arguments, cancellation);
        var fourth = await DeviceHubExplorerRootStore.CreateAsync(MediaOnly, "Phone", "Media", "Crashes", "Temp", "Logs", default,
            deferDiscovery: true, cache: reloaded);
        await fourth.DiscoverRemainingAsync(default);
        Require((await fourth.ListAsync("", default)).Count == 1, "A removed cached service survived a complete rescan.");
        await reloaded.FlushAsync(force: true);
        Require(new ExplorerTreeCache(directory, "device-A", "iPhone\niOS-26").Roots.Length == 1, "A removed service survived on disk.");

        // Malformed or incompatible local files fail open to live discovery.
        await File.WriteAllTextAsync(saved, "{broken cache");
        var broken = new ExplorerTreeCache(directory, "device-A", "iPhone\niOS-26");
        Require(broken.Roots.Length == 0, "Malformed cache was accepted.");
        var previousRequests = requests;
        await Open(broken);
        Require(requests > previousRequests, "A corrupt cache prevented live discovery.");
        Console.WriteLine("PASS tree persistence: phone/system isolation, read-only flags, exact snapshots, invalidation, foreground priority, pruning and corrupt-cache fallback.");
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Expect<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
