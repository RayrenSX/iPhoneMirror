using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IPhoneMirror.App.Services.ExplorerMapping;

// A per-mapping, loopback-only WebDAV endpoint. It has no HTTP.sys URL ACL,
// permanent listener, LAN exposure or local replica of the phone filesystem.
internal sealed class ExplorerWebDavServer : IAsyncDisposable
{
    internal const int ReadChunkSize = 192 * 1024;
    private const int MaximumHeaders = 16 * 1024;
    private const int MaximumBody = 64 * 1024;
    private const int MaximumEntries = 10000;
    private const string ReadMethods = "OPTIONS, PROPFIND, HEAD, GET";
    private const string WriteMethods = ReadMethods + ", PUT, MKCOL, MOVE, DELETE, LOCK, UNLOCK, PROPPATCH";
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace Windows = "urn:schemas-microsoft-com:";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IExplorerFileStore _store;
    private readonly long _maximumUploadBytes;
    private readonly Action<string, int>? _requestObserved;
    private readonly ConcurrentDictionary<NetworkStream, RequestObservation> _observations = new();
    private readonly ExplorerWebDavLocks _locks;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly SemaphoreSlim _admission = new(8, 8);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private readonly string _rootPath;
    private int _active;
    private int _disposed;

    private ExplorerWebDavServer(IExplorerFileStore store, TcpListener listener, Uri baseUri, long maximumUploadBytes,
        Action<string, int>? requestObserved)
    {
        _store = store;
        _maximumUploadBytes = maximumUploadBytes;
        _requestObserved = requestObserved;
        _listener = listener;
        BaseUri = baseUri;
        _rootPath = baseUri.AbsolutePath;
        _locks = new(HrefByPath, TryResolveDestination);
        _accepting = AcceptAsync();
    }

