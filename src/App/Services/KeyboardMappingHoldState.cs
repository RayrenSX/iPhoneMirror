namespace IPhoneMirror.App.Services;

// Hook-thread ownership starts before dispatcher work is queued, so even a
// release received before the gesture starts invalidates that exact press.
internal sealed class KeyboardMappingHoldState
{
    private readonly Dictionary<Guid, (MappedKey Key, CancellationTokenSource Release)> _held = [];

    internal CancellationTokenSource Begin(KeyboardMappingEntry entry)
    {
        var release = new CancellationTokenSource();
        _held.Add(entry.Id, (entry.Key!, release));
        return release;
    }

    internal void Process(MappedKey key, bool down)
    {
        foreach (var (id, held) in _held.ToArray())
            if ((!down && held.Key.SamePhysicalKey(key)) ||
                (down && (held.Key.IsModifier || held.Key.IsWindows) && !held.Key.SamePhysicalKey(key)))
            {
                _held.Remove(id);
                held.Release.Cancel();
            }
    }

    internal void Release(Guid id)
    {
        if (_held.Remove(id, out var held)) held.Release.Cancel();
    }

    internal void Complete(Guid id, CancellationTokenSource release)
    {
        if (_held.TryGetValue(id, out var held) && ReferenceEquals(held.Release, release)) _held.Remove(id);
        release.Dispose();
    }

    internal void Cancel()
    {
        var held = _held.Values.ToArray();
        _held.Clear();
        foreach (var press in held) press.Release.Cancel();
    }
}
