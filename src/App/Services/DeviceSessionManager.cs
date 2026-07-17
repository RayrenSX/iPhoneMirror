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
        var handle = state.Handle;
        if (handle == 0) return;
        try
        {
            await Task.Run(() => core.StopDeviceSession(handle));
        }
        finally
        {
            core.DestroyDeviceSession(handle);
            state.Handle = 0;
        }
    }
}
