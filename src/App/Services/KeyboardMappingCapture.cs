namespace IPhoneMirror.App.Services;

internal enum MappingEditorState { Idle, WaitingForKey, KeyCaptured, PickingPosition, MappingReady }

// Capture is a transaction, independent of TextBox/WPF logical focus. A key
// completes only after release. Cancellation invalidates queued UI callbacks.
internal sealed class KeyboardMappingCapture
{
    private Action<MappedKey>? _callback;
    private MappedKey? _key;
    private long _generation;
    private readonly Dictionary<(int, bool), MappedKey> _held = [];
    private readonly HashSet<int> _unseenHeld = [];
    internal bool Waiting => _callback is not null;
    internal bool HasHeldKeys => _held.Count != 0 || _unseenHeld.Count != 0;
    internal void Begin(Action<MappedKey> callback) { Cancel(); _callback = callback; }
    internal void Cancel() { ++_generation; _callback = null; _key = null; }

    internal void ReconcilePhysicalKeys(IReadOnlySet<int> physicallyHeld)
    {
        var capturing = Waiting || HasHeldKeys;
        foreach (var (id, key) in _held.ToArray())
            if (!physicallyHeld.Contains(key.VirtualKey)) _held.Remove(id);
        _unseenHeld.RemoveWhere(key => !physicallyHeld.Contains(key));
        // A missing up must not complete the recording or keep its old
        // candidate forever. Leave the request waiting for a fresh press.
        if (_key is { } candidate && !physicallyHeld.Contains(candidate.VirtualKey)) _key = null;
        if (capturing)
            foreach (var key in physicallyHeld)
                if (!_held.Values.Any(held => held.VirtualKey == key)) _unseenHeld.Add(key);
    }

    internal bool Process(MappedKey key, bool down, bool active, Action<Action> dispatch)
    {
        // A press first seen during the outage cannot become a new recording
        // through auto-repeat, even if the user cancels and starts another one.
        if (_unseenHeld.Contains(key.VirtualKey))
        {
            if (!down) _unseenHeld.Remove(key.VirtualKey);
            return true;
        }
        var id = (key.ScanCode == 0 ? 0x1000 + key.VirtualKey : key.ScanCode, key.Extended);
        if (!down && _held.Remove(id))
        {
            if (_key?.SamePhysicalKey(key) == true && _callback is { } callback)
            {
                var generation = _generation;
                var captured = _key;
                _callback = null;
                _key = null;
                dispatch(() => { if (_generation == generation) callback(captured); });
            }
            return true;
        }
        if (down && _held.ContainsKey(id)) return true;
        if (!Waiting || !active || !down) return false;
        _held.Add(id, key);
        _key ??= key;
        return true;
    }
}
