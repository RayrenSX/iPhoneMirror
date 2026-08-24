using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

internal sealed class DeviceSessionManager
{
    private readonly Action<ulong> _stopSession;
    private readonly Action<ulong> _destroySession;
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceCaptureState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedWirelessDevices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _teardowns =
        new(StringComparer.OrdinalIgnoreCase);

    internal DeviceSessionManager(NativeCore core)
        : this(core.StopDeviceSession, core.DestroyDeviceSession) { }

    internal DeviceSessionManager(Action<ulong> stopSession,
        Action<ulong> destroySession)
    {
        _stopSession = stopSession;
        _destroySession = destroySession;
    }

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
                state.ResetRuntimeObservations();
                state.Handle = handle;
                if (handle != 0) state.ErrorShown = false;
                changed = true;
            }
        }
        if (!changed) return;
        try { SessionHandleChanged?.Invoke(state.Udid, handle); }
        catch (Exception error)
        {
            // Session ownership changes must complete even if a UI observer
            // fails while closing a stale window.
            DiagnosticLogger.Exception("capture", "session_handle_observer_failed",
                error, ("device", AppLog.Device(state.Udid)),
                ("handle", AppLog.Handle(handle)));
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

    internal Task StopAndDestroyAsync(DeviceCaptureState state)
    {
        Task teardown;
        ulong handle;
        lock (_gate)
        {
            if (_teardowns.TryGetValue(state.Udid, out teardown!))
                return teardown;
            handle = state.Handle;
            if (handle == 0) return Task.CompletedTask;
            state.IsStopping = true;
            state.ResetRuntimeObservations();
            state.Handle = 0;
            teardown = StopAndDestroyCoreAsync(state, handle);
            _teardowns[state.Udid] = teardown;
        }
        // Revoke the handle before yielding so no preview can attach while
        // native teardown is releasing the decoder and USB configuration.
        PublishHandleChanged(state.Udid, 0);
        return teardown;
    }

    private async Task StopAndDestroyCoreAsync(DeviceCaptureState state,
        ulong handle)
    {
        // Always return to StopAndDestroyAsync before native work can finish,
        // so the in-flight task is registered before any concurrent caller or
        // completion path observes it.
        await Task.Yield();
        try
        {
            await Task.Run(() => _stopSession(handle));
        }
        finally
        {
            try { _destroySession(handle); }
            finally
            {
                lock (_gate)
                {
                    state.IsStopping = false;
                    _teardowns.Remove(state.Udid);
                }
            }
        }
    }

    private void PublishHandleChanged(string udid, ulong handle)
    {
        try { SessionHandleChanged?.Invoke(udid, handle); }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("capture", "session_handle_observer_failed",
                error, ("device", AppLog.Device(udid)),
                ("handle", AppLog.Handle(handle)));
        }
    }
}
