using System.IO;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services.Automation;
using IPhoneMirror.App.Services.ExplorerMapping;

internal static class ExplorerFileStoreTests
{
    internal static async Task Run()
    {
        await TestSnapshotsAndWrites();
        await TestModificationTimes();
        await TestWindowsProperties();
        await TestEnumeration();
        await TestQueueAndErrors();
        Console.WriteLine("PASS Explorer phone file store: exact metadata, chunk consistency, mapped writes, pagination, bounded serialization and error translation.");
    }

    private static async Task TestSnapshotsAndWrites()
    {
        const string stamp = "2026-11-17T10:20:30.123456789+00:00";
        const string changedStamp = "2026-11-17T10:20:30.123456790+00:00";
        var calls = new List<(string Action, JsonElement Args)>();
        var changed = false;
        var store = new DeviceHubExplorerFileStore((action, args, token) =>
        {
            var request = JsonSerializer.SerializeToElement(args);
            calls.Add((action, request));
            return Task.FromResult(action switch
            {
                "stat_file" => Metadata(request.GetProperty("path").GetString()!, stamp, 6),
                "read_file" => JsonSerializer.SerializeToElement(new
                {
                    path = "notes.txt", offset = request.GetProperty("offset").GetInt64(), size = 6,
                    modified = changed ? changedStamp : stamp,
                    data = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.GetProperty("offset").GetInt64() == 0 ? "ab" : "cdef")),
                    eof = request.GetProperty("offset").GetInt64() != 0,
                }),
                _ => JsonSerializer.SerializeToElement(new { status = "done" }),
            });
        });
        Require(!store.IsReadOnly, "The mapping adapter did not enable mapped writes.");
        var expected = (await store.StatAsync("notes.txt", default))!;
        Require(Encoding.UTF8.GetString((await store.ReadAsync(expected, 0, 2, default)).Span) == "ab",
            "The phone file block was not decoded correctly.");
        changed = true;
        await Expect<ExplorerFileConflictException>(() => store.ReadAsync(expected, 2, 4, default));
        await Expect<ExplorerFileConflictException>(() => store.DeleteAsync(expected with { }, default));
        await Expect<ArgumentOutOfRangeException>(() => store.ReadAsync(expected, 0, 192 * 1024 + 1, default));

        var spool = Directory.CreateTempSubdirectory("iPhoneMirror-ExplorerTransfer-");
        try
        {
            var upload = Path.Combine(spool.FullName, "content.upload");
            await File.WriteAllTextAsync(upload, "new bytes");
            await store.PutAsync("notes.txt", upload, expected, default);
            await store.PutAsync("new.txt", upload, null, default);
            await store.MakeDirectoryAsync("folder", default);
            var destination = (await store.StatAsync("destination.txt", default))!;
            await store.MoveAsync(expected, "destination.txt", destination, default);
            await store.DeleteAsync(expected, default);
            foreach (var call in calls.Where(call => call.Action is "write_mapped_file" or "move_file" or "delete_mapped_file"))
            {
                Require(call.Args.GetProperty("root").GetString() == "media", "A mapped write escaped the media service.");
                if (call.Args.GetProperty("expected").ValueKind != JsonValueKind.Null)
                    Require(call.Args.GetProperty("expected").GetProperty("modified").GetString() == stamp,
                        "A write rounded the original phone modification timestamp.");
            }
            var move = calls.Single(call => call.Action == "move_file").Args;
            Require(move.GetProperty("destinationExpected").GetProperty("modified").GetString() == stamp &&
                move.GetProperty("path").GetString() == "notes.txt" && move.GetProperty("destination").GetString() == "destination.txt",
                "Move lost the source or destination optimistic precondition.");
            Require(calls.Single(call => call.Action == "delete_mapped_file").Args.GetProperty("recursive").GetBoolean(),
                "Mapped directory deletion did not enable the bounded recursive backend operation.");
            await Expect<UnauthorizedAccessException>(() => store.PutAsync("notes.txt", Path.Combine(spool.FullName, "other.txt"), expected, default));
            await Expect<UnauthorizedAccessException>(() => store.DeleteAsync(new("", "", true, 0, DateTimeOffset.UtcNow), default));
            await Expect<UnauthorizedAccessException>(() => store.MakeDirectoryAsync("../escape", default));
        }
        finally { spool.Delete(recursive: true); }

        // A numeric AFC timestamp must also remain an exact JSON number when
        // passed back, independently of its presentation DateTimeOffset.
        JsonElement sent = default;
        var numeric = new DeviceHubExplorerFileStore((action, args, token) =>
        {
            if (action == "stat_file") return Task.FromResult(Json("{\"path\":\"n\",\"name\":\"n\",\"type\":\"file\",\"size\":1,\"modified\":1794910830.123456789}"));
            sent = JsonSerializer.SerializeToElement(args);
            return Task.FromResult(Json("{}"));
        });
        await numeric.DeleteAsync((await numeric.StatAsync("n", default))!, default);
        Require(sent.GetProperty("expected").GetProperty("modified").GetRawText() == "1794910830.123456789",
            "Numeric metadata lost timestamp precision during a write.");
    }

    private static async Task TestModificationTimes()
    {
        const string naiveStamp = "2026-11-17 10:20:30.123456";
        const string awareStamp = "2026-11-17T10:20:30.123456+02:00";
        string? writtenStamp = null;
        var store = new DeviceHubExplorerFileStore((action, args, token) =>
        {
            var request = JsonSerializer.SerializeToElement(args);
            if (action == "stat_file")
            {
                var path = request.GetProperty("path").GetString()!;
                return Task.FromResult(Metadata(path, path == "local" ? naiveStamp : awareStamp, 1));
            }
            writtenStamp = request.GetProperty("expected").GetProperty("modified").GetString();
            return Task.FromResult(Json("{}"));
        });
        // AFC uses Python datetime.fromtimestamp on this same computer, then
        // serializes that local datetime without a UTC offset. Presentation
        // must recover its actual instant, while preconditions keep raw text.
        var localTime = new DateTime(2026, 11, 17, 10, 20, 30, DateTimeKind.Local).AddTicks(1_234_560);
        var local = (await store.StatAsync("local", default))!;
        Require(local.Modified == new DateTimeOffset(localTime).ToUniversalTime() && local.Modified.Offset == TimeSpan.Zero,
            "An AFC local modification time without an offset was interpreted as UTC.");
        await store.DeleteAsync(local, default);
        Require(writtenStamp == naiveStamp, "Formatting a local modification time changed its raw write precondition.");
        var aware = (await store.StatAsync("aware", default))!;
        var expectedAware = new DateTimeOffset(2026, 11, 17, 10, 20, 30, TimeSpan.FromHours(2)).AddTicks(1_234_560);
        Require(aware.Modified == expectedAware.ToUniversalTime() && aware.Modified.Offset == TimeSpan.Zero,
            "A modification time with an explicit offset was reinterpreted in the local time zone.");
        await store.DeleteAsync(aware, default);
        Require(writtenStamp == awareStamp, "Normalizing an offset-aware presentation changed its raw write precondition.");
    }

    private static async Task TestWindowsProperties()
    {
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["folder"] = "2026-11-17T00:00:00.123456789Z",
            ["folder/file"] = "2026-11-17T00:00:00.123456789Z",
        };
        var calls = new List<(string Action, JsonElement Args)>();
        var store = new DeviceHubExplorerFileStore((action, args, token) =>
        {
            var request = JsonSerializer.SerializeToElement(args);
            calls.Add((action, request));
            var path = request.GetProperty("path").GetString()!;
            JsonElement Entry(string target) => JsonSerializer.SerializeToElement(new
            {
                path = target, name = target[(target.LastIndexOf('/') + 1)..],
                type = target.EndsWith("folder", StringComparison.Ordinal) ? "directory" : "file", size = 1, modified = stamps[target],
            });
            if (action == "list_dir") return Task.FromResult(JsonSerializer.SerializeToElement(new
            { path, total = 1, entries = new[] { Entry(path + "/file") }, nextOffset = (int?)null }));
            if (action == "stat_file") return Task.FromResult(stamps.ContainsKey(path) ? Entry(path) : Json("null"));
            if (action == "set_mapped_file_time")
            {
                Require(request.GetProperty("expected").GetProperty("modified").GetString() == stamps[path],
                    "Setting phone mtime lost the original raw snapshot.");
                stamps[path] = "2026-11-18T12:30:15.1234567Z";
                return Task.FromResult(Entry(path));
            }
            if (action == "move_file")
            {
                var destination = request.GetProperty("destination").GetString()!;
                foreach (var old in stamps.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
                {
                    stamps[destination + old[path.Length..]] = stamps[old];
                    stamps.Remove(old);
                }
                return Task.FromResult(Entry(destination));
            }
            if (action == "delete_mapped_file")
                foreach (var old in stamps.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)).ToArray()) stamps.Remove(old);
            return Task.FromResult(Json("{}"));
        });
        var entry = (await store.StatAsync("folder/file", default))!;
        var creation = new DateTimeOffset(2025, 2, 3, 4, 5, 6, TimeSpan.Zero);
        var access = creation.AddDays(1);
        var modified = new DateTimeOffset(2026, 11, 18, 12, 30, 15, TimeSpan.Zero).AddTicks(1_234_567);
        await ((IExplorerFileStore)store).UpdatePropertiesAsync(entry, new(creation, access, modified, 32), default);
        var timeRequest = calls.Single(call => call.Action == "set_mapped_file_time").Args;
        Require(timeRequest.GetProperty("modifiedNs").GetInt64() == checked((modified.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100),
            "Phone mtime was not passed as precise UTC epoch nanoseconds.");
        var updated = (await store.StatAsync("folder/file", default))!;
        Require(updated.Modified == modified && updated.WindowsProperties is { } properties &&
            properties.CreationTime == creation && properties.LastAccessTime == access && properties.Win32Attributes == 32,
            "Mapped Windows properties were not retained with the new phone snapshot.");
        Require((await store.ListAsync("folder", default)).Single().WindowsProperties?.CreationTime == creation,
            "Directory enumeration omitted session Windows metadata.");
        await ((IExplorerFileStore)store).UpdatePropertiesAsync(updated, new(LastAccessTime: access.AddDays(2)), default);
        Require((await store.StatAsync("folder/file", default))!.WindowsProperties is { } merged &&
            merged.CreationTime == creation && merged.LastAccessTime == access.AddDays(2) && merged.Win32Attributes == 32,
            "A subsequent independent metadata update erased an earlier session property.");
        await Expect<ExplorerFileConflictException>(() => ((IExplorerFileStore)store).UpdatePropertiesAsync(entry, new(Win32Attributes: 0), default));
        var before = calls.Count;
        await Expect<UnauthorizedAccessException>(() => ((IExplorerFileStore)store).UpdatePropertiesAsync(updated, new(Win32Attributes: 1), default));
        Require(calls.Count == before, "Unsupported read-only attributes partially wrote metadata before rejecting.");
        await store.MoveAsync((await store.StatAsync("folder", default))!, "renamed-folder", null, default);
        Require((await store.StatAsync("renamed-folder/file", default))!.WindowsProperties?.CreationTime == creation,
            "Moving a directory lost its child's session metadata.");
        stamps["renamed-folder/file"] = "2026-11-19T00:00:00Z";
        var externallyChanged = (await store.StatAsync("renamed-folder/file", default))!;
        Require(externallyChanged.WindowsProperties?.CreationTime == externallyChanged.Modified,
            "External file changes retained an obsolete Windows metadata overlay.");
        await ((IExplorerFileStore)store).UpdatePropertiesAsync(externallyChanged, new(CreationTime: creation, Win32Attributes: 128), default);
        await store.DeleteAsync((await store.StatAsync("renamed-folder", default))!, default);
        stamps["renamed-folder"] = "2026-11-17T00:00:00Z";
        stamps["renamed-folder/file"] = "2026-11-19T00:00:00Z";
        var recreated = (await store.StatAsync("renamed-folder/file", default))!;
        Require(recreated.WindowsProperties?.CreationTime == recreated.Modified,
            "Deleting a directory leaked its old metadata into a newly created file.");
        await ((IExplorerFileStore)store).UpdatePropertiesAsync(recreated, new(CreationTime: creation), default);
        var spool = Directory.CreateTempSubdirectory("iPhoneMirror-ExplorerTransfer-");
        try
        {
            var upload = Path.Combine(spool.FullName, "content.upload");
            await File.WriteAllTextAsync(upload, "replacement");
            await store.PutAsync(recreated.Path, upload, recreated, default);
        }
        finally { spool.Delete(recursive: true); }
        var replaced = (await store.StatAsync(recreated.Path, default))!;
        Require(replaced.WindowsProperties?.CreationTime == replaced.Modified,
            "Replacing file content kept the previous file's session metadata.");
        await ((IExplorerFileStore)store).UpdatePropertiesAsync(replaced, new(CreationTime: creation), default);
        var rememberedStamp = stamps[replaced.Path];
        stamps.Remove(replaced.Path);
        Require(await store.StatAsync(replaced.Path, default) is null, "A removed file remained visible.");
        stamps[replaced.Path] = rememberedStamp;
        var afterMissing = (await store.StatAsync(replaced.Path, default))!;
        Require(afterMissing.WindowsProperties?.CreationTime == afterMissing.Modified,
            "A file observed missing later reused stale metadata after same-snapshot recreation.");
    }

    private static async Task TestEnumeration()
    {
        var offsets = new List<int>();
        var store = new DeviceHubExplorerFileStore((action, args, token) =>
        {
            var request = JsonSerializer.SerializeToElement(args);
            var offset = request.GetProperty("offset").GetInt32();
            offsets.Add(offset);
            Require(request.GetProperty("limit").GetInt32() == 500, "Directory pages exceeded the phone page budget.");
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                path = "folder", total = 501, nextOffset = offset == 0 ? (int?)500 : null,
                entries = Enumerable.Range(offset, offset == 0 ? 500 : 1).Select(index => new
                {
                    path = "folder/f" + index, name = "f" + index,
                    type = index == 499 ? "other" : "file", size = 1, modified = "2026-11-17T00:00:00Z",
                }),
            }));
        });
        var entries = await store.ListAsync("folder", default);
        Require(entries.Count == 500 && offsets.SequenceEqual(new[] { 0, 500 }) && entries.All(entry => entry.Name != "f499"),
            "Pagination lost a page or exposed a special file.");
        var overflow = new DeviceHubExplorerFileStore((action, args, token) => Task.FromResult(Json("{\"path\":\"\",\"total\":100001,\"entries\":[],\"nextOffset\":null}")));
        await Expect<IOException>(() => overflow.ListAsync("", default));
        var traversal = new DeviceHubExplorerFileStore((action, args, token) => Task.FromResult(Json("{\"path\":\"\",\"total\":1,\"entries\":[{\"path\":\"../x\",\"name\":\"../x\",\"type\":\"file\"}],\"nextOffset\":null}")));
        await Expect<IOException>(() => traversal.ListAsync("", default));
    }

    private static async Task TestQueueAndErrors()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximum = 0;
        var store = new DeviceHubExplorerFileStore(async (action, args, token) =>
        {
            Interlocked.Increment(ref calls);
            var now = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, now);
            entered.TrySetResult();
            try { await release.Task.WaitAsync(token); return Metadata("x", "2026-11-17T00:00:00Z", 1); }
            finally { Interlocked.Decrement(ref active); }
        });
        var first = store.StatAsync("x", default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = Enumerable.Range(0, 31).Select(_ => store.StatAsync("x", default)).ToArray();
        await Expect<IOException>(() => store.StatAsync("x", default));
        Require(calls == 1, "Concurrent mapping operations reached the bridge gate.");
        release.SetResult();
        await Task.WhenAll(queued.Append(first));
        Require(maximum == 1 && calls == 32, "The bounded mapping queue overlapped bridge operations or lost a request.");

        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortQueue = new DeviceHubExplorerFileStore(async (action, args, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Metadata("x", "2026-11-17T00:00:00Z", 1);
        }, TimeSpan.FromMilliseconds(30));
        first = shortQueue.StatAsync("x", default);
        await entered.Task;
        await Expect<IOException>(() => shortQueue.StatAsync("x", default));
        using var cancellation = new CancellationTokenSource();
        var cancelled = shortQueue.StatAsync("x", cancellation.Token);
        cancellation.Cancel();
        await Expect<OperationCanceledException>(() => cancelled);
        release.SetResult();
        await first;
        Require(await shortQueue.StatAsync("x", default) is not null, "A cancelled/expired queue entry leaked admission or its gate.");

        var attempts = 0;
        var errorCode = "FILE_NOT_FOUND";
        var failures = new DeviceHubExplorerFileStore((action, args, token) =>
        {
            attempts++;
            throw new AutomationException(errorCode, "Sensitive device path must not escape");
        });
        Require(await failures.StatAsync("missing", default) is null, "File-not-found did not become a missing stat.");
        errorCode = "FILE_MAPPING_CONFLICT";
        await Expect<ExplorerFileConflictException>(() => failures.MakeDirectoryAsync("new", default));
        errorCode = "FILE_ACCESS_DENIED";
        await Expect<UnauthorizedAccessException>(() => failures.MakeDirectoryAsync("new", default));
        errorCode = "CONTROL_LOCKED";
        await Expect<IOException>(() => failures.MakeDirectoryAsync("new", default));
        Require(attempts == 4, "A device mutation was automatically retried.");
    }

    private static JsonElement Metadata(string path, string modified, long size) => JsonSerializer.SerializeToElement(new
    {
        path, name = path[(path.LastIndexOf('/') + 1)..], type = "file", size, modified,
    });
    private static JsonElement Json(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T error)
        {
            Require(!error.Message.Contains("Sensitive device path", StringComparison.Ordinal), "A backend message disclosed private path context.");
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
