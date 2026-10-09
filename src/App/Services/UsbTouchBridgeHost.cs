using IPhoneMirror.App.Localization;
namespace IPhoneMirror.App.Services;

internal enum UsbTouchTransport { Usb, Wireless }
internal enum ReverseControlState { Idle, BindingRequired, DeviceUnavailable, Connecting, Ready, Controlling, Error, Recovering }

internal sealed class BridgeStatusEventArgs(string eventName, string? code, string? message, string? text = null,
    long? clipboardReadId = null) : EventArgs
{
    internal string EventName { get; } = eventName;
    internal string? Code { get; } = code;
    internal string? Message { get; } = message;
    internal string? Text { get; } = text;
    internal long? ClipboardReadId { get; } = clipboardReadId;
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
    private long _lifecycleVersion;
    private Task? _stopTask;
    private CancellationTokenSource? _startCancellation;

    internal UsbTouchBridgeHost() : this(null) { }

    internal UsbTouchBridgeHost(UsbMuxResumeContext? muxResume)
    {
        _bridge.MuxResumeContext = muxResume;
    }

    internal bool IsReady => _bridge.IsReady;
    internal bool SupportsAutomationClipboard => _bridge.SupportsAutomationClipboard;
    internal Func<IDisposable>? InputAdmission { set => _bridge.InputAdmission = value; }
    internal bool InputWritePending => _bridge.InputWritePending;
    internal Task<string?> AutomationClipboardAsync(string kind, string? text, Func<bool> current,
        CancellationToken cancellation) => _bridge.AutomationClipboardAsync(kind, text, current, cancellation);
    internal long InputGeneration => _bridge.InputGeneration;
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
        long version;
        CancellationTokenSource startup;
        lock (_gate)
        {
            if (_started != 0 || _stopTask is { IsCompleted: false })
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeAlreadyStarted"));
            _started = 1;
            version = ++_lifecycleVersion;
            startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startCancellation = startup;
            _requestedUdid = udid;
            _transport = transport;
            State = ReverseControlState.Connecting;
            _bridge.OnEvent += OnBridgeEvent;
        }
        try
        {
            await _bridge.StartAsync(bridgePath, bridgePath, udid, 120,
                transport == UsbTouchTransport.Wireless, startup.Token);
            if (!string.Equals(_bridge.Udid, udid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(LocalizationService.Get("TouchBridgeTargetMismatch"));
            lock (_gate)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (_started == 0 || version != _lifecycleVersion || !_bridge.IsReady)
                    throw new OperationCanceledException("Bridge startup was stopped.");
                State = ReverseControlState.Ready;
                Raise("ready", null, $"{transport}:{udid}");
            }
        }
        catch
        {
            Task? stop = null;
            lock (_gate)
            {
                if (version == _lifecycleVersion)
                {
                    if (_started != 0)
                    {
                        State = ReverseControlState.Error;
                        stop = StopAsync();
                    }
                    else stop = _stopTask;
                }
            }
            if (stop is not null) await stop.ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_startCancellation, startup)) _startCancellation = null;
                startup.Dispose();
            }
        }
    }

    internal Task SendTouchBatchAsync(IReadOnlyList<TouchPoint> points,
        long timestampNs, long sequence, CancellationToken cancellationToken = default,
        Func<bool>? canSend = null, long? expectedGeneration = null)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendTouchBatchAsync(points, timestampNs, sequence, cancellationToken, canSend, expectedGeneration);
    }

    internal Task SendKeyboardAsync(IReadOnlyCollection<byte> usages,
        CancellationToken cancellationToken = default, Func<bool>? canSend = null, bool releaseAll = false)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendKeyboardAsync(usages, cancellationToken, canSend, releaseAll);
    }

    internal Task SendButtonAsync(ushort usagePage, ushort usageCode,
        string state, CancellationToken cancellationToken = default, Func<bool>? canSend = null,
        bool guardRelease = false)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendButtonAsync(usagePage, usageCode, state, cancellationToken, canSend, guardRelease);
    }

    internal Task SendPasteTextAsync(string text,
        CancellationToken cancellationToken = default, Func<bool>? canSend = null)
    {
        EnsureReady();
        State = ReverseControlState.Controlling;
        return _bridge.SendPasteTextAsync(text, cancellationToken, canSend);
    }

    internal Task SendReadClipboardAsync(
        CancellationToken cancellationToken = default, Func<bool>? canSend = null)
    {
        EnsureReady();
        return _bridge.SendReadClipboardAsync(cancellationToken, canSend);
    }


    internal Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is { IsCompleted: false }) return _stopTask;
            if (_started == 0) return Task.CompletedTask;
            _started = 0;
            _startCancellation?.Cancel();
            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        _bridge.OnEvent -= OnBridgeEvent;
        await _bridge.StopAsync().ConfigureAwait(false);
        State = ReverseControlState.Idle;
    }

    private void EnsureReady()
    {
        if (!IsReady || State is not (ReverseControlState.Ready or ReverseControlState.Controlling))
            throw new InvalidOperationException(LocalizationService.Get("ReverseControlBridgeNotReady"));
    }

    private void OnBridgeEvent(BridgeEvent e)
    {
        if (Volatile.Read(ref _started) == 0) return;
        if (e.Event == "ready") State = ReverseControlState.Ready;
        else if (e.Event == "status" && e.Code == "recovery_triggered")
            State = ReverseControlState.Recovering;
        else if (e.Event == "error" || (e.Event == "status" && e.Code == "terminated"))
            State = ReverseControlState.Error;
        StatusChanged?.Invoke(this, new BridgeStatusEventArgs(e.Event, e.Code, e.Message, e.Text, e.ClipboardReadId));
    }
    private void Raise(string name, string? code, string? message, string? text = null) =>
        StatusChanged?.Invoke(this, new BridgeStatusEventArgs(name, code, message, text));

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
