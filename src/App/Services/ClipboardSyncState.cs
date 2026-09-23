namespace IPhoneMirror.App.Services;

// Tracks successful and queued device-to-Windows clipboard updates. A failed
// clipboard write must remain eligible for a later retry of the same text.
internal sealed class ClipboardSyncState
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private string? _lastSuccessfulText;

    internal bool TryBegin(string text)
    {
        lock (_gate)
        {
            if (string.Equals(text, _lastSuccessfulText,
                    StringComparison.Ordinal))
                return false;
            return _pending.Add(text);
        }
    }

    internal void Complete(string text, bool succeeded)
    {
        lock (_gate)
        {
            _pending.Remove(text);
            if (succeeded) _lastSuccessfulText = text;
        }
    }
}
