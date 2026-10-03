namespace IPhoneMirror.App.Services;

internal readonly record struct MappingKeyResult(KeyboardMappingEntry? Mapping, bool Suppress);

// Runs on the hook's dispatcher thread; no I/O, awaits or device operations.
internal sealed class KeyboardMappingKeyState
{
    private readonly Dictionary<(int, bool), MappedKey> _down = [];
    private readonly HashSet<(int, bool)> _suppressed = [];
    private readonly HashSet<(int, bool)> _modifierCandidates = [];

    internal MappingKeyResult Process(MappedKey key, bool down, bool injected,
        bool allowed, bool modifierHeld, bool suppressOriginal, IReadOnlyList<KeyboardMappingEntry> mappings)
    {
        if (injected) return default;
        var id = (key.ScanCode == 0 ? key.VirtualKey + 0x1000 : key.ScanCode, key.Extended);
        if (!down)
        {
            var suppressed = _suppressed.Remove(id);
            _down.Remove(id);
            var candidate = _modifierCandidates.Remove(id);
            return new(candidate && allowed && !modifierHeld
                ? mappings.FirstOrDefault(m => m.Enabled && m.Key!.SamePhysicalKey(key)) : null, suppressed);
        }
        if (!_down.TryAdd(id, key)) return new(null, _suppressed.Contains(id));
        // Modifier-only bindings fire on release, but never after a chord.
        if (_down.Count > 1 || !key.IsModifier) _modifierCandidates.Clear();
        if (!allowed || modifierHeld || key.IsWindows) return default;
        var mapping = mappings.FirstOrDefault(m => m.Enabled && m.Key!.SamePhysicalKey(key));
        if (mapping is null) return default;
        if (key.IsModifier)
        {
            if (_down.Count == 1) _modifierCandidates.Add(id);
            return default; // Never swallow modifier transitions or system chords.
        }
        if (suppressOriginal) _suppressed.Add(id);
        return new(mapping, suppressOriginal);
    }

    internal void CancelCandidates() => _modifierCandidates.Clear();
    internal void Reset() { _down.Clear(); _suppressed.Clear(); _modifierCandidates.Clear(); }
}
