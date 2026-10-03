namespace IPhoneMirror.App.Services;

// Dispatcher-owned except CaptureSequence, which runs at transport event arrival.
// Retries resume on the UI thread so the Windows clipboard always uses STA.
internal sealed class ClipboardSyncState(
    Action<string> writeText,
    Func<uint> getSequence,
    Func<string, object, bool> isSourceCurrent,
    Action<string, string, Exception?> completed,
    Func<Task>? retryDelay = null)
{
    // Our writes advance these tokens, including for events still queued on the
    // dispatcher; external copies leave older tokens invalidated.
    internal sealed class SequenceSnapshot(uint sequence)
    {
        internal uint Sequence = sequence;
    }

    private sealed class Update(object source, string text, SequenceSnapshot snapshot)
    {
        internal readonly object Source = source;
        internal readonly string Text = text;
        internal readonly SequenceSnapshot Snapshot = snapshot;
        internal bool Applied;
        internal int Attempts;
    }

    private readonly Dictionary<string, Update> _latest = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sequenceLock = new();
    // Queued callbacks and device caches own tokens. Weak references let us
    // rebase live arrivals without retaining a history of clipboard versions.
    private readonly List<WeakReference<SequenceSnapshot>> _snapshots = [];
    private string? _selectedDevice;
    private Task? _pump;
    private bool _stopped;

    internal void SelectDevice(string? device)
    {
        // Losing keyboard focus must not stop clipboard synchronization while
        // the user is pasting into another Windows application.
        if (!string.IsNullOrWhiteSpace(device)) _selectedDevice = device;
    }

    internal SequenceSnapshot CaptureSequence()
    {
        lock (_sequenceLock)
        {
            var sequence = getSequence();
            SequenceSnapshot? current = null;
            for (var i = _snapshots.Count - 1; i >= 0; i--)
            {
                if (!_snapshots[i].TryGetTarget(out var snapshot)) _snapshots.RemoveAt(i);
                else if (snapshot.Sequence == sequence) current = snapshot;
            }
            if (current is not null) return current;
            current = new SequenceSnapshot(sequence);
            _snapshots.Add(new WeakReference<SequenceSnapshot>(current));
            return current;
        }
    }

    internal void Observe(string device, object source, string? text, SequenceSnapshot snapshot)
    {
        if (_stopped) return;
        // Empty/non-text contents cancel any older retry without clearing the
        // Windows clipboard. Keep them in the cache to recognize A -> empty -> A.
        _latest[device] = new Update(source, text ?? string.Empty, snapshot);
    }

    internal Task FlushAsync()
    {
        if (_stopped) return Task.CompletedTask;
        if (_pump is { IsCompleted: false }) return _pump;
        return _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        // Coalesce queued bridge events and recheck the device before writing.
        await Task.Yield();
        while (!_stopped && _selectedDevice is { } device &&
               _latest.TryGetValue(device, out var update) &&
               !update.Applied && update.Attempts < 5)
        {
            if (update.Text.Length == 0 ||
                !isSourceCurrent(device, update.Source))
            {
                // Empty text or a replacement bridge supersedes this update,
                // including when it was waiting for a busy clipboard.
                update.Applied = true;
                return;
            }
            try
            {
                // Do not hold the capture lock across OLE clipboard writes:
                // they can block/reenter, but arrival must sample promptly.
                var previousSequence = update.Snapshot.Sequence;
                if (getSequence() != previousSequence)
                {
                    update.Applied = true;
                    return;
                }
                update.Attempts++;
                writeText(update.Text);
                lock (_sequenceLock)
                {
                    var writtenSequence = getSequence();
                    for (var i = _snapshots.Count - 1; i >= 0; i--)
                    {
                        if (!_snapshots[i].TryGetTarget(out var snapshot)) _snapshots.RemoveAt(i);
                        else if (snapshot.Sequence == previousSequence)
                            snapshot.Sequence = writtenSequence;
                    }
                }
                update.Applied = true;
            }
            catch (Exception error)
            {
                if (update.Attempts == 5 || error is not System.Runtime.InteropServices.ExternalException)
                {
                    update.Attempts = 5;
                    completed(device, update.Text, error);
                    return;
                }
                await (retryDelay?.Invoke() ?? Task.Delay(100 * update.Attempts));
                continue;
            }
            completed(device, update.Text, null);
        }
    }

    internal void Stop()
    {
        _stopped = true;
        _latest.Clear();
        _selectedDevice = null;
    }
}
