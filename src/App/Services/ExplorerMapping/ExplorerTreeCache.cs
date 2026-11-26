using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IPhoneMirror.App.Services.ExplorerMapping;

// Only the tree and exact wire metadata are saved. No file contents, tunnel
// addresses, leases, handles or Windows overlays survive a mapping session.
internal sealed class ExplorerTreeCache
{
    internal sealed record DirectorySnapshot(string Scope, string Path, DateTimeOffset Saved,
        JsonElement? Self, JsonElement[]? Entries);
    private sealed record Envelope(int Version, string DeviceKey, JsonElement[] Roots, DirectorySnapshot[] Directories);
    private const int MaximumDirectories = 1024;
    private const int MaximumEntries = 50_000;
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly string _path;
    private readonly string _deviceKey;
    private readonly Dictionary<string, JsonElement> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectorySnapshot> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _checked = new(StringComparer.Ordinal);
    private readonly Queue<(string Scope, string Path)> _refresh = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private long _version;
    private long _savedVersion;
    private DateTimeOffset _lastSave;

    internal ExplorerTreeCache(string directory, string deviceIdentity, string systemIdentity = "")
    {
        var identity = deviceIdentity.Replace("-", "").Trim().ToLowerInvariant();
        _deviceKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("tree-v1\n" + identity + "\n" + systemIdentity)));
        _path = System.IO.Path.Combine(directory, _deviceKey + ".json");
        Load();
    }

    internal JsonElement[] Roots { get { lock (_sync) return _roots.Values.ToArray(); } }

    internal void Clear()
    {
        lock (_sync)
        {
            _roots.Clear(); _directories.Clear(); _checked.Clear(); _refresh.Clear(); _queued.Clear();
            _version++;
        }
    }

    internal static string ScopeKey(JsonElement root)
    {
        string Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("Invalid cached service.");
        var kind = Field("root");
        if (kind is not ("media" or "crashes" or "system" or "symbols" or "container" or "documents" or "coredevice"))
            throw new IOException("Unknown cached service.");
        var bundle = Field("bundleId");
        var domain = Field("domain");
        var id = Field("identifier");
        if ((kind is "documents" or "container") != (bundle.Length > 0) || (kind == "coredevice") != (domain.Length > 0) ||
            kind == "coredevice" && domain is not ("temporary" or "systemCrashLogs" or "appDataContainer" or "appGroupDataContainer") ||
            new[] { bundle, domain, id }.Any(value => value.Length > 255 || value.Any(char.IsControl)))
            throw new IOException("Invalid cached service identity.");
        return string.Join('\n', kind, bundle, domain, id);
    }

    internal void RememberRoot(JsonElement root)
    {
        var key = ScopeKey(root);
        lock (_sync)
        {
            if (_roots.Count >= 10_000 && !_roots.ContainsKey(key)) return;
            if (_roots.TryGetValue(key, out var previous) && previous.GetRawText() == root.GetRawText()) return;
            _roots[key] = root.Clone();
            _version++;
        }
    }

    internal bool TryDirectory(string scope, string path, out JsonElement[] entries)
    {
        lock (_sync)
        {
            if (_directories.TryGetValue(Key(scope, path), out var snapshot) && snapshot.Entries is { } items)
            { entries = items; return true; }
        }
        entries = [];
        return false;
    }

    internal bool TryDirectoryMetadata(string scope, string path, out JsonElement metadata)
    {
        lock (_sync)
        {
            if (_directories.TryGetValue(Key(scope, path), out var snapshot) && snapshot.Self is { } self)
            { metadata = self; return true; }
            var parent = Parent(path);
            if (path.Length > 0 && _directories.TryGetValue(Key(scope, parent), out snapshot) && snapshot.Entries is { } entries)
                foreach (var item in entries)
                    if (item.GetProperty("path").GetString() == path && item.GetProperty("type").GetString() == "directory")
                    { metadata = item; return true; }
        }
        metadata = default;
        return false;
    }

    internal void RememberDirectory(string scope, string path, JsonElement[] entries)
    {
        lock (_sync)
        {
            var key = Key(scope, path);
            if (entries.Length > MaximumEntries) { _directories.Remove(key); _version++; return; }
            _directories.TryGetValue(key, out var previous);
            var folders = entries.Where(entry => entry.GetProperty("type").GetString() == "directory")
                .Select(entry => entry.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);
            var prefix = path.Length == 0 ? "" : path + "/";
            foreach (var descendant in _directories.Values.Where(item => item.Scope == scope && item.Path.StartsWith(prefix, StringComparison.Ordinal) && item.Path.Length > prefix.Length).ToArray())
            {
                var child = prefix + descendant.Path[prefix.Length..].Split('/')[0];
                if (!folders.Contains(child)) { _directories.Remove(Key(scope, descendant.Path)); _checked.Remove(Key(scope, descendant.Path)); }
            }
            _directories[key] = new(scope, path, DateTimeOffset.UtcNow, previous?.Self, entries);
            _checked[key] = DateTimeOffset.UtcNow;
            Trim();
            _version++;
        }
    }

    internal void RememberStat(string scope, string path, JsonElement? metadata)
    {
        if (metadata is null || metadata.Value.ValueKind == JsonValueKind.Null) { Invalidate(scope, path); return; }
        var value = metadata.Value;
        lock (_sync)
        {
            var key = Key(scope, path);
            if (value.GetProperty("type").GetString() == "directory")
            {
                _directories.TryGetValue(key, out var previous);
                _directories[key] = new(scope, path, DateTimeOffset.UtcNow, value.Clone(), previous?.Entries);
            }
            else if (_directories.ContainsKey(key)) Invalidate(scope, path);
            if (path.Length > 0 && _directories.TryGetValue(Key(scope, Parent(path)), out var parent) && parent.Entries is { } children)
            {
                _directories[Key(scope, parent.Path)] = parent with
                { Entries = children.Select(child => child.GetProperty("path").GetString() == path ? value.Clone() : child).ToArray() };
            }
            Trim();
            _version++;
        }
    }

    internal void Invalidate(string scope, string path)
    {
        lock (_sync)
        {
            foreach (var entry in _directories.Values.Where(entry => entry.Scope == scope &&
                (path.Length == 0 || entry.Path == path || entry.Path.StartsWith(path + "/", StringComparison.Ordinal) || entry.Path == Parent(path))).ToArray())
            { _directories.Remove(Key(scope, entry.Path)); _checked.Remove(Key(scope, entry.Path)); }
            _version++;
        }
    }

    internal void RemoveRoot(string scope)
    {
        lock (_sync)
        {
            _roots.Remove(scope);
            Invalidate(scope, "");
        }
    }

    internal void RequestRefresh(string scope, string path, bool force = false)
    {
        lock (_sync)
        {
            var key = Key(scope, path);
            if (!force && _checked.TryGetValue(key, out var checkedAt) && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromSeconds(10)) return;
            if (_queued.Count < MaximumDirectories && _queued.Add(key)) _refresh.Enqueue((scope, path));
        }
    }

    internal bool TryRefresh(out (string Scope, string Path) request)
    {
        lock (_sync)
        {
            if (_refresh.TryDequeue(out request))
            {
                var key = Key(request.Scope, request.Path);
                _queued.Remove(key); _checked[key] = DateTimeOffset.UtcNow;
                return true;
            }
            return false;
        }
    }

    internal async Task FlushAsync(bool force = false)
    {
        if (!await _saving.WaitAsync(0).ConfigureAwait(false))
        {
            if (!force) return;
            await _saving.WaitAsync().ConfigureAwait(false);
        }
        string? temporary = null;
        try
        {
            Envelope envelope;
            long version;
            lock (_sync)
            {
                if (_version == _savedVersion || !force && DateTimeOffset.UtcNow - _lastSave < TimeSpan.FromSeconds(1)) return;
                envelope = new(1, _deviceKey, _roots.Values.ToArray(), _directories.Values.ToArray());
                version = _version;
            }
            var data = JsonSerializer.SerializeToUtf8Bytes(envelope);
            while (data.Length > MaximumBytes && envelope.Directories.Length > 0)
            {
                lock (_sync)
                {
                    foreach (var oldest in _directories.Values.OrderBy(item => item.Saved).Take(Math.Max(1, _directories.Count / 4)).ToArray())
                        _directories.Remove(Key(oldest.Scope, oldest.Path));
                    _version++;
                    version = _version;
                    envelope = new(1, _deviceKey, _roots.Values.ToArray(), _directories.Values.ToArray());
                }
                data = JsonSerializer.SerializeToUtf8Bytes(envelope);
            }
            if (data.Length > MaximumBytes) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, data).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            lock (_sync) { _savedVersion = version; _lastSave = DateTimeOffset.UtcNow; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _saving.Release();
        }
    }

    private void Load()
    {
        try
        {
            using var input = File.OpenRead(_path);
            if (input.Length > MaximumBytes) return;
            var saved = JsonSerializer.Deserialize<Envelope>(input);
            if (saved is not { Version: 1 } || saved.DeviceKey != _deviceKey || saved.Roots is null || saved.Roots.Length > 10_000 ||
                saved.Directories is null || saved.Directories.Length > MaximumDirectories) return;
            foreach (var root in saved.Roots) _roots[ScopeKey(root)] = root.Clone();
            foreach (var directory in saved.Directories)
            {
                if (!_roots.ContainsKey(directory.Scope) || DateTimeOffset.UtcNow - directory.Saved > Retention || directory.Saved > DateTimeOffset.UtcNow.AddMinutes(5)) continue;
                DeviceHubExplorerFileStore.ValidatePath(directory.Path, allowRoot: true);
                if (directory.Entries is { } entries)
                {
                    if (entries.Length > MaximumEntries) throw new IOException("Invalid cached directory size.");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var entry in entries)
                    {
                        var path = entry.GetProperty("path").GetString()!;
                        if (path.Length == 0 || Parent(path) != directory.Path || !seen.Add(path)) throw new IOException("Invalid cached tree.");
                        ValidateMetadata(entry, path);
                    }
                }
                if (directory.Self is { } self) ValidateMetadata(self, directory.Path);
                _directories[Key(directory.Scope, directory.Path)] = directory;
            }
            Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or NullReferenceException)
        { _roots.Clear(); _directories.Clear(); }
    }

    private static void ValidateMetadata(JsonElement entry, string path)
    {
        DeviceHubExplorerFileStore.ValidatePath(path, allowRoot: true);
        if (entry.GetProperty("path").GetString() != path || entry.GetProperty("name").GetString() != path[(path.LastIndexOf('/') + 1)..] ||
            entry.GetProperty("type").GetString() is not ("file" or "directory") || !entry.GetProperty("size").TryGetInt64(out var size) || size < 0 ||
            entry.GetProperty("modified").ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            entry.TryGetProperty("readOnly", out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new IOException("Invalid cached metadata.");
    }

    private void Trim()
    {
        var entries = _directories.Values.Sum(directory => directory.Entries?.Length ?? 0);
        foreach (var oldest in _directories.Values.OrderBy(directory => directory.Saved).ToArray())
        {
            if (_directories.Count <= MaximumDirectories && entries <= MaximumEntries) break;
            _directories.Remove(Key(oldest.Scope, oldest.Path));
            _checked.Remove(Key(oldest.Scope, oldest.Path));
            entries -= oldest.Entries?.Length ?? 0;
        }
    }

    private static string Key(string scope, string path) => scope + '\0' + path;
    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
}
