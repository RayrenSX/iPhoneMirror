namespace IPhoneMirror.App.Services;

internal enum KeyboardInputMode { None, Mapping, Direct }

// All ingress runs on the window dispatcher (including WH_KEYBOARD_LL).
// Workers only read a generation lease; they never choose an input owner.
internal sealed class KeyboardInputRouter
{
    private long _generation;
    private int _mode;
    private readonly HashSet<int> _held = [];
    private readonly HashSet<int> _retired = [];
    internal KeyboardInputMode Mode => (KeyboardInputMode)Volatile.Read(ref _mode);
    internal KeyboardInputMode RequestedMode { get; private set; }
    internal long Generation => Volatile.Read(ref _generation);
    internal bool HasRetiredKeys => _retired.Count != 0;

    internal long BeginHandoff(KeyboardInputMode next, IEnumerable<int>? held = null)
    {
        Volatile.Write(ref _mode, (int)KeyboardInputMode.None);
        var generation = Interlocked.Increment(ref _generation);
        RequestedMode = next;
        _retired.UnionWith(_held);
        if (held is not null) _retired.UnionWith(held);
        _held.Clear();
        return generation;
    }

    internal bool CompleteHandoff(long generation)
    {
        if (generation != Generation) return false;
        Volatile.Write(ref _mode, (int)RequestedMode);
        return true;
    }

    internal bool Owns(long generation, KeyboardInputMode owner) =>
        generation == Generation && Mode == owner;

    internal bool Route(KeyboardInputMode source, int key, bool down)
    {
        if (_retired.Contains(key))
        {
            if (!down) _retired.Remove(key);
            return false;
        }
        if (Mode == KeyboardInputMode.None && RequestedMode != KeyboardInputMode.None)
        {
            if (down) _retired.Add(key);
            return false;
        }
        if (source != Mode || source == KeyboardInputMode.None) return false;
        if (!down) return _held.Remove(key);
        var first = _held.Add(key);
        // Mapping's key state must see repeats to preserve Windows suppression;
        // its executor only receives the first press. HID reports are stateful.
        return first || source == KeyboardInputMode.Mapping;
    }
}

// Captures one existing session/transport. Cleanup uses that same session even
// after focus/ownership is revoked, and can never resolve a replacement device.
internal sealed record DirectKeyboardRoute(string Target, string Transport,
    object Session, long Generation, Func<bool> IsCurrent,
    Func<byte, IReadOnlyCollection<byte>, Func<bool>?, Task> SendAsync,
    Func<ushort, ushort, string, Func<bool>?, Task>? SendButtonAsync = null)
{
    internal bool SameSession(DirectKeyboardRoute other) =>
        ReferenceEquals(Session, other.Session) && Generation == other.Generation &&
        string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase) && Transport == other.Transport;
}
