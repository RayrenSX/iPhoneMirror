namespace IPhoneMirror.App.Services;

internal enum MappingEditorState { Idle, WaitingForKey, KeyCaptured, PickingPosition, MappingReady }

// Capture is a transaction, independent of TextBox/WPF logical focus. A key
// completes only after release. Cancellation invalidates queued UI callbacks.
internal sealed class KeyboardMappingCapture
{
    private Action<MappedKey>? _callback;
    private MappedKey? _key;
    private long _generation;
    private readonly HashSet<(int, bool)> _held = [];
    internal bool Waiting => _callback is not null;
    internal bool HasHeldKeys => _held.Count != 0;
    internal void Begin(Action<MappedKey> callback) { Cancel(); _callback = callback; }
    internal void Cancel() { ++_generation; _callback = null; _key = null; }

    internal bool Process(MappedKey key, bool down, bool active, Action<Action> dispatch)
    {
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
        if (down && _held.Contains(id)) return true;
        if (!Waiting || !active || !down) return false;
        _held.Add(id);
        _key ??= key;
        return true;
    }
}
