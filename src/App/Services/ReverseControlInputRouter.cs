namespace IPhoneMirror.App.Services;

/// <summary>
/// Single-owner input router. It prevents a stale session or a second mode
/// from receiving events and always releases the pressed-key state on stop.
/// </summary>
internal sealed class ReverseControlInputRouter
{
    private readonly object _gate = new();
    private string? _appleUdid;
    private ReverseControlMode _mode;
    private readonly HashSet<byte> _pressedKeys = [];

    internal string? AppleUdid { get { lock (_gate) return _appleUdid; } }
    internal ReverseControlMode Mode { get { lock (_gate) return _mode; } }

    internal bool Begin(string appleUdid, ReverseControlMode mode)
    {
        if (string.IsNullOrWhiteSpace(appleUdid) || mode == ReverseControlMode.None)
            return false;
        lock (_gate)
        {
            _appleUdid = appleUdid;
            _mode = mode;
            _pressedKeys.Clear();
            return true;
        }
    }

    internal bool Owns(string appleUdid, ReverseControlMode mode)
    {
        lock (_gate)
            return string.Equals(_appleUdid, appleUdid, StringComparison.OrdinalIgnoreCase) && _mode == mode;
    }

    internal IReadOnlyCollection<byte> UpdateKey(byte usage, bool down)
    {
        lock (_gate)
        {
            if (down) _pressedKeys.Add(usage);
            else _pressedKeys.Remove(usage);
            return _pressedKeys.ToArray();
        }
    }

    internal IReadOnlyCollection<byte> Stop()
    {
        lock (_gate)
        {
            var released = _pressedKeys.ToArray();
            _pressedKeys.Clear();
            _appleUdid = null;
            _mode = ReverseControlMode.None;
            return released;
        }
    }
}