    internal Uri BaseUri { get; }
    internal static ExplorerWebDavServer Start(IExplorerFileStore store, long maximumUploadBytes = 50L * 1024 * 1024,
        Action<string, int>? requestObserved = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maximumUploadBytes is <= 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumUploadBytes));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start(16);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            return new(store, listener, new Uri($"http://127.0.0.1:{port}/{secret}/"), maximumUploadBytes, requestObserved);
        }
        catch { listener.Stop(); throw; }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                if (!_admission.Wait(0)) { client.Dispose(); continue; }
                Interlocked.Increment(ref _active);
                _connections.TryAdd(client, 0);
                _ = ServeAsync(client);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            var started = false;
            Task? disconnect = null;
            NetworkStream? observedStream = null;
            try
            {
                client.NoDelay = true;
                var output = client.GetStream();
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await ReadRequestAsync(output, headersTimeout.Token).ConfigureAwait(false);
                if (request is null) return;
                observedStream = output;
                _observations.TryAdd(output, new(request.Method));
                disconnect = MonitorDisconnectAsync(client, lifetime);
                var cancellation = lifetime.Token;
                if (!ValidHost(request.Headers.GetValueOrDefault("Host"))) throw new HttpFailure(400);
                if (request.Headers.ContainsKey("Origin")) throw new HttpFailure(403);
                // The Windows redirector probes server capabilities at '/'. It
                // receives no directory information without the mapping token.
                if (request.Method == "OPTIONS" && request.Target is "/" or "*")
                {
                    if (request.Length != 0) throw new HttpFailure(400);
                    await EmptyAsync(output, 200, cancellation, DavHeaders()).ConfigureAwait(false);
                    return;
                }
                var path = ResolvePath(request.Target);
                if (request.Method == "OPTIONS")
                {
                    if (request.Length != 0) throw new HttpFailure(400);
                    await EmptyAsync(output, 200, cancellation, DavHeaders()).ConfigureAwait(false);
                    return;
                }
                if (request.Method is "PUT" or "MKCOL" or "MOVE" or "DELETE" or "LOCK" or "UNLOCK" or "PROPPATCH")
                {
                    if (_store.IsReadOnly) throw new HttpFailure(405, new() { ["Allow"] = ReadMethods });
                    if (request.Headers.Keys.Any(name => name.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase))) throw new HttpFailure(403);
                    await HandleWriteAsync(output, request, path, cancellation).ConfigureAwait(false);
                    return;
                }
                if (request.Method is not ("PROPFIND" or "GET" or "HEAD"))
                    throw new HttpFailure(405, new() { ["Allow"] = _store.IsReadOnly ? ReadMethods : WriteMethods });
                if (request.Length > MaximumBody || request.Method != "PROPFIND" && request.Length != 0) throw new HttpFailure(413);
                await ContinueAsync(output, request, cancellation).ConfigureAwait(false);
                var entry = await DeviceAsync(token => _store.StatAsync(path, token), cancellation).ConfigureAwait(false);
                if (entry is null) throw new HttpFailure(404);
                ValidateEntry(entry, path);
                if (request.Method == "PROPFIND")
                {
                    var depth = request.Headers.GetValueOrDefault("Depth", "infinity");
                    if (depth is not ("0" or "1"))
                    {
                        var error = XmlBytes(new XElement(Dav + "error", new XElement(Dav + "propfind-finite-depth")));
                        await ResponseAsync(output, 403, error, cancellation, new() { ["Content-Type"] = "application/xml; charset=utf-8" }).ConfigureAwait(false);
                        return;
                    }
                    var query = PropertyQuery.Parse(await ReadBodyAsync(output, request.Length, cancellation).ConfigureAwait(false));
                    IReadOnlyList<ExplorerFileEntry> children = entry.IsDirectory && depth == "1"
                        ? await DeviceAsync(token => _store.ListAsync(path, token), cancellation).ConfigureAwait(false)
                        : [];
                    if (children.Count > MaximumEntries) throw new HttpFailure(507);
                    foreach (var child in children)
                    {
                        ValidateEntry(child, child.Path);
                        var prefix = path.Length == 0 ? "" : path + "/";
                        if (!child.Path.StartsWith(prefix, StringComparison.Ordinal) ||
                            child.Path.Length == prefix.Length || child.Path[prefix.Length..].Contains('/'))
                            throw new IOException("Invalid directory child.");
                    }
                    var xml = new XElement(Dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", Dav),
                        new[] { entry }.Concat(children).Select(item => PropertyResponse(item, query)));
                    var body = XmlBytes(xml);
                    if (body.Length > 16 * 1024 * 1024) throw new HttpFailure(507);
                    await ResponseAsync(output, 207, body, cancellation,
                        new() { ["Content-Type"] = "application/xml; charset=utf-8", ["DAV"] = _store.IsReadOnly ? "1" : "1, 2" }).ConfigureAwait(false);
                    return;
                }
                if (entry.IsDirectory) throw new HttpFailure(405, new() { ["Allow"] = "OPTIONS, PROPFIND" });
                var responseHeaders = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/octet-stream", ["Accept-Ranges"] = "bytes",
                    ["Last-Modified"] = entry.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture),
                    ["ETag"] = Etag(entry)
                };
                if (MatchesEntityTag(request.Headers.GetValueOrDefault("If-None-Match"), Etag(entry)))
                { await WriteHeadersAsync(output, 304, entry.Length, responseHeaders, cancellation).ConfigureAwait(false); return; }
                var offset = 0L;
                var count = entry.Length;
                var status = 200;
                // mtime/length is a weak validator. It must not authorize an
                // If-Range partial response, even when its string matches.
                if (request.Method == "GET" && request.Headers.TryGetValue("Range", out var range) && !request.Headers.ContainsKey("If-Range"))
                {
                    if (!TryRange(range, entry.Length, out offset, out count))
                        throw new HttpFailure(416, new() { ["Content-Range"] = $"bytes */{entry.Length}" });
                    status = 206;
                    responseHeaders["Content-Range"] = $"bytes {offset}-{offset + count - 1}/{entry.Length}";
                }
                await WriteHeadersAsync(output, status, count, responseHeaders, cancellation).ConfigureAwait(false);
                started = true;
                if (request.Method == "HEAD") return;
                while (count > 0)
                {
                    var requested = (int)Math.Min(ReadChunkSize, count);
                    var data = await DeviceAsync(token => _store.ReadAsync(entry, offset, requested, token), cancellation).ConfigureAwait(false);
                    if (data.Length is <= 0 || data.Length > requested) throw new IOException("Invalid file read result.");
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    writeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await output.WriteAsync(data, writeTimeout.Token).ConfigureAwait(false);
                    offset += data.Length;
                    count -= data.Length;
                }
            }
            catch (Exception error) when (error is HttpFailure or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or
                IOException or SocketException or XmlException or DecoderFallbackException or OperationCanceledException or TimeoutException or InvalidOperationException)
            {
                if (!started && client.Connected && !lifetime.IsCancellationRequested)
                {
                    var failure = error as HttpFailure;
                    var status = failure?.Status ?? (error is ExplorerFileConflictException ? 412 : error is FileNotFoundException or DirectoryNotFoundException ? 404 :
                        error is UnauthorizedAccessException ? 403 : error is XmlException or DecoderFallbackException ? 400 : 503);
                    try { await EmptyAsync(client.GetStream(), status, lifetime.Token, failure?.Headers).ConfigureAwait(false); }
                catch (Exception writeError) when (writeError is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                }
            }
            catch (Exception)
            {
                if (!started && !lifetime.IsCancellationRequested)
                    try { await EmptyAsync(client.GetStream(), 503, lifetime.Token).ConfigureAwait(false); }
                    catch (Exception) { }
            }
            finally
            {
                lifetime.Cancel();
                if (disconnect is not null) await disconnect.ConfigureAwait(false);
                if (observedStream is not null && _observations.TryRemove(observedStream, out var observation))
                {
                    // Only protocol method and status are observable. Capability
                    // token URLs, phone paths, headers and file data stay private.
                    try { _requestObserved?.Invoke(observation.Method, observation.Status); }
                    catch (Exception) { /* Diagnostics must never change an operation result. */ }
                }
                _connections.TryRemove(client, out _);
                _admission.Release();
                if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _disposed) != 0) _drained.TrySetResult();
            }
        }
    }

    private bool ValidHost(string? host) => host is not null &&
        (host.Equals($"127.0.0.1:{BaseUri.Port}", StringComparison.OrdinalIgnoreCase) ||
         host.Equals($"localhost:{BaseUri.Port}", StringComparison.OrdinalIgnoreCase));

    private async Task HandleWriteAsync(NetworkStream stream, Request request, string path, CancellationToken cancellation)
    {
        if (path.Length == 0 && request.Method is not ("LOCK" or "UNLOCK")) throw new HttpFailure(403);
        if (request.Method == "PUT")
        {
            if (!request.Headers.ContainsKey("Content-Length")) throw new HttpFailure(411);
            if (request.Length > _maximumUploadBytes) throw new HttpFailure(413);
        }
        else if (request.Length > MaximumBody) throw new HttpFailure(413);
        if (!await _mutation.WaitAsync(TimeSpan.FromSeconds(35), cancellation).ConfigureAwait(false)) throw new HttpFailure(503);
        try
        {
            var entry = await DeviceAsync(token => _store.StatAsync(path, token), cancellation).ConfigureAwait(false);
            if (entry is not null) ValidateEntry(entry, path);
            CheckPreconditions(request, entry);
            if (request.Method is "LOCK" or "UNLOCK")
            {
                await HandleLockAsync(stream, request, path, entry, cancellation).ConfigureAwait(false);
                return;
            }
            var destination = request.Method == "MOVE" ? TryResolveDestination(request.Headers.GetValueOrDefault("Destination", "")) : null;
            if (request.Method == "MOVE" && (destination is null || destination.Length == 0)) throw new HttpFailure(400);
            var conditions = request.Headers.GetValueOrDefault("If");
            var namespaceParents = request.Method switch
            {
                "PUT" when entry is null => new[] { ParentPath(path) },
                "MKCOL" or "DELETE" => [ParentPath(path)],
                "MOVE" => [ParentPath(path), ParentPath(destination!)],
                _ => Array.Empty<string>()
            };
            if (!_locks.ValidateWrite(destination is null ? [path] : [path, destination], conditions, path,
                includeDescendants: request.Method == "MOVE" || entry?.IsDirectory == true && request.Method == "DELETE"))
                throw new HttpFailure(request.Headers.ContainsKey("If") ? 412 : 423);
            // Even a depth-zero collection lock protects the names of its
            // immediate children. Do not recursively inspect the parents here:
            // an unrelated sibling's lock must not prevent this operation.
            if (!_locks.ValidateWrite(namespaceParents, conditions, path))
                throw new HttpFailure(request.Headers.ContainsKey("If") ? 412 : 423);
            switch (request.Method)
            {
                case "PUT":
                    if (entry?.IsDirectory == true) throw new HttpFailure(405);
                    await RequireParentAsync(path, cancellation).ConfigureAwait(false);
                    await ContinueAsync(stream, request, cancellation).ConfigureAwait(false);
                    await SpoolPutAsync(stream, path, request.Length, entry, cancellation).ConfigureAwait(false);
                    await EmptyAsync(stream, entry is null ? 201 : 204, cancellation).ConfigureAwait(false);
                    return;
                case "PROPPATCH":
                    if (entry is null) throw new HttpFailure(404);
                    await HandlePropertiesAsync(stream, request, entry, cancellation).ConfigureAwait(false);
                    return;
                case "MKCOL":
                    if (request.Length != 0) throw new HttpFailure(415);
                    if (entry is not null) throw new HttpFailure(405);
                    await RequireParentAsync(path, cancellation).ConfigureAwait(false);
                    await DeviceMutationAsync(token => _store.MakeDirectoryAsync(path, token), cancellation).ConfigureAwait(false);
                    await EmptyAsync(stream, 201, cancellation).ConfigureAwait(false);
                    return;
                case "DELETE":
                    if (request.Length != 0) throw new HttpFailure(400);
                    if (entry is null) throw new HttpFailure(404);
                    if (entry.IsDirectory && request.Headers.GetValueOrDefault("Depth", "infinity") != "infinity") throw new HttpFailure(400);
                    await DeviceMutationAsync(token => _store.DeleteAsync(entry, token), cancellation).ConfigureAwait(false);
                    _locks.RemoveSubtree(path);
                    await EmptyAsync(stream, 204, cancellation).ConfigureAwait(false);
                    return;
                case "MOVE":
                    if (request.Length != 0) throw new HttpFailure(400);
                    if (entry is null) throw new HttpFailure(404);
                    if (destination == path || destination!.StartsWith(path + "/", StringComparison.Ordinal)) throw new HttpFailure(403);
                    var overwrite = request.Headers.GetValueOrDefault("Overwrite", "T");
                    if (overwrite is not ("T" or "F")) throw new HttpFailure(400);
                    var target = await DeviceAsync(token => _store.StatAsync(destination!, token), cancellation).ConfigureAwait(false);
                    if (target is not null)
                    {
                        ValidateEntry(target, destination!);
                        if (overwrite == "F") throw new HttpFailure(412);
                    }
                    await RequireParentAsync(destination!, cancellation).ConfigureAwait(false);
                    await DeviceMutationAsync(token => _store.MoveAsync(entry, destination!, target, token), cancellation).ConfigureAwait(false);
                    _locks.RemoveSubtree(path);
                    if (target is not null) _locks.RemoveSubtree(destination!);
                    await EmptyAsync(stream, target is null ? 201 : 204, cancellation).ConfigureAwait(false);
                    return;
            }
        }
        finally { _mutation.Release(); }
    }

    private async Task HandleLockAsync(NetworkStream stream, Request request, string path, ExplorerFileEntry? entry, CancellationToken cancellation)
    {
        try
        {
            if (request.Method == "UNLOCK")
            {
                if (request.Length != 0) throw new HttpFailure(400);
                var header = request.Headers.GetValueOrDefault("Lock-Token");
                if (header is null || header.Length < 3 || header[0] != '<' || header[^1] != '>') throw new HttpFailure(400);
                if (!_locks.Release(path, header[1..^1])) throw new HttpFailure(409);
                await EmptyAsync(stream, 204, cancellation).ConfigureAwait(false);
                return;
            }
            var timeout = ParseTimeout(request.Headers.GetValueOrDefault("Timeout"));
            var created = false;
            if (request.Length == 0)
            {
                if (!_locks.TryGetRefreshToken(request.Headers.GetValueOrDefault("If"), path, out var token) ||
                    _locks.Refresh(path, token, timeout) is null) throw new HttpFailure(412);
            }
            else
            {
                var depth = request.Headers.GetValueOrDefault("Depth", "infinity");
                if (depth is not ("0" or "infinity")) throw new HttpFailure(400);
                await ContinueAsync(stream, request, cancellation).ConfigureAwait(false);
                var root = ParseXml(await ReadBodyAsync(stream, request.Length, cancellation).ConfigureAwait(false));
                if (root.Name != Dav + "lockinfo" ||
                    root.Element(Dav + "lockscope")?.Elements().SingleOrDefault()?.Name != Dav + "exclusive" ||
                    root.Element(Dav + "locktype")?.Elements().SingleOrDefault()?.Name != Dav + "write") throw new HttpFailure(400);
                if (entry is null && !_locks.ValidateWrite([ParentPath(path)], request.Headers.GetValueOrDefault("If"), path))
                    throw new HttpFailure(request.Headers.ContainsKey("If") ? 412 : 423);
                var acquired = _locks.Acquire(path, depth == "infinity", root.Element(Dav + "owner"), timeout);
                if (entry is null)
                {
                    try
                    {
                        await RequireParentAsync(path, cancellation).ConfigureAwait(false);
                        await SpoolPutAsync(stream, path, 0, null, cancellation).ConfigureAwait(false);
                        created = true;
                    }
                    catch { _locks.Release(path, acquired.Token); throw; }
                }
            }
            var discovery = _locks.Discovery(path);
            var activeToken = discovery.Descendants(Dav + "locktoken").Select(element => element.Element(Dav + "href")?.Value).FirstOrDefault();
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/xml; charset=utf-8" };
            if (activeToken is not null) headers["Lock-Token"] = "<" + activeToken + ">";
            await ResponseAsync(stream, created ? 201 : 200, XmlBytes(new XElement(Dav + "prop", discovery)), cancellation, headers).ConfigureAwait(false);
        }
        catch (ExplorerWebDavLockException error) { throw new HttpFailure(error.Status); }
    }

    private async Task HandlePropertiesAsync(NetworkStream stream, Request request, ExplorerFileEntry entry, CancellationToken cancellation)
    {
        if (request.Length == 0) throw new HttpFailure(400);
        await ContinueAsync(stream, request, cancellation).ConfigureAwait(false);
        var root = ParseXml(await ReadBodyAsync(stream, request.Length, cancellation).ConfigureAwait(false));
        if (root.Name != Dav + "propertyupdate") throw new HttpFailure(400);
        var changes = new List<(XElement Property, bool Remove)>();
        foreach (var operation in root.Elements())
        {
            if (operation.Name != Dav + "set" && operation.Name != Dav + "remove") throw new HttpFailure(400);
            var containers = operation.Elements().ToArray();
            if (containers.Length != 1 || containers[0].Name != Dav + "prop") throw new HttpFailure(400);
            changes.AddRange(containers[0].Elements().Select(property => (property, operation.Name == Dav + "remove")));
            if (changes.Count > 32) throw new HttpFailure(413);
        }
        if (changes.Count == 0 || changes.Select(change => change.Property.Name).Distinct().Count() != changes.Count) throw new HttpFailure(400);
        ExplorerFileProperties properties = new();
        var failures = new Dictionary<XName, int>();
        foreach (var change in changes)
        {
            var property = change.Property;
            if (change.Remove || property.HasElements || property.Name.Namespace != Windows)
            { failures[property.Name] = 403; continue; }
            if (property.Name.LocalName == "Win32FileAttributes")
            {
                if (property.Value.Length != 8 || !uint.TryParse(property.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var attributes))
                { failures[property.Name] = 409; continue; }
                // The WebDAV view supports ordinary Windows attributes, not
                // devices, sparse/reparse, encrypted or offline file semantics.
                const uint supported = 0x01 | 0x02 | 0x04 | 0x10 | 0x20 | 0x80;
                if ((attributes & ~supported) != 0 || (attributes & 0x10) != 0 && !entry.IsDirectory)
                { failures[property.Name] = 409; continue; }
                properties = properties with { Win32Attributes = attributes };
            }
            else if (property.Name.LocalName is "Win32CreationTime" or "Win32LastAccessTime" or "Win32LastModifiedTime")
            {
                if (!DateTimeOffset.TryParseExact(property.Value, "R", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var timestamp)) { failures[property.Name] = 409; continue; }
                properties = property.Name.LocalName switch
                {
                    "Win32CreationTime" => properties with { CreationTime = timestamp.ToUniversalTime() },
                    "Win32LastAccessTime" => properties with { LastAccessTime = timestamp.ToUniversalTime() },
                    _ => properties with { LastModifiedTime = timestamp.ToUniversalTime() }
                };
            }
            else failures[property.Name] = 403;
        }
        if (failures.Count == 0)
            await DeviceMutationAsync(token => _store.UpdatePropertiesAsync(entry, properties, token), cancellation).ConfigureAwait(false);
        var statuses = changes.GroupBy(change => failures.Count == 0 ? 200 : failures.GetValueOrDefault(change.Property.Name, 424));
        var response = new XElement(Dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", Dav),
            new XElement(Dav + "response", new XElement(Dav + "href", Href(entry)), statuses.Select(group =>
                new XElement(Dav + "propstat", new XElement(Dav + "prop", group.Select(change => new XElement(change.Property.Name))),
                    new XElement(Dav + "status", $"HTTP/1.1 {group.Key} Response")))));
        await ResponseAsync(stream, 207, XmlBytes(response), cancellation, new() { ["Content-Type"] = "application/xml; charset=utf-8" }).ConfigureAwait(false);
    }

    private static int ParseTimeout(string? value)
    {
        foreach (var part in (value ?? "Second-600").Split(',').Select(part => part.Trim()))
        {
            if (part.Equals("Infinite", StringComparison.OrdinalIgnoreCase)) return 1800;
            if (part.StartsWith("Second-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(part[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                return Math.Min(seconds, 1800);
        }
        throw new HttpFailure(400);
    }

    private async Task RequireParentAsync(string path, CancellationToken cancellation)
    {
        var parent = ParentPath(path);
        var entry = await DeviceAsync(token => _store.StatAsync(parent, token), cancellation).ConfigureAwait(false);
        if (entry is not { IsDirectory: true }) throw new HttpFailure(409);
        ValidateEntry(entry, parent);
    }
    private static string ParentPath(string path) => path.LastIndexOf('/') is var slash && slash >= 0 ? path[..slash] : "";

    private static void CheckPreconditions(Request request, ExplorerFileEntry? entry)
    {
        if (request.Headers.TryGetValue("If-Match", out var match))
        {
            // Metadata-derived ETags are weak and cannot satisfy a strong
            // If-Match validator. '*' still means the resource must exist.
            if (match.Trim() != "*" || entry is null) throw new HttpFailure(412);
        }
        if (request.Headers.TryGetValue("If-None-Match", out var none) && entry is not null && MatchesEntityTag(none, Etag(entry)))
            throw new HttpFailure(412);
        if (request.Headers.TryGetValue("If-Unmodified-Since", out var unmodified) && entry is not null &&
            DateTimeOffset.TryParseExact(unmodified, "R", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) &&
            entry.Modified.ToUnixTimeSeconds() > time.ToUnixTimeSeconds()) throw new HttpFailure(412);
    }

    private string? TryResolveDestination(string value)
    {
        try
        {
            if (value.StartsWith('/')) return ResolvePath(value);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
                uri.Host is not ("127.0.0.1" or "localhost") || uri.Port != BaseUri.Port ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
            return ResolvePath(uri.AbsolutePath);
        }
        catch (HttpFailure) { return null; }
    }

    private async Task SpoolPutAsync(NetworkStream input, string path, long length, ExplorerFileEntry? expected, CancellationToken cancellation)
    {
        var directory = Directory.CreateTempSubdirectory("iPhoneMirror-ExplorerTransfer-");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.SetOwner(user);
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                directory.SetAccessControl(acl);
            }
            var spool = Path.Combine(directory.FullName, "content.upload");
            await using (var output = new FileStream(spool, FileMode.CreateNew, FileAccess.Write, FileShare.None, ReadChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[ReadChunkSize];
                while (length > 0)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), timeout.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    length -= count;
                }
            }
            await DeviceMutationAsync(token => _store.PutAsync(path, spool, expected, token), cancellation, 600).ConfigureAwait(false);
        }
        finally { try { directory.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static async Task DeviceMutationAsync(Func<CancellationToken, Task> action, CancellationToken cancellation, int seconds = 35)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        await action(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
    }
    private static async Task<byte[]> ReadBodyAsync(NetworkStream input, long length, CancellationToken cancellation)
    {
        if (length > MaximumBody) throw new HttpFailure(413);
        var body = new byte[(int)length];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await input.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
        return body;
    }
    private static Task ContinueAsync(NetworkStream output, Request request, CancellationToken cancellation) =>
        request.Headers.ContainsKey("Expect") ? output.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellation).AsTask() : Task.CompletedTask;
    private static XElement ParseXml(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBody });
        return XDocument.Load(reader).Root ?? throw new HttpFailure(400);
    }

    private string ResolvePath(string target)
    {
        if (!target.StartsWith(_rootPath, StringComparison.Ordinal) && target != _rootPath.TrimEnd('/')) throw new HttpFailure(404);
        var encoded = target.Length < _rootPath.Length ? "" : target[_rootPath.Length..];
        if (encoded.EndsWith('/')) encoded = encoded[..^1];
        if (encoded.Length == 0) return "";
        if (encoded.Length > 8192 || encoded.Contains('?') || encoded.Contains('#')) throw new HttpFailure(400);
        return string.Join('/', encoded.Split('/').Select(DecodeSegment));
    }

    private static string DecodeSegment(string raw)
    {
        using var bytes = new MemoryStream(raw.Length);
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character == '%')
            {
                if (index + 2 >= raw.Length || !byte.TryParse(raw.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                    throw new HttpFailure(400);
                bytes.WriteByte(value); index += 2;
            }
            else if (character is >= '!' and <= '~') bytes.WriteByte((byte)character);
            else throw new HttpFailure(400);
        }
        var segment = Utf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        if (segment.Length == 0 || segment is "." or ".." || segment.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '\0'))
            throw new HttpFailure(400);
        return segment;
    }

    private static void ValidateEntry(ExplorerFileEntry entry, string expectedPath)
    {
        if (entry.Path != expectedPath || entry.Length < 0 || entry.Path.Length > 2048 ||
            entry.Path.Split('/').Any(part => part is "." or ".." || part.Any(character => char.IsControl(character) || character is '\\' or ':')) ||
            (entry.Path.Length > 0 && entry.Path.Split('/').Any(string.IsNullOrEmpty))) throw new IOException("Invalid file metadata.");
    }

    private XElement PropertyResponse(ExplorerFileEntry entry, PropertyQuery query)
    {
        var properties = Properties(entry);
        var selected = query.Names ?? properties.Select(property => property.Name).ToArray();
        var found = selected.Select(name => properties.FirstOrDefault(property => property.Name == name))
            .Where(property => property is not null).Select(property => query.NamesOnly ? new XElement(property!.Name) : property!);
        var missing = selected.Where(name => properties.All(property => property.Name != name)).Select(name => new XElement(name)).ToArray();
        var result = new XElement(Dav + "response", new XElement(Dav + "href", Href(entry)),
            new XElement(Dav + "propstat", new XElement(Dav + "prop", found), new XElement(Dav + "status", "HTTP/1.1 200 OK")));
        if (missing.Length > 0) result.Add(new XElement(Dav + "propstat", new XElement(Dav + "prop", missing), new XElement(Dav + "status", "HTTP/1.1 404 Not Found")));
        return result;
    }

    private XElement[] Properties(ExplorerFileEntry entry) =>
    [
        new(Dav + "displayname", entry.Name),
        new(Dav + "resourcetype", entry.IsDirectory ? new XElement(Dav + "collection") : null),
        new(Dav + "getcontentlength", entry.IsDirectory ? 0 : entry.Length),
        new(Dav + "getcontenttype", entry.IsDirectory ? "httpd/unix-directory" : "application/octet-stream"),
        new(Dav + "getlastmodified", entry.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
        new(Dav + "creationdate", (entry.WindowsProperties?.CreationTime ?? entry.Modified).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
        new(Dav + "getetag", Etag(entry)),
        new(Windows + "Win32CreationTime", (entry.WindowsProperties?.CreationTime ?? entry.Modified).UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
        new(Windows + "Win32LastAccessTime", (entry.WindowsProperties?.LastAccessTime ?? entry.Modified).UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
        new(Windows + "Win32LastModifiedTime", entry.Modified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture)),
        new(Windows + "Win32FileAttributes", ((entry.WindowsProperties?.Win32Attributes ?? 0) | (entry.IsDirectory ? 0x10u : 0)).ToString("x8", CultureInfo.InvariantCulture)),
        _store.IsReadOnly ? new(Dav + "supportedlock") : new(Dav + "supportedlock", new XElement(Dav + "lockentry",
            new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")), new XElement(Dav + "locktype", new XElement(Dav + "write")))),
        _locks.Discovery(entry.Path),
        new(Dav + "current-user-privilege-set", new XElement(Dav + "privilege", new XElement(Dav + "read")),
            _store.IsReadOnly ? null : new XElement(Dav + "privilege", new XElement(Dav + "write")))
    ];
    private string HrefByPath(string path) => _rootPath + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private string Href(ExplorerFileEntry entry) => HrefByPath(entry.Path) +
        (entry.IsDirectory && entry.Path.Length > 0 ? "/" : "");
    private static string Etag(ExplorerFileEntry entry) => $"W/\"{entry.Length:x}-{entry.Modified.UtcTicks:x}\"";
    private static bool MatchesEntityTag(string? supplied, string current) => supplied is not null &&
        supplied.Split(',').Select(value => value.Trim()).Any(value => value == "*" ||
            (value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value) == current[2..]);
    private static byte[] XmlBytes(XElement root) => Encoding.UTF8.GetBytes(new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString(SaveOptions.DisableFormatting));

    private static async Task<T> DeviceAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellation, int seconds = 35)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return await action(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    private static async Task MonitorDisconnectAsync(TcpClient client, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(200, lifetime.Token).ConfigureAwait(false);
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                { lifetime.Cancel(); return; }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private sealed record Request(string Method, string Target, Dictionary<string, string> Headers, long Length);
    private static async Task<Request?> ReadRequestAsync(NetworkStream input, CancellationToken cancellation)
    {
        using var header = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, cancellation).ConfigureAwait(false) == 0) return null;
            header.WriteByte(one[0]);
            if (header.Length > MaximumHeaders) throw new HttpFailure(431);
            if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        var lines = Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[0].Any(character => character is < 'A' or > 'Z') ||
            !(first[1].StartsWith('/') || first[0] == "OPTIONS" && first[1] == "*") || first[2] is not ("HTTP/1.1" or "HTTP/1.0")) throw new HttpFailure(400);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]) || line[..colon].Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
                !fields.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new HttpFailure(400);
        }
        if (fields.ContainsKey("Transfer-Encoding")) throw new HttpFailure(400);
        var length = 0L;
        if (fields.TryGetValue("Content-Length", out var contentLength) &&
            (!long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0))
            throw new HttpFailure(413);
        if (fields.TryGetValue("Expect", out var expectation))
        {
            if (!expectation.Equals("100-continue", StringComparison.OrdinalIgnoreCase)) throw new HttpFailure(417);
        }
        return new(first[0], first[1], fields, length);
    }

    private Dictionary<string, string> DavHeaders() => new()
    { ["DAV"] = _store.IsReadOnly ? "1" : "1, 2", ["MS-Author-Via"] = "DAV", ["Allow"] = _store.IsReadOnly ? ReadMethods : WriteMethods };
    private Task EmptyAsync(NetworkStream output, int status, CancellationToken cancellation, Dictionary<string, string>? headers = null) =>
        WriteHeadersAsync(output, status, 0, headers, cancellation);
    private async Task ResponseAsync(NetworkStream output, int status, byte[] body, CancellationToken cancellation, Dictionary<string, string>? headers = null)
    {
        await WriteHeadersAsync(output, status, body.Length, headers, cancellation).ConfigureAwait(false);
        await output.WriteAsync(body, cancellation).ConfigureAwait(false);
    }
    private async Task WriteHeadersAsync(NetworkStream output, int status, long length, Dictionary<string, string>? headers, CancellationToken cancellation)
    {
        if (_observations.TryGetValue(output, out var observation)) observation.Status = status;
        var text = new StringBuilder($"HTTP/1.1 {status} Response\r\nConnection: close\r\nContent-Length: {length}\r\nCache-Control: no-store\r\nDate: {DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)}\r\n");
        foreach (var header in headers ?? []) text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        text.Append("\r\n");
        await output.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), cancellation).ConfigureAwait(false);
    }
    private static bool TryRange(string range, long length, out long offset, out long count)
    {
        offset = 0; count = 0;
        if (length == 0 || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || range.Contains(',')) return false;
        var fields = range[6..].Split('-');
        if (fields.Length != 2) return false;
        if (fields[0].Length == 0)
        {
            if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            count = Math.Min(suffix, length); offset = length - count; return true;
        }
        if (!long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= length) return false;
        var end = length - 1;
        if (fields[1].Length > 0 && (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < offset)) return false;
        end = Math.Min(end, length - 1); count = end - offset + 1; return true;
    }
    private sealed class HttpFailure(int status, Dictionary<string, string>? headers = null) : Exception
    { internal int Status { get; } = status; internal Dictionary<string, string>? Headers { get; } = headers; }
    private sealed class RequestObservation(string method)
    { internal string Method { get; } = method; internal int Status { get; set; } }
    private sealed record PropertyQuery(XName[]? Names, bool NamesOnly)
    {
        internal static PropertyQuery Parse(byte[] body)
        {
            if (body.Length == 0) return new(null, false);
            using var input = new MemoryStream(body);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBody });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != Dav + "propfind" || root.Elements().Count() != 1) throw new HttpFailure(400);
            var selection = root.Elements().Single();
            if (selection.Name == Dav + "allprop") return new(null, false);
            if (selection.Name == Dav + "propname") return new(null, true);
            if (selection.Name == Dav + "prop") return new(selection.Elements().Select(property => property.Name).Distinct().ToArray(), false);
            throw new HttpFailure(400);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
        foreach (var client in _connections.Keys) client.Dispose();
        await _accepting.ConfigureAwait(false);
        if (Volatile.Read(ref _active) == 0) _drained.TrySetResult();
        try { await _drained.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { /* A foreign store may ignore cancellation; its route must be generation guarded. */ }
    }
}
