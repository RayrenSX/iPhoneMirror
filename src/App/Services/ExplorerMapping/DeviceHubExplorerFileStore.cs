using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services.ExplorerMapping;

// The callback owns dispatcher access and the pinned bridge/generation. This
// adapter never discovers another phone and never retries a device mutation.
internal sealed class DeviceHubExplorerFileStore : IExplorerFileStore
{
    private const int ChunkSize = 192 * 1024;
    private const int PageSize = 500;
    private const int MaximumEntries = 100_000;
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _request;
    private readonly string _root;
    private readonly string? _bundleId;
    private readonly string? _domain;
    private readonly string? _identifier;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _queueTimeout;
    private readonly ConditionalWeakTable<ExplorerFileEntry, Snapshot> _snapshots = new();
    // AFC has no Windows creation/access/archive properties. These values
    // belong only to this mapping session and expire with its phone snapshot.
    private readonly Dictionary<string, WindowsOverlay> _windowsProperties = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readOnlyPaths = new(StringComparer.Ordinal);
    private int _admitted;
    private readonly ExplorerTreeCache? _treeCache;
    private readonly string? _cacheScope;
    private readonly Action? _foregroundAccess;

    internal DeviceHubExplorerFileStore(Func<string, object?, CancellationToken, Task<JsonElement>> request,
        TimeSpan? queueTimeout = null, string root = "media", string? bundleId = null,
        string? domain = null, string? identifier = null, ExplorerTreeCache? treeCache = null,
        string? cacheScope = null, Action? foregroundAccess = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        if (root is not ("media" or "crashes" or "documents" or "container" or "coredevice" or "system" or "symbols") ||
            (root is "documents" or "container") != (bundleId is not null) || (root == "coredevice") != (domain is not null))
            throw new ArgumentException("Choose a phone file service and its document application.", nameof(root));
        _root = root;
        _bundleId = bundleId;
        _domain = domain;
        _identifier = identifier;
        (_treeCache, _cacheScope, _foregroundAccess) = (treeCache, cacheScope, foregroundAccess);
        _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(15);
        if (_queueTimeout <= TimeSpan.Zero || _queueTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(queueTimeout));
    }

    public bool IsReadOnly => _root is "crashes" or "coredevice" or "system" or "symbols";

