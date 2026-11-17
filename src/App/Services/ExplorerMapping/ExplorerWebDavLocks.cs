using System.Xml.Linq;

namespace IPhoneMirror.App.Services.ExplorerMapping;

internal sealed record ExplorerWebDavLockSnapshot(string Path, string Token, bool DepthInfinity,
    DateTimeOffset ExpiresAt, XElement? Owner);

internal sealed class ExplorerWebDavLockException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

// Locks belong only to this endpoint instance. They never outlive a mapping or
// grant a different session access to the phone. This limited If implementation
// accepts positive state-token lists and fails closed for conditions it cannot
// evaluate (Not, entity tags, or foreign resource tags).
internal sealed class ExplorerWebDavLocks(Func<string, string> hrefForPath,
    Func<string, string?> resolveTaggedHref, TimeProvider? clock = null)
{
    private const int MaximumLocks = 128;
    private const int MaximumTimeoutSeconds = 1800;
    private static readonly XNamespace Dav = "DAV:";
    private readonly object _gate = new();
    private readonly Dictionary<string, ExplorerWebDavLockSnapshot> _locks = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal ExplorerWebDavLockSnapshot Acquire(string path, bool depthInfinity,
        XElement? owner, int timeoutSeconds)
    {
        RequirePath(path);
        if (owner is not null && (owner.Name != Dav + "owner" ||
            owner.ToString(SaveOptions.DisableFormatting).Length > 8192 || owner.Descendants().Take(129).Count() > 128))
            throw new ExplorerWebDavLockException(400, "INVALID_LOCK_OWNER");
        lock (_gate)
        {
            Expire();
            if (_locks.Values.Any(existing => Covers(existing, path) ||
                (depthInfinity && AtOrBelow(existing.Path, path))))
                throw new ExplorerWebDavLockException(423, "LOCK_CONFLICT");
            if (_locks.Count >= MaximumLocks)
                throw new ExplorerWebDavLockException(507, "LOCK_LIMIT");
            var value = new ExplorerWebDavLockSnapshot(path, "opaquelocktoken:" + Guid.NewGuid().ToString("D"),
                depthInfinity, Deadline(timeoutSeconds), owner is null ? null : new XElement(owner));
            _locks.Add(value.Token, value);
            return Copy(value);
        }
    }

    internal ExplorerWebDavLockSnapshot? Refresh(string path, string token, int timeoutSeconds)
    {
        RequirePath(path);
        if (!TryNormalizeToken(token, out var normalized)) return null;
        lock (_gate)
        {
            Expire();
            if (!_locks.TryGetValue(normalized, out var existing) || existing.Path != path) return null;
            var refreshed = existing with { ExpiresAt = Deadline(timeoutSeconds) };
            _locks[normalized] = refreshed;
            return Copy(refreshed);
        }
    }

    internal bool Release(string path, string token)
    {
        RequirePath(path);
        if (!TryNormalizeToken(token, out var normalized)) return false;
        lock (_gate)
        {
            Expire();
            return _locks.TryGetValue(normalized, out var existing) && existing.Path == path && _locks.Remove(normalized);
        }
    }

    // A successful DELETE removes locks rooted on resources it removed.
    // MOVE also removes source-rooted locks rather than transferring them.
    // An inherited ancestor lock stays intact for the remaining collection.
    internal void RemoveSubtree(string path)
    {
        RequirePath(path);
        lock (_gate)
        {
            Expire();
            foreach (var token in _locks.Where(item => AtOrBelow(item.Value.Path, path)).Select(item => item.Key).ToArray())
                _locks.Remove(token);
        }
    }

    // For recursive namespace changes, a child lock also protects deleting or
    // moving its ancestor. The caller selects that scope after statting source.
    internal bool ValidateWrite(IReadOnlyCollection<string> paths, string? ifHeader,
        string requestPath, bool includeDescendants = false)
    {
        RequirePath(requestPath);
        foreach (var path in paths) RequirePath(path);
        var conditions = Parse(ifHeader, requestPath);
        if (conditions is null) return false;
        lock (_gate)
        {
            Expire();
            if (!Evaluate(conditions, out var supplied)) return false;
            return _locks.Values.Where(value => paths.Any(path => Covers(value, path) ||
                    (includeDescendants && AtOrBelow(value.Path, path))))
                .All(value => supplied.Contains(value.Token));
        }
    }

    internal bool TryGetRefreshToken(string? ifHeader, string requestPath, out string token)
    {
        RequirePath(requestPath);
        token = "";
        var conditions = Parse(ifHeader, requestPath);
        if (conditions is null || conditions.Count == 0) return false;
        lock (_gate)
        {
            Expire();
            if (!Evaluate(conditions, out var supplied)) return false;
            var matches = _locks.Values.Where(value => value.Path == requestPath && supplied.Contains(value.Token)).ToArray();
            if (matches.Length != 1) return false;
            token = matches[0].Token;
            return true;
        }
    }

    internal XElement Discovery(string path)
    {
        RequirePath(path);
        lock (_gate)
        {
            Expire();
            return new XElement(Dav + "lockdiscovery", _locks.Values.Where(value => Covers(value, path))
                .Select(value => new XElement(Dav + "activelock",
                    new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                    new XElement(Dav + "locktype", new XElement(Dav + "write")),
                    new XElement(Dav + "depth", value.DepthInfinity ? "infinity" : "0"),
                    value.Owner is null ? null : new XElement(value.Owner),
                    new XElement(Dav + "timeout", "Second-" + Math.Max(1,
                        (int)Math.Ceiling((value.ExpiresAt - _clock.GetUtcNow()).TotalSeconds))),
                    new XElement(Dav + "locktoken", new XElement(Dav + "href", value.Token)),
                    new XElement(Dav + "lockroot", new XElement(Dav + "href", hrefForPath(value.Path))))));
        }
    }

    internal static bool TryNormalizeToken(string value, out string token)
    {
        token = "";
        const string prefix = "opaquelocktoken:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(value[prefix.Length..], "D", out var id)) return false;
        token = prefix + id.ToString("D");
        return true;
    }

    private bool Evaluate(IReadOnlyDictionary<string, List<string[]>> conditions, out HashSet<string> supplied)
    {
        supplied = new(StringComparer.Ordinal);
        foreach (var group in conditions)
        {
            // State tokens in one list are ANDed; lists for one resource are
            // ORed. A token associated only with another path is not true here.
            var validLists = group.Value.Where(list => list.All(token =>
                _locks.TryGetValue(token, out var value) && Covers(value, group.Key))).ToArray();
            if (validLists.Length == 0) return false;
            foreach (var token in validLists.SelectMany(list => list)) supplied.Add(token);
        }
        return true;
    }

    private Dictionary<string, List<string[]>>? Parse(string? header, string requestPath)
    {
        var result = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        if (header is null) return result;
        if (header.Length == 0 || header.Length > 16384) return null;
        var offset = 0;
        void Space() { while (offset < header.Length && header[offset] is ' ' or '\t') offset++; }
        string? Angle()
        {
            if (offset >= header.Length || header[offset++] != '<') return null;
            var start = offset;
            while (offset < header.Length && header[offset] != '>')
            {
                if (header[offset] is '<' or '\r' or '\n' || char.IsControl(header[offset])) return null;
                offset++;
            }
            if (offset >= header.Length || offset == start) return null;
            return header[start..offset++];
        }
        bool Lists(string path)
        {
            if (!result.TryGetValue(path, out var alternatives)) result[path] = alternatives = [];
            var first = alternatives.Count;
            Space();
            while (offset < header.Length && header[offset] == '(')
            {
                offset++;
                var tokens = new List<string>();
                Space();
                while (offset < header.Length && header[offset] == '<')
                {
                    var value = Angle();
                    if (value is null || !TryNormalizeToken(value, out var token) || tokens.Count >= MaximumLocks) return false;
                    tokens.Add(token);
                    Space();
                }
                if (tokens.Count == 0 || offset >= header.Length || header[offset++] != ')') return false;
                alternatives.Add(tokens.Distinct(StringComparer.Ordinal).ToArray());
                Space();
            }
            return alternatives.Count > first;
        }
        Space();
        if (offset >= header.Length) return null;
        if (header[offset] == '(')
            return Lists(requestPath) && offset == header.Length ? result : null;
        while (offset < header.Length)
        {
            var href = Angle();
            if (href is null) return null;
            string? path;
            try { path = resolveTaggedHref(href); }
            catch (Exception error) when (error is UriFormatException or ArgumentException) { return null; }
            if (path is null || !ValidPath(path) || !Lists(path)) return null;
        }
        return result;
    }

    private DateTimeOffset Deadline(int seconds) => _clock.GetUtcNow().AddSeconds(Math.Clamp(seconds, 1, MaximumTimeoutSeconds));
    private static ExplorerWebDavLockSnapshot Copy(ExplorerWebDavLockSnapshot value) =>
        value with { Owner = value.Owner is null ? null : new XElement(value.Owner) };
    private void Expire()
    {
        var now = _clock.GetUtcNow();
        foreach (var token in _locks.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray()) _locks.Remove(token);
    }
    private static bool Covers(ExplorerWebDavLockSnapshot value, string path) =>
        value.Path == path || (value.DepthInfinity && AtOrBelow(path, value.Path));
    private static bool AtOrBelow(string path, string ancestor) => path == ancestor ||
        ancestor.Length == 0 || path.StartsWith(ancestor + "/", StringComparison.Ordinal);
    private static bool ValidPath(string path) => path.Length <= 2048 && (path.Length == 0 ||
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.Any(character => char.IsControl(character) || character is '\\' or ':')));
    private static void RequirePath(string path)
    {
        if (!ValidPath(path)) throw new ArgumentException("Use a canonical relative device path.", nameof(path));
    }
}
