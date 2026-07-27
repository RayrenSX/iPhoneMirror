using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

internal sealed class DeviceSessionManager(NativeCore core)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceCaptureState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedWirelessDevices =
        new(StringComparer.OrdinalIgnoreCase);

    internal event Action<string, ulong>? SessionHandleChanged;

    internal IReadOnlyList<KeyValuePair<string, DeviceCaptureState>> Entries
    {
        get { lock (_gate) return _states.ToArray(); }
    }

    internal IReadOnlyList<DeviceCaptureState> Values
    {
        get { lock (_gate) return _states.Values.ToArray(); }
    }

    internal bool AnySession
    {
        get { lock (_gate) return _states.Values.Any(state => state.HasSession); }
    }

    internal DeviceCaptureState? Get(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return null;
        lock (_gate) return _states.GetValueOrDefault(udid);
    }

    internal bool TryGet(string udid, out DeviceCaptureState state)
    {
        lock (_gate) return _states.TryGetValue(udid, out state!);
    }

    internal void Set(DeviceCaptureState state)
    {
        lock (_gate) _states[state.Udid] = state;
    }

    internal void SetHandle(DeviceCaptureState state, ulong handle)
    {
        var changed = false;
        lock (_gate)
        {
            if (state.Handle != handle)
            {
                state.Handle = handle;
                changed = true;
            }
        }
        if (!changed) return;
        try { SessionHandleChanged?.Invoke(state.Udid, handle); }
        catch
        {
            // Session ownership changes must complete even if a UI observer
            // fails while closing a stale window.
        }
    }

    internal bool Remove(string udid)
    {
        lock (_gate) return _states.Remove(udid);
    }

    internal bool IsWirelessPaused(string udid)
    {
        lock (_gate) return _pausedWirelessDevices.Contains(udid);
    }

    internal void SetWirelessPaused(string udid, bool paused)
    {
        lock (_gate)
        {
            if (paused) _pausedWirelessDevices.Add(udid);
            else _pausedWirelessDevices.Remove(udid);
        }
    }

    internal async Task StopAndDestroyAsync(DeviceCaptureState state)
    {
        ulong handle;
        lock (_gate)
        {
            handle = state.Handle;
            if (handle == 0 || state.IsStopping) return;
            state.IsStopping = true;
        }
        // Revoke the handle before yielding so no preview can attach while
        // native teardown is releasing the decoder and USB configuration.
        SetHandle(state, 0);
        try
        {
            await Task.Run(() => core.StopDeviceSession(handle));
        }
        finally
        {
            try { core.DestroyDeviceSession(handle); }
            finally
            {
                lock (_gate) state.IsStopping = false;
            }
        }
    }
}
