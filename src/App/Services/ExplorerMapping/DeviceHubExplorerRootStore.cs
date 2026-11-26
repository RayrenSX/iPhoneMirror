using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IPhoneMirror.App.Services.Automation;

namespace IPhoneMirror.App.Services.ExplorerMapping;

// One drive contains independent phone services. Virtual directories never
// become remote paths, and snapshots retain the exact service that issued them.
internal sealed class DeviceHubExplorerRootStore : IExplorerFileStore
{
    private sealed record Scope(string Name, string Key, DeviceHubExplorerFileStore Store);
    private sealed record Source(Scope Scope, ExplorerFileEntry Entry);
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private ExplorerStorageInfo? _storage;
    private DateTimeOffset _storageChecked;
    private long _storageGeneration;
    private readonly Func<string, object?, CancellationToken, Task<JsonElement>> _request;
    private readonly string[] _names;
    private readonly ExplorerTreeCache? _cache;
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _discoveryAttempt;
    private CancellationTokenSource? _cacheRefreshAttempt;
    private int _foregroundPending;
    private int? _nextOffset = 0;
    private int? _total;
    internal event Action? RootsChanged;
    internal event Action<string>? DirectoryChanged;
    internal event Action? StorageChanged;
    private readonly ConditionalWeakTable<ExplorerFileEntry, Source> _sources = new();
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    private long _rootModifiedTicks = DateTimeOffset.UtcNow.UtcTicks;
    private readonly string _name;

    private DeviceHubExplorerRootStore(Func<string, object?, CancellationToken, Task<JsonElement>> request,
        string name, ExplorerTreeCache? cache, params string[] names) => (_request, _name, _cache, _names) = (request, name, cache, names);

    internal static async Task<DeviceHubExplorerRootStore> CreateAsync(
        Func<string, object?, CancellationToken, Task<JsonElement>> request,
        string name, string mediaName, string crashesName, string temporaryName, string systemCrashesName,
        CancellationToken cancellation, IProgress<(int Current, int Total)>? progress = null, string systemName = "System directories",
        string symbolsName = "System shared libraries", bool deferDiscovery = false, ExplorerTreeCache? cache = null)
    {
        var store = new DeviceHubExplorerRootStore(request, name, cache, mediaName, crashesName,
            temporaryName, systemCrashesName, systemName, symbolsName);
        if (deferDiscovery && cache?.Roots is { Length: > 0 } cachedRoots)
        {
            try { store.AcceptRoots(cachedRoots, confirmed: false); }
            catch (IOException)
            {
                cache.Clear();
                store._scopes.Clear(); store._seen.Clear(); store._used.Clear();
            }
            if (store._scopes.Count > 0) { cancellation.ThrowIfCancellationRequested(); return store; }
        }
        if (deferDiscovery)
        {
            var result = await request("explorer_roots", new { offset = 0, limit = 4, initialOnly = true }, cancellation).ConfigureAwait(false);
            store.AcceptPage(result, 0, 4, allowPending: true, progress);
        }
        else
        {
            await store.DiscoverRemainingAsync(cancellation, progress, pageSize: 4).ConfigureAwait(false);
            if (store._scopes.Count == 0) throw new IOException("No readable phone folders were found.");
        }
        cancellation.ThrowIfCancellationRequested();
        return store;
    }

    internal async Task DiscoverRemainingAsync(CancellationToken cancellation, IProgress<(int Current, int Total)>? progress = null, int pageSize = 1)
    {
        while (_nextOffset is { } offset)
        {
            cancellation.ThrowIfCancellationRequested();
            // Give waiting file reads priority between discovery requests.
            await Task.Delay(200, cancellation).ConfigureAwait(false);
            await _requests.WaitAsync(cancellation).ConfigureAwait(false);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            try
            {
                lock (_sync)
                {
                    if (_foregroundPending != 0) continue;
                    _discoveryAttempt = attempt;
                }
                var result = await _request("explorer_roots", new { offset, limit = pageSize }, attempt.Token).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                AcceptPage(result, offset, pageSize, allowPending: false, progress);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && attempt.IsCancellationRequested)
            {
                // A foreground read interrupted this read-only probe. Repeat
                // the same page when idle; never repeat a file mutation.
            }
            catch (AutomationException error) when (error.Code == "CONTROL_LOCKED")
            {
                // Another Device Hub command owns the connection. Discovery
                // is read-only and may wait until that command has completed.
            }
            finally
            {
                lock (_sync) { if (ReferenceEquals(_discoveryAttempt, attempt)) _discoveryAttempt = null; }
                _requests.Release();
            }
        }
        // Only a successfully completed catalogue may retire cached services.
        // A cancellation, busy service or disconnected phone keeps previews.
        lock (_sync)
        {
            var removed = _scopes.Values.Where(scope => !_confirmed.Contains(scope.Key)).ToArray();
            foreach (var scope in removed)
            {
                _scopes.Remove(scope.Name);
                _cache?.RemoveRoot(scope.Key);
            }
            if (removed.Length > 0) RootsChanged?.Invoke();
        }
    }

