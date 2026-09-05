namespace IPhoneMirror.App.Services;

internal enum UsbTouchTransport { Usb, Wireless }
internal enum ReverseControlState { Idle, BindingRequired, DeviceUnavailable, Connecting, Ready, Controlling, Error }

internal sealed class BridgeStatusEventArgs(string eventName, string? code, string? message) : EventArgs
{
    internal string EventName { get; } = eventName;
    internal string? Code { get; } = code;
    internal string? Message { get; } = message;
}

/// <summary>
/// Owns one UsbTouchBridge process. Callers never interact with Process/stdin
/// directly and can only send after the bridge has validated its ready event.
/// </summary>
internal sealed class UsbTouchBridgeHost : IAsyncDisposable
{
    private readonly DirectUsbInputBridge _bridge = new();
    private readonly object _gate = new();
    private string? _requestedUdid;
    private UsbTouchTransport _transport;
    private int _started;

    internal bool IsReady => _bridge.IsReady;
    internal string? Udid => _bridge.Udid;
    internal bool GateOpen => _bridge.GateOpen;
    internal string? AuthMode => _bridge.AuthMode;
    internal string? LastErrorCode => _bridge.LastErrorCode;
    internal string? LastDiagnostic => _bridge.LastDiagnostic;
    internal ReverseControlState State { get; private set; } = ReverseControlState.Idle;
    internal event EventHandler<BridgeStatusEventArgs>? StatusChanged;

    internal async Task StartAsync(UsbTouchTransport transport, string udid,
        string bridgePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("USB 触控桥接器已经启动。");
            _requestedUdid = udid;
            _transport = transport;
            State = ReverseControlState.Connecting;
        }
        _bridge.OnEvent += OnBridgeEvent;
        try
        {
            await _bridge.StartAsync(bridgePath, bridgePath, udid, 120,
                transport == UsbTouchTransport.Wireless, cancellationToken);
            if (!string.Equals(_bridge.Udid, udid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("反控桥接目标 UDID 不匹配。");
            State = ReverseControlState.Ready;
            Raise("ready", null, $"{transport}:{udid}");
        }
        catch
        {
            State = ReverseControlState.Error;
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task SendTouchBatchAsync(IReadOnlyList<TouchPoint> points,
        long timestampNs, long sequence, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendTouchBatchAsync(points, timestampNs, sequence, cancellationToken);
    }

    internal Task SendKeyboardAsync(IReadOnlyCollection<byte> usages,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendKeyboardAsync(usages, cancellationToken);
    }

    internal Task SendButtonAsync(ushort usagePage, ushort usageCode,
        string state, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendButtonAsync(usagePage, usageCode, state, cancellationToken);
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        await _bridge.StopAsync().ConfigureAwait(false);
        _bridge.OnEvent -= OnBridgeEvent;
        State = ReverseControlState.Idle;
    }

    private void EnsureReady()
    {
        if (!IsReady || State is not (ReverseControlState.Ready or ReverseControlState.Controlling))
            throw new InvalidOperationException("反控桥接器尚未就绪。");
    }

    private void OnBridgeEvent(BridgeEvent e) => Raise(e.Event, e.Code, e.Message);
    private void Raise(string name, string? code, string? message) =>
        StatusChanged?.Invoke(this, new BridgeStatusEventArgs(name, code, message));

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
