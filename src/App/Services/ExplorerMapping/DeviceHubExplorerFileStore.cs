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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _queueTimeout;
    private readonly ConditionalWeakTable<ExplorerFileEntry, Snapshot> _snapshots = new();
    // AFC has no Windows creation/access/archive properties. These values
    // belong only to this mapping session and expire with its phone snapshot.
    private readonly Dictionary<string, WindowsOverlay> _windowsProperties = new(StringComparer.Ordinal);
    private int _admitted;

    internal DeviceHubExplorerFileStore(Func<string, object?, CancellationToken, Task<JsonElement>> request,
        TimeSpan? queueTimeout = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(15);
        if (_queueTimeout <= TimeSpan.Zero || _queueTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(queueTimeout));
    }

    public bool IsReadOnly => false;

    public Task<ExplorerFileEntry?> StatAsync(string path, CancellationToken cancellation) => RunAsync(async () =>
    {
        ValidatePath(path, allowRoot: true);
        try
        {
            var value = await RequestAsync("stat_file", new { root = "media", path }, cancellation).ConfigureAwait(false);
            if (value.ValueKind != JsonValueKind.Null) return ParseEntry(value, path);
            ClearProperties(path);
            return null;
        }
        catch (FileNotFoundException) { ClearProperties(path); return null; }
    }, cancellation);

    public Task<IReadOnlyList<ExplorerFileEntry>> ListAsync(string path, CancellationToken cancellation) => RunAsync<IReadOnlyList<ExplorerFileEntry>>(async () =>
    {
        ValidatePath(path, allowRoot: true);
        var entries = new List<ExplorerFileEntry>();
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
                entries.Add(ParseEntry(child, childPath));
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
                return entries;
            }
            if (!next.TryGetInt32(out var nextOffset) || nextOffset != offset + PageSize ||
                nextOffset >= currentTotal || nextOffset > MaximumEntries) throw InvalidResponse();
            offset = nextOffset;
        }
    }, cancellation);

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
        return true;
    }, cancellation);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
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
        try { return await _request(action, arguments, cancellation).ConfigureAwait(false); }
        catch (AutomationException error)
        {
            throw error.Code switch
            {
                "FILE_MAPPING_CONFLICT" or "FILE_CHANGED" => new ExplorerFileConflictException("The phone file changed; refresh and retry."),
                "FILE_NOT_FOUND" => new FileNotFoundException("The phone file was not found."),
                "FILE_ACCESS_DENIED" or "ACCESS_DENIED" => new UnauthorizedAccessException("The phone denied file access."),
                "CONTROL_LOCKED" or "MANAGEMENT_BUSY" => new IOException("The phone file service is busy."),
                _ => new IOException("The phone file operation failed."),
            };
        }
    }

    private ExplorerFileEntry ParseEntry(JsonElement value, string path)
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
        if (_windowsProperties.TryGetValue(path, out var overlay))
        {
            if (MetadataMatches(snapshot.Metadata, overlay.Snapshot.Metadata)) properties = overlay.Properties;
            else if (type == "directory") _windowsProperties.Remove(path);
            else ClearProperties(path);
        }
        var entry = new ExplorerFileEntry(path, String(value, "name"), type == "directory", length, timestamp, properties);
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

    private static void ValidatePath(string path, bool allowRoot)
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