    internal async Task MaintainCacheAsync(CancellationToken cancellation)
    {
        if (_cache is null) return;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            await _cache.FlushAsync().ConfigureAwait(false);
            if (!_cache.TryRefresh(out var refresh)) { await Task.Delay(200, cancellation).ConfigureAwait(false); continue; }
            Scope? scope;
            lock (_sync) scope = _scopes.Values.FirstOrDefault(item => item.Key == refresh.Scope);
            if (scope is null) continue;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock (_sync) _cacheRefreshAttempt = attempt;
            try
            {
                await scope.Store.RefreshDirectoryAsync(refresh.Path, attempt.Token).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                DirectoryChanged?.Invoke(scope.Name + (refresh.Path.Length == 0 ? "" : "/" + refresh.Path));
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { _cache.RequestRefresh(refresh.Scope, refresh.Path, force: true); }
            catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) { _cache.Invalidate(refresh.Scope, refresh.Path); }
            catch (IOException) { /* Keep the last successful tree across transient transport failures. */ }
            catch (UnauthorizedAccessException) { _cache.Invalidate(refresh.Scope, refresh.Path); }
            finally { lock (_sync) { if (ReferenceEquals(_cacheRefreshAttempt, attempt)) _cacheRefreshAttempt = null; } }
            await Task.Delay(200, cancellation).ConfigureAwait(false);
        }
    }

    private void CancelCacheRefresh()
    {
        lock (_sync) _cacheRefreshAttempt?.Cancel();
    }

    private async Task<JsonElement> RequestFileAsync(string action, object? arguments, CancellationToken cancellation)
    {
        lock (_sync)
        {
            _foregroundPending++;
            _discoveryAttempt?.Cancel();
        }
        try
        {
            await _requests.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await _request(action, arguments, cancellation).ConfigureAwait(false); }
            finally
            {
                if (action is "write_mapped_file" or "make_directory" or "move_file" or "delete_mapped_file" or "set_mapped_file_time")
                    lock (_sync) { _storageChecked = DateTimeOffset.MinValue; _storageGeneration++; }
                _requests.Release();
            }
        }
        finally { lock (_sync) _foregroundPending--; }
    }

    private void AcceptPage(JsonElement result, int offset, int limit, bool allowPending, IProgress<(int Current, int Total)>? progress)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("roots", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 20 ||
            !result.TryGetProperty("total", out var count) || count.ValueKind != JsonValueKind.Number ||
            !count.TryGetInt32(out var currentTotal) || currentTotal is < 0 or > 10000 ||
            !result.TryGetProperty("nextOffset", out var next))
            throw new IOException("The phone returned invalid folder discovery results.");
        var pending = allowPending && result.TryGetProperty("pending", out var pendingValue) && pendingValue.ValueKind == JsonValueKind.True;
        if (pending)
        {
            if (currentTotal != 0 || next.ValueKind != JsonValueKind.Number || !next.TryGetInt32(out var start) || start != 0)
                throw new IOException("The phone returned an invalid discovery continuation.");
        }
        else
        {
            if (_total is not null && _total != currentTotal || offset > currentTotal)
                throw new IOException("The phone folder catalogue changed during discovery.");
            _total = currentTotal;
            if (next.ValueKind == JsonValueKind.Null) _nextOffset = null;
            else if (next.ValueKind == JsonValueKind.Number && next.TryGetInt32(out var following) && following == offset + limit && following < currentTotal)
                _nextOffset = following;
            else throw new IOException("Phone folder discovery made no progress.");
            progress?.Report((Math.Min(offset + limit, currentTotal), currentTotal));
        }
        AcceptRoots(items.EnumerateArray(), confirmed: true);
    }

    private void AcceptRoots(IEnumerable<JsonElement> items, bool confirmed)
    {
        var changed = false;
        foreach (var item in items)
        {
            var root = Field(item, "root") ?? throw new IOException("The phone returned a folder without its service.");
            if (root is not ("media" or "crashes" or "documents" or "container" or "coredevice" or "system" or "symbols")) continue;
            var bundle = Field(item, "bundleId");
            var domain = Field(item, "domain");
            var identifier = Field(item, "identifier");
            if ((root is "documents" or "container") && string.IsNullOrWhiteSpace(bundle) || root == "coredevice" && string.IsNullOrWhiteSpace(domain))
                throw new IOException("The phone returned a folder without its application or domain.");
            var folder = root switch
            {
                "media" => _names[0], "crashes" => _names[1], "system" => _names[4], "symbols" => _names[5],
                "coredevice" when domain == "temporary" => _names[2],
                "coredevice" when domain == "systemCrashLogs" => _names[3],
                _ => Field(item, "name") ?? bundle ?? identifier ?? domain,
            };
            if (string.IsNullOrWhiteSpace(folder)) throw new IOException("The phone returned an invalid folder name.");
            lock (_sync)
            {
                var key = ExplorerTreeCache.ScopeKey(item);
                if (confirmed) _confirmed.Add(key);
                _cache?.RememberRoot(item);
                if (!_seen.Add(key)) continue;
                var basis = FolderName(folder);
                var unique = basis;
                for (var number = 2; !_used.Add(unique); number++) unique = basis + " (" + number + ")";
                _scopes.Add(unique, new(unique, key, new(RequestFileAsync, root: root, bundleId: bundle,
                    domain: domain, identifier: identifier, treeCache: _cache, cacheScope: key, foregroundAccess: CancelCacheRefresh)));
                changed = true;
            }
        }
        if (changed)
        {
            Interlocked.Exchange(ref _rootModifiedTicks, DateTimeOffset.UtcNow.UtcTicks);
            RootsChanged?.Invoke();
        }
    }

    private static string? Field(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new IOException("The phone returned an invalid folder entry.");
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new IOException("The phone returned an invalid folder field.");
        return value.GetString();
    }

    public bool IsReadOnly => false;

    public async Task<ExplorerStorageInfo?> GetStorageAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CancelCacheRefresh();
        await _storageGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            long generation;
            lock (_sync)
            {
                if (_storage is not null && DateTimeOffset.UtcNow - _storageChecked < TimeSpan.FromSeconds(15)) return _storage;
                generation = _storageGeneration;
            }
            // Use the same selected-device service and disk fields as the
            // device-information panel. Windows must not use its local cache
            // disk's capacity as a substitute for the phone.
            var result = await RequestFileAsync("device_info", null, cancellation).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("disk", out var disk) || disk.ValueKind != JsonValueKind.Object ||
                !disk.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True ||
                !disk.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
                return _storage;
            if (!values.TryGetProperty("TotalDiskCapacity", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var totalBytes) ||
                !values.TryGetProperty("TotalDataAvailable", out var free) || free.ValueKind != JsonValueKind.Number || !free.TryGetInt64(out var availableBytes))
                throw new IOException("The phone returned invalid storage information.");
            var storage = new ExplorerStorageInfo(totalBytes, availableBytes);
            if (!storage.IsValid) throw new IOException("The phone returned invalid storage capacity.");
            bool changed;
            lock (_sync)
            {
                changed = _storage != storage;
                _storage = storage;
                _storageChecked = generation == _storageGeneration ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue;
            }
            if (changed) StorageChanged?.Invoke();
            return storage;
        }
        finally { _storageGate.Release(); }
    }

    public async Task<ExplorerFileEntry?> StatAsync(string path, CancellationToken cancellation)
        => await StatCoreAsync(path, cancellation, fresh: false).ConfigureAwait(false);

    public async Task<ExplorerFileEntry?> StatFreshAsync(string path, CancellationToken cancellation)
        => await StatCoreAsync(path, cancellation, fresh: true).ConfigureAwait(false);

    private async Task<ExplorerFileEntry?> StatCoreAsync(string path, CancellationToken cancellation, bool fresh)
    {
        cancellation.ThrowIfCancellationRequested();
        DeviceHubExplorerFileStore.ValidatePath(path, allowRoot: true);
        if (path.Length == 0) return DirectoryEntry("", _name);
        if (!TryResolve(path, out var scope, out var relative)) return null;
        if (relative.Length == 0) return DirectoryEntry(scope.Name, scope.Name, scope.Store.IsReadOnly);
        // Listing the drive must not open every app or depend on crash-service
        // availability. Open each service only when its directory is entered.
        var entry = await (fresh ? scope.Store.StatFreshAsync(relative, cancellation) : scope.Store.StatAsync(relative, cancellation)).ConfigureAwait(false);
        return entry is null ? null : Project(scope, entry);
    }

    public async Task<IReadOnlyList<ExplorerFileEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        DeviceHubExplorerFileStore.ValidatePath(path, allowRoot: true);
        if (path.Length == 0)
        {
            lock (_sync) return _scopes.Values.Select(scope => DirectoryEntry(scope.Name, scope.Name, scope.Store.IsReadOnly)).ToArray();
        }
        var (scope, relative) = Resolve(path);
        var entries = await scope.Store.ListAsync(relative, cancellation).ConfigureAwait(false);
        return entries.Select(entry => Project(scope, entry)).ToArray();
    }

    public Task<ReadOnlyMemory<byte>> ReadAsync(ExplorerFileEntry expected, long offset, int count, CancellationToken cancellation)
    {
        var source = Expected(expected);
        return source.Scope.Store.ReadAsync(source.Entry, offset, count, cancellation);
    }

    public Task PutAsync(string path, string localSourcePath, ExplorerFileEntry? expected, CancellationToken cancellation)
    {
        var (scope, relative) = Writable(path);
        return scope.Store.PutAsync(relative, localSourcePath, ExpectedAt(expected, scope, relative), cancellation);
    }

    public Task MakeDirectoryAsync(string path, CancellationToken cancellation)
    {
        var (scope, relative) = Writable(path);
        return scope.Store.MakeDirectoryAsync(relative, cancellation);
    }

    public Task MoveAsync(ExplorerFileEntry expectedSource, string destination, ExplorerFileEntry? expectedDestination, CancellationToken cancellation)
    {
        var source = Expected(expectedSource);
        Writable(expectedSource.Path);
        var (scope, relative) = Writable(destination);
        if (!ReferenceEquals(source.Scope, scope) || source.Scope.Store.IsReadOnly)
            throw new UnauthorizedAccessException("Move files within the same media or application folder.");
        return scope.Store.MoveAsync(source.Entry, relative, ExpectedAt(expectedDestination, scope, relative), cancellation);
    }

    public Task DeleteAsync(ExplorerFileEntry expected, CancellationToken cancellation)
    {
        var source = Expected(expected);
        Writable(expected.Path);
        return source.Scope.Store.DeleteAsync(source.Entry, cancellation);
    }

    public Task UpdatePropertiesAsync(ExplorerFileEntry expected, ExplorerFileProperties properties, CancellationToken cancellation)
    {
        var source = Expected(expected);
        Writable(expected.Path);
        return source.Scope.Store.UpdatePropertiesAsync(source.Entry, properties, cancellation);
    }

    private ExplorerFileEntry Project(Scope scope, ExplorerFileEntry entry)
    {
        var mapped = entry with { Path = scope.Name + (entry.Path.Length == 0 ? "" : "/" + entry.Path),
            Name = entry.Path.Length == 0 ? scope.Name : entry.Name };
        _sources.Add(mapped, new(scope, entry));
        return mapped;
    }

    private ExplorerFileEntry DirectoryEntry(string path, string name, bool readOnly = true) =>
        new(path, name, true, 0, path.Length == 0 ? new DateTimeOffset(Interlocked.Read(ref _rootModifiedTicks), TimeSpan.Zero) : _created,
            new(_created, _created, _created, readOnly ? 17u : 16u), IsReadOnly: readOnly);

    private Source Expected(ExplorerFileEntry entry) => _sources.TryGetValue(entry, out var source)
        ? source : throw new UnauthorizedAccessException("Mapped service folders cannot be changed; refresh files before using them.");

    private ExplorerFileEntry? ExpectedAt(ExplorerFileEntry? entry, Scope scope, string relative)
    {
        if (entry is null) return null;
        var source = Expected(entry);
        if (!ReferenceEquals(source.Scope, scope) || source.Entry.Path != relative)
            throw new ExplorerFileConflictException("The phone snapshot belongs to another folder.");
        return source.Entry;
    }

    private (Scope Scope, string Relative) Writable(string path)
    {
        var (scope, relative) = Resolve(path);
        if (relative.Length == 0 || scope.Store.IsReadOnly)
            throw new UnauthorizedAccessException("Service folders and crash logs are read-only.");
        return (scope, relative);
    }

    private (Scope Scope, string Relative) Resolve(string path) => TryResolve(path, out var scope, out var relative)
        ? (scope, relative) : throw new DirectoryNotFoundException("The mapped phone folder was not found.");

    private bool TryResolve(string path, out Scope scope, out string relative)
    {
        DeviceHubExplorerFileStore.ValidatePath(path, allowRoot: false);
        var separator = path.IndexOf('/');
        relative = separator < 0 ? "" : path[(separator + 1)..];
        lock (_sync) return _scopes.TryGetValue(separator < 0 ? path : path[..separator], out scope!);
    }

    private static string FolderName(string value)
    {
        var name = new string(value.Select(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "App";
        var stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '0' and <= '9')
            name = "_" + name;
        // Reserve room for a collision suffix within AFC/Windows name limits.
        while (Encoding.UTF8.GetByteCount(name) > 200) name = name[..^1];
        if (char.IsHighSurrogate(name[^1])) name = name[..^1];
        return name;
    }
}
