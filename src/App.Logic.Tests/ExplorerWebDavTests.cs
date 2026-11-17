using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using IPhoneMirror.App.Services.ExplorerMapping;

internal static class ExplorerWebDavTests
{
    internal static async Task RunAsync()
    {
        var store = new FixtureStore();
        await using var server = ExplorerWebDavServer.Start(store);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(10) };
        var dav = (XNamespace)"DAV:";
        using (var options = await client.SendAsync(new(HttpMethod.Options, new Uri(server.BaseUri, "/"))))
        {
            Require(options.StatusCode == HttpStatusCode.OK && options.Headers.GetValues("DAV").Single() == "1" &&
                !options.Content.Headers.Allow.Contains("PUT"), "The root probe must advertise only implemented capabilities.");
        }
        using (var unknown = await client.GetAsync(new Uri(server.BaseUri, "/unrelated")))
            Require(unknown.StatusCode == HttpStatusCode.NotFound, "A request without the token reached the file service.");
        var calls = store.Calls;
        foreach (var path in new[] { "%2e%2e/secret", "folder%2fsecret", "folder%5csecret", "a//b", "%00", "%GG", "%C0%AE" })
        {
            var response = await RawAsync(server.BaseUri, "GET", server.BaseUri.AbsolutePath + path);
            Require(response.StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), "Unsafe path was accepted: " + path);
        }
        Require(store.Calls == calls, "Invalid paths reached the phone adapter.");
        using (var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), server.BaseUri))
        using (var response = await client.SendAsync(request))
        {
            Require(response.StatusCode == HttpStatusCode.Forbidden &&
                (await response.Content.ReadAsStringAsync()).Contains("propfind-finite-depth"), "Unbounded PROPFIND was not rejected.");
        }
        using (var request = Propfind(server.BaseUri, "1"))
        using (var response = await client.SendAsync(request))
        {
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var resources = xml.Descendants(dav + "response").ToArray();
            Require((int)response.StatusCode == 207 && resources.Length == 4 &&
                resources[0].Descendants(dav + "collection").Any(), "Depth one did not include root and its children.");
            var unicode = resources.Single(item => item.Descendants(dav + "displayname").Any(name => name.Value == "照片 & 100%.bin"));
            Require(unicode.Element(dav + "href")!.Value == server.BaseUri.AbsolutePath + "%E7%85%A7%E7%89%87%20%26%20100%25.bin" &&
                !unicode.Descendants(dav + "collection").Any(), "PROPFIND did not preserve escaped file identity.");
        }
        var body = "<D:propfind xmlns:D=\"DAV:\" xmlns:X=\"urn:test\"><D:prop><D:getcontentlength/><X:unknown/></D:prop></D:propfind>";
        using (var request = Propfind(new Uri(server.BaseUri, "large.bin"), "0", body))
        using (var response = await client.SendAsync(request))
        {
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            Require(xml.Descendants(dav + "getcontentlength").Single().Value == FixtureStore.LargeLength.ToString(CultureInfo.InvariantCulture) &&
                xml.Descendants(dav + "status").Any(status => status.Value.Contains("404")) &&
                xml.Descendants((XNamespace)"urn:test" + "unknown").Count() == 1,
                "Named properties did not keep unknown properties in their own 404 propstat.");
        }
        using (var request = Propfind(server.BaseUri, "0", "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///C:/Windows/win.ini'>]><D:propfind xmlns:D='DAV:'><D:allprop>&a;</D:allprop></D:propfind>"))
        using (var response = await client.SendAsync(request))
            Require(response.StatusCode == HttpStatusCode.BadRequest, "An external XML entity was accepted.");
        store.Reads.Clear();
        using (var request = new HttpRequestMessage(HttpMethod.Head, new Uri(server.BaseUri, "large.bin")))
        {
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-7");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentLength == FixtureStore.LargeLength &&
                    await (await response.Content.ReadAsStreamAsync()).ReadAsync(new byte[1]) == 0 && store.Reads.IsEmpty,
                    "HEAD must return the real length without downloading the phone file.");
            using var cached = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, "large.bin"));
            cached.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.ToString());
            using var unchanged = await client.SendAsync(cached, HttpCompletionOption.ResponseHeadersRead);
            Require(unchanged.StatusCode == HttpStatusCode.NotModified && unchanged.Content.Headers.ContentLength == FixtureStore.LargeLength && store.Reads.IsEmpty,
                    "A 304 must preserve the representation length and must not read file bytes.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, "large.bin")))
        {
            // A range beyond 4 GiB proves offsets stay Int64 and no prefix is
            // downloaded or buffered to satisfy a seek.
            var start = 5L * 1024 * 1024 * 1024;
            var length = ExplorerWebDavServer.ReadChunkSize * 2 + 37;
            request.Headers.TryAddWithoutValidation("Range", $"bytes={start}-{start + length - 1}");
            using var response = await client.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Require(response.StatusCode == HttpStatusCode.PartialContent && bytes.Length == length &&
                response.Content.Headers.ContentRange!.From == start &&
                bytes.Select((value, index) => value == FixtureStore.ByteAt(start + index)).All(match => match) &&
                store.Reads.Count == 3 && store.Reads.All(read => read.Count <= ExplorerWebDavServer.ReadChunkSize) &&
                store.Reads.Min(read => read.Offset) == start,
                "A large-file range downloaded a prefix or violated bounded reads.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, "large.bin")))
        {
            request.Headers.TryAddWithoutValidation("Range", "bytes=-7");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).Length == 7,
                "Suffix range failed.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.BaseUri, "large.bin")))
        {
            request.Headers.TryAddWithoutValidation("Range", $"bytes={FixtureStore.LargeLength}-");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                response.Content.Headers.ContentRange!.Length == FixtureStore.LargeLength, "An out-of-bounds range was accepted.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "new.bin")) { Content = new ByteArrayContent([1, 2]) })
        using (var response = await client.SendAsync(request))
            Require(response.StatusCode == HttpStatusCode.MethodNotAllowed, "The read-only endpoint accepted a phone mutation.");

        var blocked = new FixtureStore { BlockReads = true };
        var abortServer = ExplorerWebDavServer.Start(blocked);
        try
        {
            using var aborted = new TcpClient();
            await aborted.ConnectAsync(IPAddress.Loopback, abortServer.BaseUri.Port);
            var request = Encoding.ASCII.GetBytes($"GET {abortServer.BaseUri.AbsolutePath}large.bin HTTP/1.1\r\nHost: 127.0.0.1:{abortServer.BaseUri.Port}\r\nRange: bytes=0-1\r\n\r\n");
            await aborted.GetStream().WriteAsync(request);
            await blocked.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            aborted.Dispose();
            await blocked.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await abortServer.DisposeAsync(); }
        var disposedStore = new FixtureStore { BlockReads = true };
        var disposedServer = ExplorerWebDavServer.Start(disposedStore);
        using var pendingRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(disposedServer.BaseUri, "large.bin"));
        pendingRequest.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
        using var pending = client.SendAsync(pendingRequest);
        await disposedStore.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await disposedServer.DisposeAsync();
        await disposedStore.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { using var ignored = await pending; throw new Exception("Shutdown unexpectedly completed a blocked file."); }
        catch (HttpRequestException) { }
        await RunWriteAsync(client);
        Console.WriteLine("PASS Explorer WebDAV: token/path isolation, finite properties, XML, real >4GiB ranged streaming, read-only methods, disconnect and shutdown cancellation.");
    }

    private static async Task RunWriteAsync(HttpClient client)
    {
        var store = new WritableStore();
        await using var server = ExplorerWebDavServer.Start(store, maximumUploadBytes: 1024 * 1024);
        using (var options = await client.SendAsync(new(HttpMethod.Options, server.BaseUri)))
            Require(options.Headers.GetValues("DAV").Single() == "1, 2" && options.Content.Headers.Allow.Contains("LOCK"),
                "A writable endpoint must advertise its actual write and lock methods.");
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "new.bin")) { Content = new ByteArrayContent([1, 2, 3, 4]) })
        {
            request.Headers.ExpectContinue = true;
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.Created && store.Bytes("new.bin").SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "PUT with 100-continue did not commit the uploaded bytes.");
            Require(store.SpoolPaths.Count == 1 && !File.Exists(store.SpoolPaths[0]), "The committed PUT left a private temporary file.");
        }
        var calls = store.Mutations;
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "new.bin")) { Content = new ByteArrayContent([9]) })
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.PreconditionFailed && store.Mutations == calls,
                "PUT overwrote an existing file despite If-None-Match.");
        }
        foreach (var header in new[] { "Origin", "Sec-Fetch-Site" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "browser.bin")) { Content = new ByteArrayContent([1]) };
            request.Headers.TryAddWithoutValidation(header, header == "Origin" ? "http://evil.test" : "same-site");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.Forbidden && store.Mutations == calls, "Browser-originated phone writes were allowed.");
        }
        var oversized = await RawAsync(server.BaseUri, "PUT", server.BaseUri.AbsolutePath + "large.bin", "Content-Length: 1048577\r\nExpect: 100-continue\r\n");
        Require(oversized.StartsWith("HTTP/1.1 413 ", StringComparison.Ordinal) && store.Mutations == calls,
            "An oversized upload was admitted or requested its body.");
        using (var response = await client.SendAsync(new(new HttpMethod("MKCOL"), new Uri(server.BaseUri, "folder"))))
            Require(response.StatusCode == HttpStatusCode.Created && (await store.StatAsync("folder", default))!.IsDirectory, "MKCOL failed.");
        var preserved = new DateTimeOffset(2026, 11, 1, 8, 12, 24, TimeSpan.Zero);
        var metadata = $"<D:propertyupdate xmlns:D='DAV:' xmlns:W='urn:schemas-microsoft-com:'><D:set><D:prop>" +
            $"<W:Win32CreationTime>{preserved:R}</W:Win32CreationTime><W:Win32LastAccessTime>{preserved:R}</W:Win32LastAccessTime>" +
            $"<W:Win32LastModifiedTime>{preserved:R}</W:Win32LastModifiedTime><W:Win32FileAttributes>00000020</W:Win32FileAttributes>" +
            "</D:prop></D:set></D:propertyupdate>";
        using (var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), new Uri(server.BaseUri, "new.bin"))
            { Content = new StringContent(metadata, Encoding.UTF8, "application/xml") })
        using (var response = await client.SendAsync(request))
        {
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var actual = await store.StatAsync("new.bin", default);
            Require((int)response.StatusCode == 207 && xml.Descendants((XNamespace)"DAV:" + "status").All(value => value.Value.Contains("200")) &&
                actual!.Modified == preserved && actual.WindowsProperties is { CreationTime: not null, Win32Attributes: 0x20 },
                "PROPPATCH acknowledged metadata without persisting the actual last-modified time and Windows properties.");
        }
        var beforeInvalid = store.Mutations;
        var unsupported = "<D:propertyupdate xmlns:D='DAV:' xmlns:W='urn:schemas-microsoft-com:'><D:set><D:prop>" +
            "<W:Win32FileAttributes>00000000</W:Win32FileAttributes><D:unknown>no</D:unknown></D:prop></D:set></D:propertyupdate>";
        using (var request = new HttpRequestMessage(new HttpMethod("PROPPATCH"), new Uri(server.BaseUri, "new.bin"))
            { Content = new StringContent(unsupported, Encoding.UTF8, "application/xml") })
        using (var response = await client.SendAsync(request))
        {
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            var statuses = xml.Descendants((XNamespace)"DAV:" + "status").Select(value => value.Value).ToArray();
            Require((int)response.StatusCode == 207 && statuses.Any(value => value.Contains("403")) && statuses.Any(value => value.Contains("424")) &&
                store.Mutations == beforeInvalid && (await store.StatAsync("new.bin", default))!.WindowsProperties!.Win32Attributes == 0x20,
                "A partly invalid PROPPATCH mutated supported properties or falsely reported success.");
        }
        using (var request = new HttpRequestMessage(new HttpMethod("MOVE"), new Uri(server.BaseUri, "new.bin")))
        {
            request.Headers.TryAddWithoutValidation("Destination", new Uri(server.BaseUri, "folder/moved.bin").AbsoluteUri);
            request.Headers.TryAddWithoutValidation("Overwrite", "F");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.Created && await store.StatAsync("new.bin", default) is null &&
                store.Bytes("folder/moved.bin").SequenceEqual(new byte[] { 1, 2, 3, 4 }), "MOVE did not rename the original resource.");
        }
        using (var request = new HttpRequestMessage(new HttpMethod("MOVE"), new Uri(server.BaseUri, "folder/moved.bin")))
        {
            request.Headers.TryAddWithoutValidation("Destination", "http://example.test/other.bin");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.BadRequest, "MOVE accepted a different server or token root.");
        }
        var lockBody = "<D:lockinfo xmlns:D='DAV:'><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner>test editor</D:owner></D:lockinfo>";
        string lockToken;
        using (var request = new HttpRequestMessage(new HttpMethod("LOCK"), new Uri(server.BaseUri, "folder/moved.bin")) { Content = new StringContent(lockBody, Encoding.UTF8, "application/xml") })
        {
            request.Headers.Add("Depth", "0");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.OK, "An exclusive file LOCK failed.");
            lockToken = response.Headers.GetValues("Lock-Token").Single();
            Require((await response.Content.ReadAsStringAsync()).Contains(lockToken[1..^1]), "LOCK discovery did not return its issued token.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "folder/moved.bin")) { Content = new ByteArrayContent([8]) })
        using (var response = await client.SendAsync(request))
            Require((int)response.StatusCode == 423, "A write bypassed an existing exclusive lock.");
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "folder/moved.bin")) { Content = new ByteArrayContent([8]) })
        {
            request.Headers.TryAddWithoutValidation("If", "(" + lockToken + ")");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.NoContent && store.Bytes("folder/moved.bin").SequenceEqual(new byte[] { 8 }),
                "The lock holder could not update its resource.");
        }
        using (var request = new HttpRequestMessage(new HttpMethod("LOCK"), new Uri(server.BaseUri, "folder/moved.bin")))
        {
            request.Headers.TryAddWithoutValidation("If", "(" + lockToken + ")");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.OK, "An active lock could not be refreshed.");
        }
        using (var response = await client.DeleteAsync(new Uri(server.BaseUri, "folder")))
            Require((int)response.StatusCode == 423, "Deleting a directory bypassed its descendant file lock.");
        using (var request = new HttpRequestMessage(new HttpMethod("UNLOCK"), new Uri(server.BaseUri, "folder/moved.bin")))
        {
            request.Headers.TryAddWithoutValidation("Lock-Token", lockToken);
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.NoContent, "UNLOCK failed.");
        }
        using (var response = await client.DeleteAsync(new Uri(server.BaseUri, "folder/moved.bin")))
            Require(response.StatusCode == HttpStatusCode.NoContent && await store.StatAsync("folder/moved.bin", default) is null, "DELETE failed.");
        string parentLock;
        using (var request = new HttpRequestMessage(new HttpMethod("LOCK"), new Uri(server.BaseUri, "folder")) { Content = new StringContent(lockBody, Encoding.UTF8, "application/xml") })
        {
            request.Headers.Add("Depth", "0");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.OK, "A depth-zero directory lock failed.");
            parentLock = response.Headers.GetValues("Lock-Token").Single();
        }
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "folder/new.bin")) { Content = new ByteArrayContent([1]) })
        using (var response = await client.SendAsync(request))
            Require((int)response.StatusCode == 423, "Creating a child bypassed its depth-zero parent lock.");
        using (var request = new HttpRequestMessage(HttpMethod.Put, new Uri(server.BaseUri, "folder/new.bin")) { Content = new ByteArrayContent([1]) })
        {
            request.Headers.TryAddWithoutValidation("If", "<" + new Uri(server.BaseUri, "folder").AbsoluteUri + "> (" + parentLock + ")");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.Created, "A properly tagged depth-zero parent token could not create its child.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(server.BaseUri, "folder")))
        {
            request.Headers.TryAddWithoutValidation("If", "(" + parentLock + ")");
            using var response = await client.SendAsync(request);
            Require(response.StatusCode == HttpStatusCode.NoContent, "The collection lock holder could not delete its collection.");
        }
        using (var response = await client.SendAsync(new(new HttpMethod("MKCOL"), new Uri(server.BaseUri, "folder"))))
            Require(response.StatusCode == HttpStatusCode.Created, "A successful DELETE left a ghost collection lock at the reused path.");
        Console.WriteLine("PASS Explorer WebDAV writes: bounded private PUT/100-continue, conditional create, browser rejection, persisted Windows PROPPATCH metadata with atomic invalid-property rejection, MKCOL/MOVE/DELETE, exclusive locks and spool cleanup.");
    }

    private static HttpRequestMessage Propfind(Uri uri, string depth, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.Add("Depth", depth);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        return request;
    }
    private static async Task<string> RawAsync(Uri server, string method, string target, string extraHeaders = "")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\n{extraHeaders}\r\n"), timeout.Token);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, timeout.Token);
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    private sealed class WritableStore : IExplorerFileStore
    {
        private readonly Dictionary<string, (ExplorerFileEntry Entry, byte[] Bytes)> _files = new(StringComparer.Ordinal)
        { [""] = (new("", "Phone media", true, 0, DateTimeOffset.UtcNow), []) };
        internal int Mutations;
        internal readonly List<string> SpoolPaths = [];
        public bool IsReadOnly => false;
        internal byte[] Bytes(string path) => _files[path].Bytes;
        public Task<ExplorerFileEntry?> StatAsync(string path, CancellationToken cancellation) => Task.FromResult(_files.TryGetValue(path, out var value) ? value.Entry : null);
        public Task<IReadOnlyList<ExplorerFileEntry>> ListAsync(string path, CancellationToken cancellation) =>
            Task.FromResult<IReadOnlyList<ExplorerFileEntry>>(_files.Values.Select(value => value.Entry)
                .Where(entry => entry.Path != path && (Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? "") == path).ToArray());
        public Task<ReadOnlyMemory<byte>> ReadAsync(ExplorerFileEntry expected, long offset, int count, CancellationToken cancellation) =>
            Task.FromResult<ReadOnlyMemory<byte>>(_files[expected.Path].Bytes.AsMemory((int)offset, count));
        public async Task PutAsync(string path, string localSourcePath, ExplorerFileEntry? expected, CancellationToken cancellation)
        {
            Require((_files.TryGetValue(path, out var existing) ? existing.Entry : null) == expected, "PUT was given an obsolete expected snapshot.");
            SpoolPaths.Add(localSourcePath);
            var bytes = await File.ReadAllBytesAsync(localSourcePath, cancellation);
            _files[path] = (new(path, path.Split('/')[^1], false, bytes.Length, DateTimeOffset.UtcNow), bytes);
            Mutations++;
        }
        public Task MakeDirectoryAsync(string path, CancellationToken cancellation)
        { _files.Add(path, (new(path, path.Split('/')[^1], true, 0, DateTimeOffset.UtcNow), [])); Mutations++; return Task.CompletedTask; }
        public Task MoveAsync(ExplorerFileEntry expectedSource, string destination, ExplorerFileEntry? expectedDestination, CancellationToken cancellation)
        {
            Require(_files[expectedSource.Path].Entry == expectedSource &&
                (_files.TryGetValue(destination, out var existing) ? existing.Entry : null) == expectedDestination,
                "MOVE was given an obsolete snapshot.");
            var source = _files[expectedSource.Path];
            _files.Remove(expectedSource.Path);
            _files[destination] = (expectedSource with { Path = destination, Name = destination.Split('/')[^1] }, source.Bytes);
            Mutations++; return Task.CompletedTask;
        }
        public Task DeleteAsync(ExplorerFileEntry expected, CancellationToken cancellation)
        { Require(_files[expected.Path].Entry == expected, "DELETE was given an obsolete snapshot."); _files.Remove(expected.Path); Mutations++; return Task.CompletedTask; }
        public Task UpdatePropertiesAsync(ExplorerFileEntry expected, ExplorerFileProperties properties, CancellationToken cancellation)
        {
            Require(_files[expected.Path].Entry == expected, "PROPPATCH was given an obsolete snapshot.");
            var current = _files[expected.Path];
            _files[expected.Path] = (expected with { Modified = properties.LastModifiedTime ?? expected.Modified, WindowsProperties = properties }, current.Bytes);
            Mutations++; return Task.CompletedTask;
        }
    }

    private sealed class FixtureStore : IExplorerFileStore
    {
        internal const long LargeLength = 6L * 1024 * 1024 * 1024;
        private static readonly DateTimeOffset Modified = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        internal int Calls;
        internal readonly ConcurrentBag<(long Offset, int Count)> Reads = [];
        internal bool BlockReads;
        internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReadCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static byte ByteAt(long offset) => (byte)(offset % 251);
        public Task<ExplorerFileEntry?> StatAsync(string path, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<ExplorerFileEntry?>(path switch
            {
                "" => new("", "Phone media", true, 0, Modified),
                "folder" => new("folder", "folder", true, 0, Modified),
                "large.bin" => new("large.bin", "large.bin", false, LargeLength, Modified),
                "照片 & 100%.bin" => new(path, path, false, 0, Modified),
                _ => null
            });
        }
        public Task<IReadOnlyList<ExplorerFileEntry>> ListAsync(string path, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<ExplorerFileEntry>>([
                new("folder", "folder", true, 0, Modified), new("large.bin", "large.bin", false, LargeLength, Modified),
                new("照片 & 100%.bin", "照片 & 100%.bin", false, 0, Modified)
            ]);
        }
        public async Task<ReadOnlyMemory<byte>> ReadAsync(ExplorerFileEntry expected, long offset, int count, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            Reads.Add((offset, count));
            ReadStarted.TrySetResult();
            if (BlockReads)
            {
                try { await Task.Delay(Timeout.Infinite, cancellation); }
                catch (OperationCanceledException) { ReadCancelled.TrySetResult(); throw; }
            }
            return Enumerable.Range(0, count).Select(index => ByteAt(offset + index)).ToArray();
        }
    }
}