    public Task<ExplorerFileEntry?> StatAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidatePath(path, allowRoot: true);
        if (_treeCache is not null && _cacheScope is not null && _treeCache.TryDirectoryMetadata(_cacheScope, path, out var cached))
        {
            try
            {
                var entry = ParseEntry(cached, path, cached: true);
                _treeCache.RequestRefresh(_cacheScope, path);
                return Task.FromResult<ExplorerFileEntry?>(entry);
            }
            catch (IOException) { _treeCache.Invalidate(_cacheScope, path); }
        }
        return StatFreshAsync(path, cancellation);
    }

    public Task<ExplorerFileEntry?> StatFreshAsync(string path, CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(path, allowRoot: true);
        try
        {
            var value = await RequestAsync("stat_file", new { root = "media", path }, cancellation).ConfigureAwait(false);
            if (value.ValueKind != JsonValueKind.Null)
            {
                var entry = ParseEntry(value, path);
                RememberStat(path, value, entry.IsReadOnly);
                return entry;
            }
            InvalidateTree(path);
            ClearProperties(path);
            return null;
        }
        catch (FileNotFoundException) { InvalidateTree(path); ClearProperties(path); return null; }
    }, cancellation);

    public Task<IReadOnlyList<ExplorerFileEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidatePath(path, allowRoot: true);
        if (_treeCache is not null && _cacheScope is not null && _treeCache.TryDirectory(_cacheScope, path, out var cached))
        {
            try
            {
                IReadOnlyList<ExplorerFileEntry> entries = cached.Select(item => ParseEntry(item, item.GetProperty("path").GetString()!, cached: true)).ToArray();
                _treeCache.RequestRefresh(_cacheScope, path);
                return Task.FromResult(entries);
            }
            catch (IOException) { _treeCache.Invalidate(_cacheScope, path); }
        }
        return RunAsync(() => ListLiveAsync(path, cancellation), cancellation);
    }

    internal Task RefreshDirectoryAsync(string path, CancellationToken cancellation) =>
        RunAsync(async () =>
        {
            var value = await RequestAsync("stat_file", new { root = "media", path }, cancellation).ConfigureAwait(false);
            if (value.ValueKind == JsonValueKind.Null) { InvalidateTree(path); throw new DirectoryNotFoundException("The phone directory no longer exists."); }
            var entry = ParseEntry(value, path);
            RememberStat(path, value, entry.IsReadOnly);
            if (!entry.IsDirectory) { InvalidateTree(path); throw new DirectoryNotFoundException("The phone path is no longer a directory."); }
            return await ListLiveAsync(path, cancellation).ConfigureAwait(false);
        }, cancellation, background: true);

    private async Task<IReadOnlyList<ExplorerFileEntry>> ListLiveAsync(string path, CancellationToken cancellation)
    {
        ValidatePath(path, allowRoot: true);
        var entries = new List<ExplorerFileEntry>();
        var metadata = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0;
        int? total = null;
        while (true)
        {
            var page = await RequestAsync("list_dir", new { root = "media", path, offset, limit = PageSize }, cancellation).ConfigureAwait(false);
            if (page.ValueKind != JsonValueKind.Object || String(page, "path") != path ||
                !page.TryGetProperty("total", out var totalValue) || !totalValue.TryGetInt32(out var currentTotal) ||
                currentTotal < 0 || currentTotal > MaximumEntries ||
                !page.TryGetProperty("entries", out var children) || children.ValueKind != JsonValueKind.Array ||
                children.GetArrayLength() > PageSize || !page.TryGetProperty("nextOffset", out var next))
                throw InvalidResponse();
            if (total is not null && total != currentTotal)
                throw new ExplorerFileConflictException("The phone directory changed during enumeration.");
            total = currentTotal;
            foreach (var child in children.EnumerateArray())
            {
                var name = String(child, "name");
                if (name.Length == 0 || name.Contains('/')) throw InvalidResponse();
                var childPath = path.Length == 0 ? name : path + "/" + name;
                ValidatePath(childPath, allowRoot: false);
                if (String(child, "path") != childPath || !seen.Add(childPath)) throw InvalidResponse();
                // The phone listing reports links and other special entries as
                // 'other'. They must never become traversable mapped entries.
                var type = String(child, "type");
                if (type == "other") { ClearProperties(childPath); continue; }
                var entry = ParseEntry(child, childPath);
                entries.Add(entry);
                if (_treeCache is not null) metadata.Add(CacheMetadata(child, entry.IsReadOnly));
                if (entries.Count > MaximumEntries) throw InvalidResponse();
            }
            if (next.ValueKind == JsonValueKind.Null)
            {
                var prefix = path.Length == 0 ? "" : path + "/";
                foreach (var known in _windowsProperties.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length).ToArray())
                {
                    var child = prefix + known[prefix.Length..].Split('/')[0];
                    if (!seen.Contains(child)) ClearProperties(child);
                }
                if (_treeCache is not null && _cacheScope is not null) _treeCache.RememberDirectory(_cacheScope, path, metadata.ToArray());
                return entries;
            }
            if (!next.TryGetInt32(out var nextOffset) || nextOffset != offset + PageSize ||
                nextOffset >= currentTotal || nextOffset > MaximumEntries) throw InvalidResponse();
            offset = nextOffset;
        }
    }

    public Task<ReadOnlyMemory<byte>> ReadAsync(ExplorerFileEntry expected, long offset, int count,
        CancellationToken cancellation) => RunAsync<ReadOnlyMemory<byte>>(async () =>
    {
        var snapshot = Expected(expected);
        if (expected.IsDirectory) throw new UnauthorizedAccessException("Directories cannot be read as files.");
        if (offset < 0 || offset > expected.Length || count < 0 || count > ChunkSize)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var requested = (int)Math.Min(count, expected.Length - offset);
        if (requested == 0) return ReadOnlyMemory<byte>.Empty;
        var value = await RequestAsync("read_file", new { root = "media", path = expected.Path, offset, length = requested }, cancellation).ConfigureAwait(false);
        if (value.ValueKind != JsonValueKind.Object || String(value, "path") != expected.Path ||
            !value.TryGetProperty("offset", out var replyOffset) || !replyOffset.TryGetInt64(out var actualOffset) || actualOffset != offset ||
            !value.TryGetProperty("size", out var size) || !size.TryGetInt64(out var actualSize) || actualSize != expected.Length ||
            !value.TryGetProperty("modified", out var modified) || !SameModified(modified, snapshot.Modified))
            throw new ExplorerFileConflictException("The phone file changed during reading.");
        var encoded = String(value, "data");
        if (encoded.Length > ((requested + 2) / 3) * 4) throw InvalidResponse();
        byte[] data;
        try { data = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw InvalidResponse(); }
        if (data.Length == 0 || data.Length > requested || !value.TryGetProperty("eof", out var eof) ||
            eof.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            eof.GetBoolean() != (offset + data.Length >= expected.Length)) throw InvalidResponse();
        return data;
    }, cancellation);

    public Task PutAsync(string path, string localSourcePath, ExplorerFileEntry? expected, CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(path, allowRoot: false);
        var previous = ExpectedAt(expected, path);
        ValidateSpool(localSourcePath);
        await RequestAsync("write_mapped_file", new { root = "media", path, sourcePath = localSourcePath, expected = previous }, cancellation).ConfigureAwait(false);
        ClearProperties(path);
        return true;
    }, cancellation);

    public Task MakeDirectoryAsync(string path, CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(path, allowRoot: false);
        await RequestAsync("make_directory", new { root = "media", path }, cancellation).ConfigureAwait(false);
        ClearProperties(path);
        return true;
    }, cancellation);

    public Task MoveAsync(ExplorerFileEntry expectedSource, string destination, ExplorerFileEntry? expectedDestination,
        CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(expectedSource.Path, allowRoot: false);
        ValidatePath(destination, allowRoot: false);
        var previous = Expected(expectedSource).Metadata;
        var target = ExpectedAt(expectedDestination, destination);
        var moved = await RequestAsync("move_file", new { root = "media", path = expectedSource.Path, destination, expected = previous, destinationExpected = target }, cancellation).ConfigureAwait(false);
        var retained = _windowsProperties.Where(item => IsRooted(item.Key, expectedSource.Path)).ToArray();
        ClearProperties(expectedSource.Path);
        ClearProperties(destination);
        foreach (var item in retained) _windowsProperties[destination + item.Key[expectedSource.Path.Length..]] = item.Value;
        if (moved.ValueKind == JsonValueKind.Object && moved.TryGetProperty("modified", out _))
        {
            // A directory rename may update the directory's own mtime. Its
            // children retain their snapshots and session metadata unchanged.
            var overlay = _windowsProperties.GetValueOrDefault(destination);
            _windowsProperties.Remove(destination);
            var fresh = ParseEntry(moved, destination);
            if (overlay is not null && MetadataMatches(previous, overlay.Snapshot.Metadata))
                _windowsProperties[destination] = new(Expected(fresh), overlay.Properties with { LastModifiedTime = fresh.Modified });
        }
        return true;
    }, cancellation);

    public Task DeleteAsync(ExplorerFileEntry expected, CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(expected.Path, allowRoot: false);
        var previous = Expected(expected).Metadata;
        await RequestAsync("delete_mapped_file", new { root = "media", path = expected.Path, expected = previous, recursive = true }, cancellation).ConfigureAwait(false);
        ClearProperties(expected.Path);
        return true;
    }, cancellation);

    public Task UpdatePropertiesAsync(ExplorerFileEntry expected, ExplorerFileProperties properties,
        CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(expected.Path, allowRoot: false);
        if (IsReadOnly || expected.IsReadOnly || _readOnlyPaths.Any(root => IsRooted(expected.Path, root)))
            throw new UnauthorizedAccessException("The mapped phone file or folder is read-only.");
        var previous = Expected(expected);
        if (properties.Win32Attributes is { } attributes &&
            attributes is not (0 or 32 or 128) && !(expected.IsDirectory && attributes == 16))
            throw new UnauthorizedAccessException("These Windows file attributes are not supported by the phone mapping.");
        long? modifiedNs = properties.LastModifiedTime is { } modified ? EpochNanoseconds(modified) : null;
        JsonElement value;
        try { value = await RequestAsync("stat_file", new { root = "media", path = expected.Path }, cancellation).ConfigureAwait(false); }
        catch (FileNotFoundException) { ClearProperties(expected.Path); throw; }
        if (value.ValueKind == JsonValueKind.Null)
        {
            ClearProperties(expected.Path);
            throw new FileNotFoundException("The phone file was not found.");
        }
        var fresh = ParseEntry(value, expected.Path);
        if (fresh.IsReadOnly) throw new UnauthorizedAccessException("The mapped phone file or folder is read-only.");
        if (!MetadataMatches(previous.Metadata, Expected(fresh).Metadata))
            throw new ExplorerFileConflictException("The phone file changed before its metadata update.");
        var existing = fresh.WindowsProperties!;
        if (modifiedNs is { } nanoseconds)
        {
            var updated = await RequestAsync("set_mapped_file_time", new
            { root = "media", path = expected.Path, expected = previous.Metadata, modifiedNs = nanoseconds }, cancellation).ConfigureAwait(false);
            // Parse the response before acknowledging; all future preconditions
            // must refer to the actual mtime returned by the phone, not a guess.
            _windowsProperties.Remove(expected.Path);
            fresh = ParseEntry(updated, expected.Path);
        }
        var merged = new ExplorerFileProperties(properties.CreationTime ?? existing.CreationTime,
            properties.LastAccessTime ?? existing.LastAccessTime, fresh.Modified,
            properties.Win32Attributes ?? existing.Win32Attributes);
        _windowsProperties[expected.Path] = new(Expected(fresh), merged);
        InvalidateTree(expected.Path);
        return true;
    }, cancellation);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellation, bool background = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!background) _foregroundAccess?.Invoke();
        if (Interlocked.Increment(ref _admitted) > 32)
        {
            Interlocked.Decrement(ref _admitted);
            throw new IOException("The phone file service queue is full.");
        }
        var entered = false;
        try
        {
            entered = await _gate.WaitAsync(_queueTimeout, cancellation).ConfigureAwait(false);
            if (!entered) throw new IOException("The phone file service queue timed out.");
            cancellation.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            if (entered) _gate.Release();
            Interlocked.Decrement(ref _admitted);
        }
    }

    private async Task<JsonElement> RequestAsync(string action, object arguments, CancellationToken cancellation)
    {
        var scoped = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var writing = action is not ("stat_file" or "list_dir" or "read_file");
        var path = scoped["path"].GetString()!;
        if (writing && (IsReadOnly || _readOnlyPaths.Any(root => IsRooted(path, root)) ||
            action == "move_file" && _readOnlyPaths.Any(root => IsRooted(scoped["destination"].GetString()!, root))))
            throw new UnauthorizedAccessException("The mapped phone file or folder is read-only.");
        scoped["root"] = JsonSerializer.SerializeToElement(_root);
        scoped["mappedNames"] = JsonSerializer.SerializeToElement(true);
        if (_bundleId is not null) scoped["bundleId"] = JsonSerializer.SerializeToElement(_bundleId);
        if (_domain is not null)
        {
            scoped["domain"] = JsonSerializer.SerializeToElement(_domain);
            scoped["identifier"] = JsonSerializer.SerializeToElement(_identifier ?? "");
        }
        // Refresh crash reports only at the start of a root enumeration, not
        // during a file read or between pages of the same listing.
        if (_root == "crashes" && action == "list_dir" && scoped["path"].GetString() == "" && scoped["offset"].GetInt32() == 0)
            scoped["refresh"] = JsonSerializer.SerializeToElement(true);
        try { return await _request(action, scoped, cancellation).ConfigureAwait(false); }
        catch (AutomationException error)
        {
            if (writing && error.Code is "FILE_ACCESS_DENIED" or "ACCESS_DENIED") _readOnlyPaths.Add(path);
            throw error.Code switch
            {
                "FILE_MAPPING_CONFLICT" or "FILE_CHANGED" => new ExplorerFileConflictException("The phone file changed; refresh and retry."),
                "FILE_NOT_FOUND" => new FileNotFoundException("The phone file was not found."),
                "FILE_ACCESS_DENIED" or "ACCESS_DENIED" => new UnauthorizedAccessException("The phone denied file access."),
                "CONTROL_LOCKED" or "MANAGEMENT_BUSY" => new IOException("The phone file service is busy."),
                _ => new IOException("The phone file operation failed."),
            };
        }
        finally
        {
            if (writing)
            {
                InvalidateTree(path);
                if (action == "move_file") InvalidateTree(scoped["destination"].GetString()!);
            }
        }
    }

    private void InvalidateTree(string path)
    {
        if (_treeCache is not null && _cacheScope is not null) _treeCache.Invalidate(_cacheScope, path);
    }

    private void RememberStat(string path, JsonElement value, bool readOnly)
    {
        if (_treeCache is not null && _cacheScope is not null) _treeCache.RememberStat(_cacheScope, path, CacheMetadata(value, readOnly));
    }

    private static JsonElement CacheMetadata(JsonElement value, bool readOnly)
    {
        var fields = value.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone());
        fields["readOnly"] = JsonSerializer.SerializeToElement(readOnly);
        return JsonSerializer.SerializeToElement(fields);
    }

    private ExplorerFileEntry ParseEntry(JsonElement value, string path, bool cached = false)
    {
        ValidatePath(path, allowRoot: true);
        if (value.ValueKind != JsonValueKind.Object || String(value, "path") != path ||
            String(value, "name") != path[(path.LastIndexOf('/') + 1)..] ||
            !value.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length) || length < 0 ||
            !value.TryGetProperty("modified", out var modified)) throw InvalidResponse();
        var type = String(value, "type");
        if (type is not ("directory" or "file")) throw new UnauthorizedAccessException("Links and special files are not mapped.");
        var snapshot = new Snapshot(JsonSerializer.SerializeToElement(new { type, size = length, modified }), modified.Clone());
        var timestamp = ParseModified(modified);
        var properties = new ExplorerFileProperties(timestamp, timestamp, timestamp, type == "directory" ? 16u : 32u);
        if (cached)
        {
            var locked = IsReadOnly || value.TryGetProperty("readOnly", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (locked) properties = properties with { Win32Attributes = properties.Win32Attributes | 1u };
            var preview = new ExplorerFileEntry(path, String(value, "name"), type == "directory", length, timestamp, properties, locked);
            _snapshots.Add(preview, snapshot);
            return preview;
        }
        if (value.TryGetProperty("readOnly", out var access))
        {
            if (access.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
            if (access.GetBoolean()) _readOnlyPaths.Add(path);
            else _readOnlyPaths.Remove(path);
        }
        if (_windowsProperties.TryGetValue(path, out var overlay))
        {
            if (MetadataMatches(snapshot.Metadata, overlay.Snapshot.Metadata)) properties = overlay.Properties;
            else if (type == "directory") _windowsProperties.Remove(path);
            else ClearProperties(path);
        }
        var readOnly = IsReadOnly || _readOnlyPaths.Any(root => IsRooted(path, root));
        if (readOnly) properties = properties with { Win32Attributes = properties.Win32Attributes | 1u };
        var entry = new ExplorerFileEntry(path, String(value, "name"), type == "directory", length, timestamp, properties, readOnly);
        // DateTimeOffset cannot represent every nanosecond reported by AFC.
        // Keep the exact wire snapshot for preconditions and every read block.
        _snapshots.Add(entry, snapshot);
        return entry;
    }

    private Snapshot Expected(ExplorerFileEntry entry) => _snapshots.TryGetValue(entry, out var snapshot)
        ? snapshot : throw new ExplorerFileConflictException("Refresh the phone file snapshot before changing or reading it.");

    private JsonElement? ExpectedAt(ExplorerFileEntry? entry, string path)
    {
        if (entry is null) return null;
        if (entry.Path != path) throw new ExplorerFileConflictException("The phone file snapshot belongs to a different file.");
        return Expected(entry).Metadata;
    }

    private static bool SameModified(JsonElement actual, JsonElement expected) => actual.ValueKind == expected.ValueKind &&
        (actual.ValueKind == JsonValueKind.String ? actual.GetString() == expected.GetString() : actual.GetRawText() == expected.GetRawText());

    private static bool MetadataMatches(JsonElement actual, JsonElement expected) =>
        String(actual, "type") == String(expected, "type") &&
        actual.GetProperty("size").GetInt64() == expected.GetProperty("size").GetInt64() &&
        SameModified(actual.GetProperty("modified"), expected.GetProperty("modified"));

    private static bool IsRooted(string path, string root) => root.Length == 0 || path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    private void ClearProperties(string path)
    {
        foreach (var key in _windowsProperties.Keys.Where(key => IsRooted(key, path)).ToArray()) _windowsProperties.Remove(key);
    }

    private static long EpochNanoseconds(DateTimeOffset value)
    {
        try
        {
            var nanoseconds = checked((value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
            if (nanoseconds >= 0) return nanoseconds;
        }
        catch (OverflowException) { }
        throw new UnauthorizedAccessException("The phone does not support this file timestamp.");
    }

    private static DateTimeOffset ParseModified(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var seconds))
        {
            try { return DateTimeOffset.UnixEpoch.AddTicks(decimal.ToInt64(decimal.Truncate(seconds * TimeSpan.TicksPerSecond))); }
            catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException) { }
        }
        throw InvalidResponse();
    }

    private static string String(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw InvalidResponse();

    internal static void ValidatePath(string path, bool allowRoot)
    {
        if (path is null || path.Length > 2048 || path.Length == 0 && !allowRoot || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || path.Length > 0 && path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || Encoding.UTF8.GetByteCount(part) > 255))
            throw new UnauthorizedAccessException("The mapped phone path is invalid.");
    }

    private static void ValidateSpool(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new UnauthorizedAccessException("A private transfer file is required.");
        var file = new FileInfo(path);
        var directory = file.Directory;
        if (file.Name != "content.upload" || directory is null || !directory.Name.StartsWith("iPhoneMirror-ExplorerTransfer-", StringComparison.Ordinal) ||
            !string.Equals(directory.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("A private transfer file is required.");
    }

    private static IOException InvalidResponse() => new("The phone returned invalid file metadata.");
    private sealed record Snapshot(JsonElement Metadata, JsonElement Modified);
    private sealed record WindowsOverlay(Snapshot Snapshot, ExplorerFileProperties Properties);
}
