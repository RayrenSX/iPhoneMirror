using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using IPhoneMirror.App.Localization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace IPhoneMirror.App.Services;

internal enum BluetoothHidStartupStage
{
    CheckingBluetooth,
    SwitchingToPeripheral,
}

/// <summary>
/// Exposes the Windows Bluetooth radio as a standard BLE HID mouse/keyboard.
/// iOS consumes this as a normal pointer device when AssistiveTouch is enabled.
/// </summary>
internal sealed class BluetoothHidMouseService : IAsyncDisposable
{
    internal const int ReportMapVersion = BluetoothHidProtocol.ReportMapVersion;
    private static readonly Guid HidServiceUuid = GattServiceUuids.HumanInterfaceDevice;
    private static readonly Guid ReportUuid = GattCharacteristicUuids.Report;
    private static readonly Guid BootKeyboardInputUuid =
        Guid.Parse("00002a22-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BootMouseInputUuid =
        Guid.Parse("00002a33-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ReportReferenceUuid = Guid.Parse("00002908-0000-1000-8000-00805f9b34fb");
    private static readonly byte[] HidInformation = [0x11, 0x01, 0x00, 0x03];
    private static readonly byte[] DefaultProtocolMode = [0x01];
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(2);
    // A native WinRT notification may outlive this managed watchdog. Once the
    // watchdog fires, retain the sole transport slot until the native task
    // really completes so delayed packets cannot overlap and replay stale
    // mouse movement on a restarted route.
    private static readonly TimeSpan MouseNotificationTimeout =
        TimeSpan.FromMilliseconds(300);
    // Pacing mouse notifications near the connection interval prevents an
    // invisible controller queue from becoming visible pointer lag after
    // physical movement stops.
    private static readonly TimeSpan MouseReportInterval = TimeSpan.FromMilliseconds(8);
    // A native WinRT notification can outlive its managed timeout. Shutdown
    // must remain bounded so the UI can restore normal mouse input promptly.
    private static readonly TimeSpan MousePumpStopTimeout =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TargetClientGateTimeout =
        TimeSpan.FromMilliseconds(500);
    // Release reports are best-effort safety state. Do not keep the reverse
    // control shutdown pending for seconds when a stalled BLE notification
    // is already known to be unusable; StopAsync will tear down the session
    // immediately afterwards and release the phone-side buttons as well.
    private static readonly TimeSpan ReleaseReportsTimeout =
        TimeSpan.FromMilliseconds(500);
    // Report 1 is keyboard and report 2 is mouse. Keeping both reports in one
    // HID service lets iOS expose pointer and keyboard input from one pairing.
    private const int AbsoluteMouseProbeMaximum = 32767;
    private const byte RelativeMouseReportId = 6;
    private const int MouseButtonTransitionQueueLimit = 64;
    private static readonly TimeSpan HybridGestureInterval = TimeSpan.FromMilliseconds(32);
    private static readonly TimeSpan HybridAnchorSettleInterval = TimeSpan.FromMilliseconds(64);
    private static readonly TimeSpan HybridReleaseSettleInterval = TimeSpan.FromMilliseconds(112);
    private GattLocalCharacteristic? _relativeMouseReport;
    private bool _useAbsoluteMouse;
    internal bool UsesAbsoluteMouse => Volatile.Read(ref _useAbsoluteMouse);
    private static readonly byte[] ReportMap =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x75, 0x08, 0x95, 0x01,
        0x81, 0x01, 0x75, 0x08, 0x95, 0x06, 0x15, 0x00, 0x25, 0x65,
        0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00, 0xC0,
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, 0x02, 0x09, 0x01,
        0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00,
        0x25, 0x01, 0x75, 0x01, 0x95, 0x05, 0x81, 0x02, 0x75, 0x03,
        0x95, 0x01, 0x81, 0x01, 0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02,
        0x81, 0x06, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08,
        0x95, 0x01, 0x81, 0x06,
        0x85, 0x03,
        0x09, 0x48, 0x15, 0x00, 0x25, 0x0A, 0x75, 0x08, 0x95, 0x01,
        0xB1, 0x02, 0xC0, 0xC0,
        0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x04,
        0x15, 0x00, 0x26, 0xFF, 0x03, 0x75, 0x10, 0x95, 0x01,
        // A Consumer usage array supports Globe and hardware volume/power
        // keys. A single Variable field only describes the Globe usage.
        0x19, 0x00, 0x2A, 0xFF, 0x03, 0x81, 0x00, 0xC0,
        // Navigation controls are separate from the Globe/Fn modifier report.
        // This mirrors standard external-keyboard Consumer Control usages used
        // by iPadOS for Back and Menu/recent-tasks.
        0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, 0x05,
        0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x0D,
        0x0A, 0x24, 0x02, 0x09, 0x40, 0x0A, 0x23, 0x02,
        0x0A, 0xAE, 0x01, 0x0A, 0x21, 0x02, 0x81, 0x02,
        0x95, 0x03, 0x75, 0x01, 0x81, 0x03, 0xC0
    ];

    // A failed send retains the newest button state so a release can never
    // be lost. Retry that state on this cadence; each new user transition
    // and each successful send resets the budget below.
    private static readonly TimeSpan MouseStateRetryInterval =
        TimeSpan.FromMilliseconds(16);
    private const int MouseStateRetryLimit = 1;
    private static readonly TimeSpan MouseStallLogInterval = TimeSpan.FromSeconds(1);

    private const ushort GlobeKeyboardLayoutUsage = 0x029D;
    private const ushort NavigationMenu = 0x0002;
    private static readonly TimeSpan AppSwitcherDoublePressInterval =
        TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _gate = new(1, 1);
    // NotifyValueAsync is a WinRT operation. Cancelling its managed AsTask
    // wrapper does not guarantee that the native notification has stopped.
    // Keep one transport slot until the real operation completes, otherwise
    // a timed-out report can overlap newer reports and replay old movement.
    private readonly SemaphoreSlim _mouseNotificationTransportGate = new(1, 1);
    private readonly object _notificationChannelSync = new();
    private NotificationChannel _notificationChannel = new();
    private NotificationChannel _mouseNotificationChannel = new();
    private readonly SemaphoreSlim _targetClientGate = new(1, 1);
    private readonly SemaphoreSlim _clientRefreshGate = new(1, 1);
    private readonly object _clientRefreshTrackingSync = new();
    private readonly object _mousePumpSync = new();
    private readonly BluetoothClientRouteTable _clientRoutes = new();
    private readonly ConcurrentDictionary<byte, byte[]> _lastReports = new();
    private readonly Func<byte, byte[], Func<bool>?, Task<bool>> _sendInputReport;
    private int _submittedInputReports;
    private HybridMouseState? _hybridMouseState;

    // One state per input generation. Only the active mouse pump mutates it.
    private sealed class HybridMouseState(long generation, int route)
    {
        internal readonly long Generation = generation;
        internal readonly int Route = route;
        internal byte[]? Anchor;
        internal byte[]? HeldPosition;
        internal bool NeedsAnchorRefresh;
        internal bool ButtonDeliveryUnconfirmed;
        internal bool SyntheticScroll;
        internal (double X, double Y)? DragPosition;
    }
    private (double Width, double Height) _absoluteDragGeometry = (375, 812);
    private readonly ConcurrentDictionary<string, ClientState> _clientStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GattSession> _clientSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _clientConnectedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _clientRefreshTasks = new();
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _mouseReport;
    private GattLocalCharacteristic? _keyboardReport;
    private GattLocalCharacteristic? _consumerReport;
    private GattLocalCharacteristic? _navigationReport;
    private GattLocalCharacteristic? _bootMouseInput;
    private GattLocalCharacteristic? _bootKeyboardInput;
    private GattLocalCharacteristic? _protocolModeCharacteristic;
    private GattLocalCharacteristic? _wheelResolutionCharacteristic;
    private TaskCompletionSource<bool>? _advertisingStarted;
    private TaskCompletionSource<bool>? _advertisingStopped;
    private int _advertisingStartupInProgress;
    private TaskCompletionSource<bool>? _clientConnected;
    private Task? _mousePumpTask;
    // Delayed retry continuations can outlive a native WinRT notification.
    // Incrementing this generation invalidates every continuation created
    // before a stop/release transition.
    private long _mousePumpGeneration;
    private byte[]? _pendingMouseReport;
    private Func<bool>? _pendingMouseCanSend;
    private long _pendingMouseReportTimestamp;
    private sealed record MouseInputReport(byte[] Payload, Func<bool>? CanSend, bool Reset = false);
    private readonly Queue<MouseInputReport> _mousePriorityReports = new();
    private readonly Queue<(byte ReportId, byte[] Report, TaskCompletionSource<bool> Completion,
        Func<bool>? CanSend)>
        _keyboardPriorityReports = new();
    private bool _mousePumpRunning;
    private bool _mousePumpStopping;
    private byte _lastQueuedMouseButtons;
    private int _mouseStateRetryAttempts;
    private int _mouseStallRecoveryInProgress;
    private long _lastMouseStallLogTimestamp;
    private int _transportFailed;
    private int _advertisingStopRequested;
    private int _disposed;
    private string? _targetClientId;
    private string? _targetDeviceUdid;
    private string? _targetDeviceName;
    private string? _preferredClientId;
    private int _routeGeneration;

    public bool IsAdvertising => _provider?.AdvertisementStatus is
        GattServiceProviderAdvertisementStatus.Started or
        GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
    public bool IsConnected => Volatile.Read(ref _transportFailed) == 0 &&
        IsMouseConnected;
    internal Func<IDisposable>? InputAdmission { get; set; }
    private static async Task FinishAdmittedInput(Task send, IDisposable? admission)
    {
        using (admission) await send.ConfigureAwait(false);
    }
    internal bool IsMouseReady => IsConnected;
    internal bool HasPendingInput { get { lock (_mousePumpSync) return _mousePumpRunning ||
        _lastQueuedMouseButtons != 0 || _mouseNotificationTransportGate.CurrentCount == 0; } }
    internal bool IsKeyboardReady => Volatile.Read(ref _transportFailed) == 0 &&
        (HasTargetSubscriber(_keyboardReport) || HasTargetSubscriber(_bootKeyboardInput));
    public int WheelResolutionMultiplier => GetTargetClientState()?.WheelResolutionMultiplier ?? 1;
    private bool IsMouseConnected => (HasTargetSubscriber(_mouseReport) &&
        (!UsesAbsoluteMouse || HasTargetSubscriber(_relativeMouseReport))) ||
        HasTargetSubscriber(_bootMouseInput);
    private bool HasAnySubscriber => HasSubscribers(_mouseReport) ||
        HasSubscribers(_relativeMouseReport) || HasSubscribers(_bootMouseInput) || HasSubscribers(_keyboardReport) ||
        HasSubscribers(_consumerReport) || HasSubscribers(_navigationReport) ||
        HasSubscribers(_bootKeyboardInput);
    public string SuggestedDeviceName { get; } = Environment.MachineName;
    public string Status { get; private set; } = LocalizationService.Get("BluetoothControlOff");
    public string? Error { get; private set; }
    internal bool HasTransportFailure => Volatile.Read(ref _transportFailed) != 0;

    public event EventHandler? StatusChanged;

    public BluetoothHidMouseService() : this(absoluteMouseProbe: false) { }

    internal BluetoothHidMouseService(bool absoluteMouseProbe)
    {
        _useAbsoluteMouse = absoluteMouseProbe;
        _sendInputReport = (id, report, guard) => SendReportAsync(id, report, canSend: guard);
    }

    internal BluetoothHidMouseService(Func<byte, byte[], Func<bool>?, Task<bool>> sendInputReport)
    {
        ArgumentNullException.ThrowIfNull(sendInputReport);
        _sendInputReport = sendInputReport;
    }
    internal string? TargetClientId => Volatile.Read(ref _targetClientId);
    internal bool IsTargetClientConnected => IsMouseConnected;

    private string ClientConnectionStatus => IsConnected
        ? LocalizationService.Get("BluetoothHidSelectedConnected")
        : HasAnySubscriber
            ? LocalizationService.Get("BluetoothHidWaitingSelected")
            : LocalizationService.Get("BluetoothHidWaitingClient");

    internal async Task<bool> WaitForConnectionAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (IsMouseConnected) return true;
        var waiter = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _clientConnected = waiter;
        if (IsMouseConnected) waiter.TrySetResult(true);
        try
        {
            var completed = await Task.WhenAny(waiter.Task,
                Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            return completed == waiter.Task && await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_clientConnected, waiter)) _clientConnected = null;
        }
    }

    public async Task<bool> StartAsync(string targetDeviceUdid,
        string? targetDeviceName = null,
        bool preserveExistingBinding = false,
        string? preferredClientId = null,
        CancellationToken cancellationToken = default,
        Action<BluetoothHidStartupStage>? startupProgress = null, bool? useAbsoluteMouse = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceUdid);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (useAbsoluteMouse is bool absolute && absolute != UsesAbsoluteMouse)
            {
                StopAndClearProviderState();
                Volatile.Write(ref _useAbsoluteMouse, absolute);
            }
            await BeginTargetRouteAsync(targetDeviceUdid, targetDeviceName,
                clearPreviousBinding: !IsAdvertising && !preserveExistingBinding,
                preferredClientId).ConfigureAwait(false);
            startupProgress?.Invoke(BluetoothHidStartupStage.CheckingBluetooth);
            if (IsAdvertising && Volatile.Read(ref _advertisingStopRequested) == 0)
            {
                await RefreshTargetClientAsync().ConfigureAwait(false);
                return true;
            }
            if (IsAdvertising)
            {
                StopAndClearProviderState();
                await BeginTargetRouteAsync(targetDeviceUdid, targetDeviceName,
                    preferredClientId: preferredClientId).ConfigureAwait(false);
            }
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                SetStatus(LocalizationService.Get("BluetoothHidPeripheralUnavailable"),
                    LocalizationService.Get("BluetoothHidLowEnergyUnsupported"));
                return false;
            }
            // Some Windows Bluetooth drivers report the peripheral capability
            // bit as false even though GATT service providers can advertise.
            // Let Create/StartAdvertising be the authoritative test, as this
            // is the path that works on the MediaTek adapter used by v1.8.1.
            SetStatus(adapter.IsPeripheralRoleSupported
                ? LocalizationService.Get("BluetoothHidPeripheralSupported")
                : LocalizationService.Get("BluetoothHidPeripheralTrying"), null);
            startupProgress?.Invoke(BluetoothHidStartupStage.SwitchingToPeripheral);

            if (_provider is null)
            {
                var result = await GattServiceProvider.CreateAsync(HidServiceUuid);
                if (result.Error != BluetoothError.Success || result.ServiceProvider is null)
                {
                    SetStatus(LocalizationService.Get("BluetoothHidServiceCreateFailed"),
                        LocalizationService.Format("BluetoothHidServiceCreateResultFormat", result.Error));
                    return false;
                }

                _provider = result.ServiceProvider;
                _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
                await CreateCharacteristicsAsync(_provider.Service);
            }

            var advertising = new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true,
            };
            Interlocked.Exchange(ref _advertisingStopRequested, 0);
            Interlocked.Exchange(ref _transportFailed, 0);
            _lastReports.Clear();
            Interlocked.Exchange(ref _submittedInputReports, 0);
            TrackSubscribedClients();
            _advertisingStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _advertisingStartupInProgress, 1);
            _provider.StartAdvertising(advertising);
            var completed = await Task.WhenAny(_advertisingStarted.Task,
                Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            Interlocked.Exchange(ref _advertisingStartupInProgress, 0);
            if (completed != _advertisingStarted.Task || !IsAdvertising)
            {
                StopAndClearProviderState();
                SetStatus(LocalizationService.Get("BluetoothHidAdvertisingFailed"),
                    LocalizationService.Get("BluetoothHidAdvertisingNotReported"));
                return false;
            }
            SetStatus(LocalizationService.Format("BluetoothHidAdvertisingFormat", _provider.AdvertisementStatus),
                null);
            await RefreshTargetClientAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _advertisingStartupInProgress, 0);
            StopAndClearProviderState();
            SetStatus(LocalizationService.Get("BluetoothHidStartFailed"), error.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        long generation;
        Task? mousePump;
        lock (_mousePumpSync)
        {
            generation = Interlocked.Increment(ref _mousePumpGeneration);
            _mousePumpStopping = true;
            _pendingMouseReport = null;
            _pendingMouseCanSend = null;
            _pendingMouseReportTimestamp = 0;
            _mousePriorityReports.Clear();
            while (_keyboardPriorityReports.Count > 0)
                _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
            mousePump = _mousePumpTask;
        }
        RetireNotificationChannel();
        if (mousePump is not null)
        {
            var completed = await Task.WhenAny(mousePump,
                Task.Delay(MousePumpStopTimeout)).ConfigureAwait(false);
            if (completed != mousePump)
                DiagnosticLogger.ReverseControlWarning("bluetooth",
                    "mouse_pump_stop_timeout");
        }
        var gateAcquired = await _gate.WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        if (!gateAcquired)
        {
            DiagnosticLogger.ReverseControlWarning("bluetooth",
                "bluetooth_stop_gate_timeout");
            lock (_mousePumpSync)
            {
                if (generation == _mousePumpGeneration) _mousePumpTask = null;
            }
            return;
        }
        try
        {
            if (!await StopAdvertisingSessionAsync().ConfigureAwait(false))
                StopAndClearProviderState();
            SetStatus(LocalizationService.Get("BluetoothControlOff"), null);
        }
        finally
        {
            _gate.Release();
            lock (_mousePumpSync)
            {
                if (generation == _mousePumpGeneration)
                {
                    _mousePumpStopping = false;
                    _mousePumpRunning = false;
                    _mousePumpTask = null;
                }
            }
        }
    }

    internal Task<bool> MoveAbsoluteMouseProbeAsync(double x, double y, string expectedTargetDeviceUdid)
    {
        if (!UsesAbsoluteMouse) throw new InvalidOperationException("Absolute mouse mode is disabled.");
        var absoluteX = EncodeAbsoluteAxis(x);
        var absoluteY = EncodeAbsoluteAxis(y);
        byte[] report = [0, (byte)(absoluteX & 0xFF), (byte)(absoluteX >> 8),
            (byte)(absoluteY & 0xFF), (byte)(absoluteY >> 8), 0];
        return SendReportAsync(2, report, expectedTargetDeviceUdid, Volatile.Read(ref _routeGeneration));
    }

    private static byte[] AddRelativeMouseReport(byte[] descriptor) =>
    [
        .. descriptor,
        // Separate Generic Desktop Mouse collection with relative X/Y.
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, RelativeMouseReportId,
        0x09, 0x01, 0xA1, 0x00,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x05, 0x81, 0x02, 0x75, 0x03, 0x95, 0x01, 0x81, 0x01,
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x81, 0x06,
        0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x01, 0x81, 0x06,
        0xC0, 0xC0,
    ];

    private static int EncodeAbsoluteAxis(double value) =>
        (int)Math.Round(Math.Clamp(value, 0, 1) * AbsoluteMouseProbeMaximum);

    internal Task SendAbsoluteMouseAsync(double x, double y, byte buttons, int wheel,
        string? target, Func<bool> canSend, bool reset = false) =>
        QueueMouseAsync(EncodeAbsoluteAxis(x), EncodeAbsoluteAxis(y), buttons, wheel, target,
            canSend, preserveButtonTransitions: true, absolute: true, reset: reset);

    internal void ConfigureAbsoluteDragGeometry(string? productType, uint width, uint height, int previewRotation)
    {
        var geometry = BluetoothAbsoluteDragMotion.Geometry(productType, width, height, previewRotation);
        lock (_mousePumpSync) _absoluteDragGeometry = geometry;
    }

    internal Task ReleaseAbsoluteMouseAsync(string? target, Func<bool> canSend)
    {
        if (!_lastReports.TryGetValue(2, out var report)) return Task.CompletedTask;
        var x = report[1] | report[2] << 8;
        var y = report[3] | report[4] << 8;
        return QueueMouseAsync(x, y, 0, 0, target, canSend,
            preserveButtonTransitions: true, absolute: true, reset: true);
    }

    private static byte[] MouseButtonStateReport(byte buttons, byte[]? position)
    {
        if (position is { Length: 6 })
            return [buttons, position[1], position[2], position[3], position[4], 0];
        return [buttons, 0, 0, 0, 0, 0];
    }

    private static byte[] CreateAbsoluteMouseReportMap()
    {
        ReadOnlySpan<byte> relativeAxes =
        [
            0x09, 0x30, 0x09, 0x31, // X, Y
            0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, // -32767 to 32767
            0x75, 0x10, 0x95, 0x02, 0x81, 0x06, // Two 16-bit relative values
        ];
        ReadOnlySpan<byte> absoluteAxes =
        [
            0x09, 0x30, 0x09, 0x31,
            0x16, 0x00, 0x00, 0x26, 0xFF, 0x7F, // 0 to 32767
            0x75, 0x10, 0x95, 0x02, 0x81, 0x02, // Two 16-bit absolute values
        ];
        var descriptor = (byte[])ReportMap.Clone();
        var offset = descriptor.AsSpan().IndexOf(relativeAxes);
        if (offset < 0) throw new InvalidOperationException("Relative mouse X/Y descriptor was not found.");
        absoluteAxes.CopyTo(descriptor.AsSpan(offset));
        return descriptor;
    }

    public Task SendMouseAsync(int dx, int dy, byte buttons = 0, int wheel = 0) =>
        QueueMouseAsync(dx, dy, buttons, wheel, null, null);

    private Task QueueMouseAsync(int dx, int dy, byte buttons, int wheel,
        string? expectedTargetDeviceUdid, Func<bool>? canSend, bool preserveButtonTransitions = false,
        bool absolute = false, bool reset = false)
    {
        if (absolute != UsesAbsoluteMouse) return Task.CompletedTask;
        // Capture before admission: a producer can be suspended there while
        // another thread pauses/resumes or replaces the connection.
        var generation = Volatile.Read(ref _mousePumpGeneration);
        var routeGeneration = Volatile.Read(ref _routeGeneration);
        var target = expectedTargetDeviceUdid ?? Volatile.Read(ref _targetDeviceUdid);
        bool SameSession() => generation == Volatile.Read(ref _mousePumpGeneration) &&
            routeGeneration == Volatile.Read(ref _routeGeneration) &&
            string.Equals(target, Volatile.Read(ref _targetDeviceUdid), StringComparison.OrdinalIgnoreCase);
        bool Current() => SameSession() && canSend?.Invoke() != false;
        if (!Current() || Volatile.Read(ref _mousePumpStopping) || !CanQueueReports)
            return Task.CompletedTask;
        using var admission = InputAdmission?.Invoke();
        var x = (short)Math.Clamp(dx, short.MinValue + 1, short.MaxValue);
        var y = (short)Math.Clamp(dy, short.MinValue + 1, short.MaxValue);
        var encodedWheel = Math.Clamp(wheel, -127, 127);
        if (!CanQueueReports)
            return Task.CompletedTask;
        byte[] report =
        [buttons, (byte)(x & 0xFF), (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF), (byte)((y >> 8) & 0xFF),
            unchecked((byte)(sbyte)encodedWheel)];
        if (!Current()) return Task.CompletedTask;
        lock (_mousePumpSync)
        {
            // External focus/ownership guards can take their own locks. Only
            // inspect transport state while holding the producer lock; the
            // pump revalidates the complete guard before actual delivery.
            if (!SameSession() || _mousePumpStopping || !CanQueueReports) return Task.CompletedTask;
            if (reset || buttons != _lastQueuedMouseButtons)
            {
                if (reset) _mousePriorityReports.Clear();
                QueuePendingMotionBeforePriorityReport();
                _lastQueuedMouseButtons = buttons;
                // A fresh user transition re-arms the retained-state retry
                // budget so the newest press/release always gets a full set
                // of delivery attempts even after earlier route failures.
                Volatile.Write(ref _mouseStateRetryAttempts, 0);
                if (preserveButtonTransitions)
                {
                    // Preserve a quick press/release while the previous BLE
                    // packet is in flight. Bound the backlog during a stall.
                    if (_mousePriorityReports.Count >= MouseButtonTransitionQueueLimit)
                    {
                        _mousePriorityReports.Clear();
                        // The discarded backlog may contain the up for an
                        // already delivered down. Release before admitting a
                        // new gesture, without dragging to its new position.
                        _mousePriorityReports.Enqueue(new(MouseButtonStateReport(0, absolute ? report : null), Current, Reset: true));
                    }
                    _mousePriorityReports.Enqueue(new(report, Current, reset));
                }
                else
                    EnqueueLatestMousePriorityReport(report, Current);
            }
            else if (wheel != 0)
            {
                // A wheel tick must not replace a pending click transition.
                if (preserveButtonTransitions && _mousePriorityReports.Count != 0)
                    return Task.CompletedTask;
                QueuePendingMotionBeforePriorityReport();
                EnqueueLatestMousePriorityReport(report, Current);
            }
            else
            {
                // NotifyValueAsync can take longer than a BLE connection
                // interval. Keep exactly one latest sample so a blocked
                // native notification cannot become a replay queue.
                // MergeRecentMotion(_pendingMouseReport, report) would replay
                // every relative count collected while BLE is busy. Keep one
                // disposable latest sample instead; old travel is latency.
                _pendingMouseReport = BluetoothMouseReportCoalescer
                    .MergePendingMotion(_pendingMouseReport, report);
                _pendingMouseCanSend = Current;
                _pendingMouseReportTimestamp = Stopwatch.GetTimestamp();
            }
            _lastReports[2] = absolute ? MouseButtonStateReport(0, report) : report;
            if (_mousePumpRunning) return Task.CompletedTask;
            _mousePumpRunning = true;
            StartReportPumpWhileLocked();
        }
        return Task.CompletedTask;
    }

    internal Task SendMouseAsync(int dx, int dy, byte buttons = 0, int wheel = 0,
        string? expectedTargetDeviceUdid = null, Func<bool>? canSend = null,
        bool preserveButtonTransitions = false) =>
        QueueMouseAsync(dx, dy, buttons, wheel, expectedTargetDeviceUdid, canSend,
            preserveButtonTransitions);

    private void QueuePendingMotionBeforePriorityReport()
    {
        // Never move historical pointer travel into the priority queue. If the
        // GATT stack is stalled, that movement is already stale and replaying
        // it after a button or wheel event causes seconds of visible drift.
        _pendingMouseReport = null;
        _pendingMouseCanSend = null;
        _pendingMouseReportTimestamp = 0;
    }

    private void EnqueueLatestMousePriorityReport(byte[] report, Func<bool>? canSend)
    {
        // A button/wheel report is also relative state. Keep one pending
        // transition behind the in-flight BLE notification; retaining a FIFO
        // here would replay stale clicks or scroll ticks after a stall.
        _mousePriorityReports.Clear();
        _mousePriorityReports.Enqueue(new(report, canSend));
    }

    private void StartReportPumpWhileLocked()
    {
        var generation = Volatile.Read(ref _mousePumpGeneration);
        // An async method runs synchronously until its first incomplete await.
        // Keep WinRT property reads and notification startup off the input thread
        // and outside the producer's lock, including uncontended semaphore waits.
        _mousePumpTask = Task.Run(() => PumpReportsAsync(generation));
    }

    private bool CanQueueReports => Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _transportFailed) == 0 &&
        Volatile.Read(ref _advertisingStopRequested) == 0 &&
        Volatile.Read(ref _targetDeviceUdid) is not null;

    private async Task<bool> SendHybridMouseAsync(byte[] report, bool reset, long generation,
        Func<bool> canSend)
    {
        HybridMouseState state;
        lock (_mousePumpSync)
        {
            if (generation != _mousePumpGeneration) return true;
            var route = Volatile.Read(ref _routeGeneration);
            if (_hybridMouseState is not { } current || current.Generation != generation || current.Route != route)
                _hybridMouseState = new(generation, route);
            state = _hybridMouseState;
        }

        var buttons = report[0];
        if (state.HeldPosition is { } previous)
        {
            if (!reset && !state.SyntheticScroll)
            {
                (double Width, double Height) geometry;
                lock (_mousePumpSync) geometry = _absoluteDragGeometry;
                var target = (X: (report[1] | report[2] << 8) * geometry.Width / AbsoluteMouseProbeMaximum,
                    Y: (report[3] | report[4] << 8) * geometry.Height / AbsoluteMouseProbeMaximum);
                var current = state.DragPosition ?? ((previous[1] | previous[2] << 8) * geometry.Width / AbsoluteMouseProbeMaximum,
                    (previous[3] | previous[4] << 8) * geometry.Height / AbsoluteMouseProbeMaximum);
                var wheel = buttons != 0 ? report[5] : (byte)0;
                var moved = false;
                for (var stepCount = 0; stepCount < 4096; stepCount++)
                {
                    if (!canSend()) return true;
                    var step = BluetoothAbsoluteDragMotion.Next(target.X - current.Item1, target.Y - current.Item2);
                    if (step == (0, 0)) break;
                    // Unacknowledged relative travel is disposable. Advance
                    // the prediction before awaiting so retry cannot replay it.
                    var travel = BluetoothAbsoluteDragMotion.Predict(step.X, step.Y);
                    current = (current.Item1 + travel.X, current.Item2 + travel.Y);
                    state.DragPosition = current;
                    state.HeldPosition = MouseButtonStateReport(previous[0], report);
                    state.NeedsAnchorRefresh = true;
                    moved = true;
                    if (!await SendRelativeAsync(buttons != 0 ? buttons : previous[0], step.X, step.Y, 0).ConfigureAwait(false))
                        return false;
                    // The acknowledged GATT notification already serializes
                    // delivery. Extra pacing per subdivision adds drag lag.
                    // PumpReportsAsync paces the completed input sample.
                    var yieldToKeyboard = false;
                    // Let a newly queued transition replace this obsolete
                    // movement target. Its final position is handled next.
                    lock (_mousePumpSync)
                    {
                        if (_keyboardPriorityReports.Count != 0)
                        {
                            // A multi-step drag must not starve key edges.
                            // Resume an unfinished up before any later click;
                            // a held move can defer to newer pointer samples.
                            if (buttons == 0 || (_mousePriorityReports.Count == 0 && _pendingMouseReport is null))
                            {
                                var later = _mousePriorityReports.ToArray();
                                _mousePriorityReports.Clear();
                                _mousePriorityReports.Enqueue(new(report, canSend, reset));
                                foreach (var queued in later) _mousePriorityReports.Enqueue(queued);
                            }
                            yieldToKeyboard = true;
                        }
                        if (buttons != 0 && (_mousePriorityReports.Count != 0 || _pendingMouseReport is not null)) break;
                    }
                    if (yieldToKeyboard) return true;
                }
                if (wheel != 0 ||
                    (buttons != 0 && (buttons != previous[0] || state.ButtonDeliveryUnconfirmed)))
                {
                    // Include the final movement before releasing, even if the
                    // pending move was coalesced into this button-up sample.
                    var heldButtons = buttons != 0 ? buttons : previous[0];
                    state.HeldPosition = MouseButtonStateReport(previous[0], report);
                    if (!await SendRelativeAsync(heldButtons, 0, 0, wheel).ConfigureAwait(false))
                        return false;
                    if (!canSend()) return true;
                    if (buttons != 0)
                    {
                        state.HeldPosition = report;
                        return true;
                    }
                    await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
                }
                else if (buttons != 0)
                {
                    // Keep sub-count travel until it rounds to a usable delta.
                    return true;
                }
                if (moved) await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
            }
            if (!await SendRelativeAsync(0, 0, 0, 0).ConfigureAwait(false)) return false;
            if (!canSend()) return true;
            state.HeldPosition = null;
            state.SyntheticScroll = false;
            state.DragPosition = null;
            // Do not move the absolute pointer until iOS has consumed the up.
            await Task.Delay(HybridReleaseSettleInterval).ConfigureAwait(false);
        }

        var position = MouseButtonStateReport(0, report);
        if (!await SendAnchorAsync(position, activateRelative: buttons != 0 || report[5] != 0).ConfigureAwait(false)) return false;
        if (!canSend()) return true;
        if (buttons == 0)
        {
            if (report[5] == 0 || reset) return true;
            // AssistiveTouch does not consistently consume a wheel from a
            // hybrid absolute/relative mouse. Scroll with a short, bounded
            // pointer drag on the same BLE route, then restore the anchor.
            var y = report[3] | report[4] << 8;
            var travel = Math.Clamp(-(sbyte)report[5] * 24, -256, 256);
            // Keep the press on the requested scroll surface. Moving the
            // origin into the middle of the screen can activate another pane.
            // Shorten outward travel at the edge; never turn it into a tap.
            var upwardRoom = y * 1024 / AbsoluteMouseProbeMaximum;
            var downwardRoom = (AbsoluteMouseProbeMaximum - y) * 1024 / AbsoluteMouseProbeMaximum;
            travel = Math.Clamp(travel, -upwardRoom, downwardRoom);
            // A sub-threshold drag is interpreted as a click by iOS. Ignore
            // outward scrolling when the edge leaves too little drag room.
            if (Math.Abs(travel) < 16) return true;
            await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
            if (!canSend()) return true;
            state.SyntheticScroll = true;
            state.HeldPosition = MouseButtonStateReport(1, position);
            if (!await SendRelativeAsync(1, 0, 0, 0).ConfigureAwait(false)) return false;
            for (var remaining = travel; remaining != 0;)
            {
                await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
                if (!canSend()) return true;
                var step = (short)Math.Clamp(remaining, -32, 32);
                remaining -= step;
                state.NeedsAnchorRefresh = true;
                if (!await SendRelativeAsync(1, 0, step, 0).ConfigureAwait(false)) return false;
            }
            await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
            // Cleanup is also attempted by the queued reset if focus expires.
            if (!canSend()) return true;
            if (!await SendRelativeAsync(0, 0, 0, 0).ConfigureAwait(false)) return false;
            state.HeldPosition = null;
            state.SyntheticScroll = false;
            // Do not move the absolute pointer until iOS has consumed the up.
            await Task.Delay(HybridReleaseSettleInterval).ConfigureAwait(false);
            return await SendAnchorAsync(position).ConfigureAwait(false);
        }

        // iOS switches between two pointer collections asynchronously. A BLE
        // notification acknowledgment is not a rendered-position acknowledgment.
        await Task.Delay(HybridAnchorSettleInterval).ConfigureAwait(false);
        if (!canSend()) return true;
        // A timed-out press may still have reached the device. Retain enough
        // state to release it even if WinRT did not acknowledge it.
        state.HeldPosition = report;
        var pressed = await SendRelativeAsync(buttons, 0, 0, report[5]).ConfigureAwait(false);
        // A queued up can follow immediately after a down. Give iOS at least
        // one display/input cycle to observe the press before draining the up.
        if (pressed) await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
        return pressed;

        async Task<bool> SendAnchorAsync(byte[] position, bool activateRelative = false)
        {
            if (state.Anchor is null || activateRelative)
            {
                // Select the relative pointer collection before anchoring the
                // next press; after scrolling iOS can return to its old
                // cursor even though the absolute sample was acknowledged.
                if (!await SendRelativeAsync(0, 1, 0, 0).ConfigureAwait(false)) return false;
                if (!canSend()) return true;
                state.NeedsAnchorRefresh = true;
                await Task.Delay(HybridAnchorSettleInterval).ConfigureAwait(false);
            }
            if (state.Anchor is null || (state.NeedsAnchorRefresh &&
                position.AsSpan(1, 4).SequenceEqual(state.Anchor.AsSpan(1, 4))))
            {
                // iOS may ignore the first absolute sample after connecting,
                // or an unchanged sample after a relative gesture. Change the
                // sample before sending the actual requested position.
                var x = position[1] | position[2] << 8;
                var offset = AbsoluteMouseProbeMaximum / 100;
                x += x <= AbsoluteMouseProbeMaximum / 2 ? offset : -offset;
                byte[] refresh = [0, (byte)x, (byte)(x >> 8), position[3], position[4], 0];
                if (!await _sendInputReport(2, refresh, canSend).ConfigureAwait(false)) return false;
                if (!canSend()) return true;
                await Task.Delay(HybridGestureInterval).ConfigureAwait(false);
            }
            var sent = await _sendInputReport(2, position, canSend).ConfigureAwait(false);
            if (sent && canSend())
            {
                state.Anchor = position;
                state.NeedsAnchorRefresh = false;
            }
            return sent;
        }

        async Task<bool> SendRelativeAsync(byte held, short dx, short dy, byte wheel)
        {
            // Retaining a possibly delivered press is necessary for cleanup,
            // but must not make a failed press look acknowledged on retry.
            // Retry the button state without replaying disposable movement.
            state.ButtonDeliveryUnconfirmed = true;
            var sent = await _sendInputReport(RelativeMouseReportId,
                EncodeRelativeMouse(held, dx, dy, wheel), canSend).ConfigureAwait(false);
            if (sent && canSend()) state.ButtonDeliveryUnconfirmed = false;
            return sent;
        }
    }

    private static byte[] EncodeRelativeMouse(byte buttons, short dx, short dy, byte wheel) =>
        [buttons, (byte)dx, (byte)(dx >> 8), (byte)dy, (byte)(dy >> 8), wheel];

    private async Task PumpReportsAsync(long generation)
    {
        while (true)
        {
            byte[]? report = null;
            byte reportId = 2;
            var reset = false;
            TaskCompletionSource<bool>? completion = null;
            Func<bool>? canSend = null;
            lock (_mousePumpSync)
            {
                if (generation != Volatile.Read(ref _mousePumpGeneration)) return;
                if (_mousePumpStopping)
                {
                    _mousePumpRunning = false;
                    return;
                }
                // Keyboard state changes are latency-sensitive. Always drain
                // them before coalesced mouse motion so pointer traffic cannot
                // starve key presses or releases.
                if (_keyboardPriorityReports.Count > 0)
                {
                    var item = _keyboardPriorityReports.Dequeue();
                    report = item.Report;
                    completion = item.Completion;
                    reportId = item.ReportId;
                    canSend = item.CanSend;
                }
                else if (_mousePriorityReports.Count > 0)
                {
                    var item = _mousePriorityReports.Dequeue();
                    report = item.Payload;
                    reset = item.Reset;
                    canSend = item.CanSend;
                }
                else if (_pendingMouseReport is not null)
                {
                    report = _pendingMouseReport;
                    canSend = _pendingMouseCanSend;
                    _pendingMouseReport = null;
                    _pendingMouseCanSend = null;
                    var queuedAt = _pendingMouseReportTimestamp;
                    _pendingMouseReportTimestamp = 0;
                    if (!UsesAbsoluteMouse && queuedAt != 0 &&
                        (Stopwatch.GetTimestamp() - queuedAt) * 1000.0 /
                            Stopwatch.Frequency > 100)
                    {
                        report = null;
                    }
                }
                else
                {
                    _mousePumpRunning = false;
                    return;
                }
            }
            if (report is null)
                continue;
            try
            {
                if (reportId == 2 && canSend?.Invoke() == false) continue;
                // Recheck after transport waits as well: a dequeued report
                // must not enter a fresh channel after release or stop.
                bool Current() => generation == Volatile.Read(ref _mousePumpGeneration) &&
                    canSend?.Invoke() != false;
                var sent = await (reportId == 2 && UsesAbsoluteMouse
                    ? SendHybridMouseAsync(report, reset, generation, Current)
                    : _sendInputReport(reportId, report, Current)).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _mousePumpGeneration))
                {
                    completion?.TrySetCanceled();
                    return;
                }
                if (sent)
                {
                    completion?.TrySetResult(true);
                    if (reportId == 2)
                    {
                        lock (_mousePumpSync)
                        {
                            if (generation != _mousePumpGeneration) return;
                            _mouseStateRetryAttempts = 0;
                        }
                        // NotifyValueAsync can complete after Windows queues
                        // the packet, rather than after the next BLE
                        // connection event. Pacing mouse reports here
                        // prevents an invisible queue from becoming visible
                        // pointer lag after physical movement stops.
                        await Task.Delay(MouseReportInterval).ConfigureAwait(false);
                    }
                }
                else
                {
                    completion?.TrySetException(new IOException(
                        "The selected Bluetooth HID client is not available."));
                    if (reportId == 2 && canSend?.Invoke() != false)
                    {
                        // A timed-out notification may still be held by the
                        // Windows stack. Drop movement and wheel history, but
                        // retain the newest button state so a release cannot
                        // be lost while the mouse is stationary.
                        bool waitForTransport;
                        bool retry;
                        lock (_mousePumpSync)
                        {
                            if (generation != Volatile.Read(ref _mousePumpGeneration)) return;
                            _pendingMouseReport = null;
                            _pendingMouseCanSend = null;
                            _pendingMouseReportTimestamp = 0;
                            var position = report;
                            var buttonState = report.Length > 0 ? report[0] :
                                _lastQueuedMouseButtons;
                            while (_mousePriorityReports.Count > 0)
                            {
                                var item = _mousePriorityReports.Dequeue();
                                var pending = item.Payload;
                                if (pending.Length > 0 && pending[5] == 0)
                                {
                                    buttonState = pending[0];
                                    position = pending;
                                    reset = item.Reset;
                                    canSend = item.CanSend;
                                }
                            }
                            _mousePriorityReports.Clear();
                            _mousePriorityReports.Enqueue(
                                new(MouseButtonStateReport(buttonState, UsesAbsoluteMouse ? position : null), canSend, reset));
                            _lastQueuedMouseButtons = buttonState;
                            waitForTransport = !TryAcquireMouseTransportGate();
                            retry = !waitForTransport &&
                                _mouseStateRetryAttempts < MouseStateRetryLimit;
                            if (retry) ++_mouseStateRetryAttempts;
                            else if (!waitForTransport) _mousePriorityReports.Clear();
                            _mousePumpRunning = false;
                        }
                        LogMouseSendFailure(report.Length > 0 ? report[0] : (byte)0);
                        if (waitForTransport)
                        {
                            // The timed-out WinRT operation may still be in
                            // flight. Wait for that native operation instead
                            // of retrying against it and building a delayed
                            // movement/press queue.
                            _ = ResumeMousePumpAfterTransportAsync(
                                generation);
                        }
                        else if (retry)
                        {
                            // The failure is transient (a notification
                            // timeout whose native call already ended, or a
                            // subscriber enumeration race). Redeliver the
                            // retained button state after a short pause:
                            // dropping it here is what leaves iOS holding a
                            // pressed button as a permanent drag rectangle.
                            _ = ResumeMousePumpAfterRetryDelayAsync(
                                generation);
                        }
                        // A key may have arrived during the mouse await.
                        // It must complete even when mouse retries are exhausted.
                        StartReportPumpIfNeeded(generation, keyboardOnly: true);
                        return;
                    }
                }
            }
            catch (Exception error)
            {
                completion?.TrySetException(error);
            }
        }
    }

    private async Task ResumeMousePumpAfterRetryDelayAsync(long generation)
    {
        try { await Task.Delay(MouseStateRetryInterval).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        StartMousePumpIfNeeded(generation);
    }

    private bool TryAcquireMouseTransportGate()
    {
        if (!_mouseNotificationTransportGate.Wait(0)) return false;
        _mouseNotificationTransportGate.Release();
        return true;
    }

    private async Task ResumeMousePumpAfterTransportAsync(long generation)
    {
        try
        {
            if (!await _mouseNotificationTransportGate.WaitAsync(MouseNotificationTimeout)
                    .ConfigureAwait(false)) return;
            _mouseNotificationTransportGate.Release();
        }
        catch (ObjectDisposedException) { return; }
        StartMousePumpIfNeeded(generation);
    }

    private void StartMousePumpIfNeeded(long? expectedGeneration = null) =>
        StartReportPumpIfNeeded(expectedGeneration, keyboardOnly: false);

    private void StartReportPumpIfNeeded(long? expectedGeneration, bool keyboardOnly)
    {
        lock (_mousePumpSync)
        {
            if (expectedGeneration is not null &&
                expectedGeneration.Value != Volatile.Read(ref _mousePumpGeneration))
                return;
            if (!CanQueueReports || _mousePumpStopping || _mousePumpRunning ||
                (keyboardOnly && _keyboardPriorityReports.Count == 0) ||
                (_pendingMouseReport is null && _mousePriorityReports.Count == 0 &&
                    _keyboardPriorityReports.Count == 0))
                return;
            _mousePumpRunning = true;
            StartReportPumpWhileLocked();
        }
    }

    private void LogMouseSendFailure(byte buttons)
    {
        // Throttle per episode so a sustained stall cannot flood the log
        // while still recording how the link recovers.
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastMouseStallLogTimestamp);
        if (last != 0 && (now - last) * 1000.0 / Stopwatch.Frequency <
                MouseStallLogInterval.TotalMilliseconds) return;
        Interlocked.Exchange(ref _lastMouseStallLogTimestamp, now);
        DiagnosticLogger.ReverseControlWarning("bluetooth", "mouse_report_send_retry",
            ("buttons", buttons),
            ("attempt", Volatile.Read(ref _mouseStateRetryAttempts) + 1),
            ("attempt_limit", MouseStateRetryLimit));
    }

    internal async Task ReleaseAllAsync(bool keepPumpStopped = false) =>
        _ = await ReleaseInputReportsAsync(keepPumpStopped, requireDelivery: false).ConfigureAwait(false);

    internal Task<bool> PauseInputAsync() =>
        ReleaseInputReportsAsync(keepPumpStopped: true, requireDelivery: true);

    internal bool ResumeInput(string targetDeviceUdid)
    {
        lock (_mousePumpSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || HasTransportFailure ||
                !string.Equals(_targetDeviceUdid, targetDeviceUdid, StringComparison.OrdinalIgnoreCase))
                return false;
            _mousePumpStopping = false;
            _mousePumpRunning = false;
            _mousePumpTask = null;
            return true;
        }
    }

    private async Task<bool> ReleaseInputReportsAsync(bool keepPumpStopped, bool requireDelivery)
    {
        var routeGeneration = Volatile.Read(ref _routeGeneration);
        var targetDevice = Volatile.Read(ref _targetDeviceUdid);
        var targetClient = Volatile.Read(ref _targetClientId);
        long generation;
        Task? mousePump;
        lock (_mousePumpSync)
        {
            generation = Interlocked.Increment(ref _mousePumpGeneration);
            _mousePumpStopping = true;
            _pendingMouseReport = null;
            _pendingMouseCanSend = null;
            _pendingMouseReportTimestamp = 0;
            _mousePriorityReports.Clear();
            _lastQueuedMouseButtons = 0;
            while (_keyboardPriorityReports.Count > 0)
                _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
            mousePump = _mousePumpTask;
        }
        RetireNotificationChannel();
        if (mousePump is not null)
        {
            var completed = await Task.WhenAny(mousePump,
                Task.Delay(MousePumpStopTimeout)).ConfigureAwait(false);
            if (completed != mousePump)
                DiagnosticLogger.ReverseControlWarning("bluetooth",
                    "mouse_pump_release_timeout");
        }
        // Keep the wait bounded. Shutdown can ignore a failed release and
        // disconnect; callers retaining the connection must check the result.
        try
        {
            if (await _mouseNotificationTransportGate.WaitAsync(
                    MousePumpStopTimeout).ConfigureAwait(false))
                _mouseNotificationTransportGate.Release();
        }
        catch (ObjectDisposedException) { }
        var releaseActive = 1;
        bool SameRoute() => generation == Volatile.Read(ref _mousePumpGeneration) &&
            routeGeneration == Volatile.Read(ref _routeGeneration) &&
            string.Equals(targetDevice, Volatile.Read(ref _targetDeviceUdid), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(targetClient, Volatile.Read(ref _targetClientId), StringComparison.OrdinalIgnoreCase);
        bool CanRelease() => Volatile.Read(ref releaseActive) != 0 &&
            SameRoute();
        var releaseTask = Task.Run(async () =>
        {
            try
            {
                // A paused connection cannot rely on disconnect to clear a
                // missed release. Require mouse delivery and every report that
                // has submitted input in this session: a cached zero can be an
                // unsuccessful key-up, not proof the phone released the key.
                // Unused optional reports need no subscriber.
                var mouseReportId = UsesAbsoluteMouse ? RelativeMouseReportId : (byte)2;
                var reports = new (byte Id, byte[] Payload)[]
                {
                    (mouseReportId, MouseButtonStateReport(0, null)),
                    (1, new byte[8]), (4, [0, 0]), (5, [0, 0]),
                };
                var results = await Task.WhenAll(reports
                    .Where(report => !requireDelivery || report.Id == mouseReportId ||
                        (Volatile.Read(ref _submittedInputReports) & (1 << report.Id)) != 0 ||
                        (_lastReports.TryGetValue(report.Id, out var previous) && previous.Any(value => value != 0)))
                    .Select(report => _sendInputReport(report.Id, report.Payload, CanRelease))).ConfigureAwait(false);
                return results.All(sent => sent);
            }
            catch (Exception error)
            {
                LogGattCallbackFailure("release_reports", error);
                return false;
            }
        });
        var releaseCompleted = await Task.WhenAny(releaseTask,
            Task.Delay(ReleaseReportsTimeout)).ConfigureAwait(false) == releaseTask;
        var released = releaseCompleted && await releaseTask.ConfigureAwait(false);
        if (!releaseCompleted)
        {
            DiagnosticLogger.ReverseControlWarning("bluetooth",
                "release_reports_timeout");
        }
        Volatile.Write(ref releaseActive, 0);
        if (!keepPumpStopped)
        {
            lock (_mousePumpSync)
            {
                if (generation == _mousePumpGeneration)
                {
                    _mousePumpStopping = false;
                    _mousePumpRunning = false;
                    _mousePumpTask = null;
                }
            }
        }
        return released && SameRoute();
    }

    internal Task<bool> CalibrateAsync(string targetDeviceUdid,
        CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _routeGeneration);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Relative HID reports have no absolute position. Do not send a
            // synthetic "center" report: 0x8001 is -32767 in the HID map and
            // would physically move the iPhone cursor during startup.
            return Task.FromResult(IsCurrentRoute(targetDeviceUdid, generation) && IsMouseReady);
        }
        catch (OperationCanceledException) { return Task.FromResult(false); }
    }

    internal async Task<IReadOnlyList<BluetoothClientInfo>>
        GetSubscribedClientInfosAsync()
    {
        string[] clientIds;
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally { _targetClientGate.Release(); }

        // A phone can remain connected in Windows while the HID report
        // characteristics have not subscribed yet (for example immediately
        // after pairing or after the app restarts). Include connected BLE
        // devices so the binding page can show that device and let the user
        // explicitly bind it; routing still requires the stable device ID.
        try
        {
            var connected = await DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(
                    BluetoothConnectionStatus.Connected));
            var hidConnected = new List<string>();
            foreach (var device in connected)
            {
                if (await IsHidDeviceAsync(device.Id).ConfigureAwait(false))
                    hidConnected.Add(device.Id);
            }
            clientIds = clientIds.Concat(hidConnected)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception error)
        {
            DiagnosticLogger.Exception("bluetooth", "connected_device_enumeration_failed", error);
        }

        var clients = new List<BluetoothClientInfo>(clientIds.Length);
        foreach (var id in clientIds)
        {
            var name = await GetClientNameAsync(id).ConfigureAwait(false);
            var address = string.Empty;
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(id);
                if (device is not null)
                    address = device.BluetoothAddress.ToString("X12");
            }
            catch { }
            clients.Add(new BluetoothClientInfo(id, name, address,
                _clientConnectedAt.TryGetValue(id, out var connectedAt)
                    ? connectedAt : DateTimeOffset.Now));
        }
        return clients;
    }

    private static async Task<bool> IsHidDeviceAsync(string deviceId)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(deviceId);
            if (device is null) return false;
            var result = await device.GetGattServicesForUuidAsync(HidServiceUuid,
                BluetoothCacheMode.Uncached);
            return result.Status == GattCommunicationStatus.Success && result.Services.Count > 0;
        }
        catch { return false; }
    }

    internal async Task<bool> BindTargetClientAsync(string clientId)
    {
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsAdvertising || string.IsNullOrWhiteSpace(_targetDeviceUdid))
                return false;
            var clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id).ToArray();
            if (!_clientRoutes.SetBinding(_targetDeviceUdid, clientId, clientIds))
                return false;
            Volatile.Write(ref _targetClientId, clientId);
            AdvanceRouteGeneration();
            return true;
        }
        finally { _targetClientGate.Release(); }
    }

    internal Task<bool> BindTargetClientAsync(string targetDeviceUdid, string clientId)
    {
        if (!string.Equals(_targetDeviceUdid, targetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);
        return BindTargetClientAsync(clientId);
    }

    public Task SendKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages) =>
        QueueKeyboardAsync(modifiers, usages, null);

    private Task QueueKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages,
        Func<bool>? canSend)
    {
        if (!CanQueueReports) return Task.CompletedTask;
        canSend = CaptureReportSessionGuard(canSend);
        var report = new byte[8];
        report[0] = modifiers;
        var index = 2;
        foreach (var usage in usages.Take(6)) report[index++] = usage;
        var admission = InputAdmission?.Invoke();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping || !CanQueueReports) completion.TrySetResult(true);
            else
            {
                _keyboardPriorityReports.Enqueue((1, report, completion, canSend));
                if (!_mousePumpRunning)
                {
                    _mousePumpRunning = true;
                    StartReportPumpWhileLocked();
                }
            }
        }
        return FinishAdmittedInput(completion.Task, admission);
    }

    internal Task SendKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages,
        string? expectedTargetDeviceUdid, Func<bool>? canSend = null)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return QueueKeyboardAsync(modifiers, usages, canSend);
    }

    internal DirectKeyboardRoute CaptureKeyboardRoute(string target)
    {
        var generation = Volatile.Read(ref _routeGeneration);
        bool Current() => generation == Volatile.Read(ref _routeGeneration) &&
            string.Equals(target, _targetDeviceUdid, StringComparison.OrdinalIgnoreCase);
        return new(target, "BluetoothDirect", this, generation, Current,
            (modifiers, usages, canSend) => QueueKeyboardAsync(modifiers, usages,
                () => Current() && canSend?.Invoke() != false));
    }

    private Func<bool> CaptureReportSessionGuard(Func<bool>? canSend = null)
    {
        var generation = Volatile.Read(ref _routeGeneration);
        // F9 retains the BLE route but replaces the input session. Fence both
        // producers suspended at admission and shortcut finally-block releases.
        var inputGeneration = Volatile.Read(ref _mousePumpGeneration);
        var target = _targetDeviceUdid;
        return () => generation == Volatile.Read(ref _routeGeneration) &&
            inputGeneration == Volatile.Read(ref _mousePumpGeneration) &&
            string.Equals(target, _targetDeviceUdid, StringComparison.OrdinalIgnoreCase) && canSend?.Invoke() != false;
    }

    private Task SendConsumerAsync(ushort usage, Func<bool>? canSend = null)
    {
        if (!CanQueueReports) return Task.CompletedTask;
        canSend = CaptureReportSessionGuard(canSend);
        var report = new[] { (byte)(usage & 0xFF), (byte)(usage >> 8) };
        var admission = InputAdmission?.Invoke();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping || !CanQueueReports) completion.TrySetResult(true);
            else
            {
                _keyboardPriorityReports.Enqueue((4, report, completion, canSend));
                if (!_mousePumpRunning)
                {
                    _mousePumpRunning = true;
                    StartReportPumpWhileLocked();
                }
            }
        }
        return FinishAdmittedInput(completion.Task, admission);
    }

    private Task SendNavigationAsync(ushort controls, Func<bool>? canSend = null)
    {
        if (!CanQueueReports) return Task.CompletedTask;
        canSend = CaptureReportSessionGuard(canSend);
        var report = new[] { (byte)(controls & 0xFF), (byte)(controls >> 8) };
        var admission = InputAdmission?.Invoke();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping || !CanQueueReports) completion.TrySetResult(true);
            else
            {
                _keyboardPriorityReports.Enqueue((5, report, completion, canSend));
                if (!_mousePumpRunning)
                {
                    _mousePumpRunning = true;
                    StartReportPumpWhileLocked();
                }
            }
        }
        return FinishAdmittedInput(completion.Task, admission);
    }

    internal async Task SendIphoneSystemShortcutAsync(byte keyboardUsage,
        Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return;
        var session = CaptureReportSessionGuard();
        canSend = CaptureReportSessionGuard(canSend);
        Exception? failure = null;
        try
        {
            // Queue both pressed reports before awaiting either notification.
            // iPadOS recognizes Globe/Fn shortcuts only while the keyboard-layout
            // consumer control and keyboard usage overlap.
            var modifierPressed = SendConsumerAsync(GlobeKeyboardLayoutUsage, canSend);
            var keyPressed = QueueKeyboardAsync(0, [keyboardUsage], canSend);
            await Task.WhenAll(modifierPressed, keyPressed).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }

        try
        {
            var keyReleased = QueueKeyboardAsync(0, [], session);
            var modifierReleased = SendConsumerAsync(0, session);
            await Task.WhenAll(keyReleased, modifierReleased).ConfigureAwait(false);
        }
        catch (Exception error) { failure ??= error; }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal Task SendIphoneSystemShortcutAsync(byte keyboardUsage,
        string? expectedTargetDeviceUdid, Func<bool>? canSend = null)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return SendIphoneSystemShortcutAsync(keyboardUsage, canSend);
    }

    internal async Task SendIphoneAppSwitcherAsync(Func<bool>? canSend = null)
    {
        canSend = CaptureReportSessionGuard(canSend);
        await SendNavigationControlAsync(NavigationMenu, canSend).ConfigureAwait(false);
        await Task.Delay(AppSwitcherDoublePressInterval).ConfigureAwait(false);
        await SendNavigationControlAsync(NavigationMenu, canSend).ConfigureAwait(false);
    }

    internal Task SendIphoneAppSwitcherAsync(string? expectedTargetDeviceUdid,
        Func<bool>? canSend = null)
    {
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        return SendIphoneAppSwitcherAsync(canSend);
    }

    internal async Task SendIphoneConsumerShortcutAsync(ushort usage, int holdMs,
        string? expectedTargetDeviceUdid, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return;
        var session = CaptureReportSessionGuard();
        canSend = CaptureReportSessionGuard(canSend);
        if (expectedTargetDeviceUdid is not null &&
            !string.Equals(_targetDeviceUdid, expectedTargetDeviceUdid,
                StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await SendConsumerAsync(usage, canSend).ConfigureAwait(false);
            await Task.Delay(holdMs).ConfigureAwait(false);
        }
        finally { await SendConsumerAsync(0, session).ConfigureAwait(false); }
    }

    private async Task SendNavigationControlAsync(ushort controls, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return;
        var session = CaptureReportSessionGuard();
        canSend = CaptureReportSessionGuard(canSend);
        Exception? failure = null;
        try { await SendNavigationAsync(controls, canSend).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        try { await SendNavigationAsync(0, session).ConfigureAwait(false); }
        catch (Exception error) { failure ??= error; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task CreateCharacteristicsAsync(GattLocalService service)
    {
        _protocolModeCharacteristic = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.ProtocolMode,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
            DefaultProtocolMode);
        _protocolModeCharacteristic.WriteRequested += OnProtocolModeWriteRequested;
        _protocolModeCharacteristic.ReadRequested += (sender, args) =>
            RespondToReadAsync(args, () => GetProtocolModeValue(args));
        _wheelResolutionCharacteristic = await CreateFeatureCharacteristicAsync(service);

        var hidInfo = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.HidInformation,
            GattCharacteristicProperties.Read, HidInformation);
        hidInfo.ReadRequested += (_, args) => RespondToReadAsync(args, () => HidInformation);
        _ = hidInfo;
        var descriptor = UsesAbsoluteMouse ? CreateAbsoluteMouseReportMap() : ReportMap;
        if (UsesAbsoluteMouse) descriptor = AddRelativeMouseReport(descriptor);
        var reportMap = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.ReportMap,
            GattCharacteristicProperties.Read, descriptor);
        reportMap.ReadRequested += (_, args) => RespondToReadAsync(args, () => descriptor);
        _ = reportMap;
        var controlPoint = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.HidControlPoint,
            GattCharacteristicProperties.WriteWithoutResponse, [0x00]);
        controlPoint.WriteRequested += OnControlPointWriteRequested;

        _keyboardReport = await CreateReportCharacteristicAsync(service, 0x01);
        _mouseReport = await CreateReportCharacteristicAsync(service, 0x02);
        if (UsesAbsoluteMouse)
        {
            _relativeMouseReport = await CreateReportCharacteristicAsync(service, RelativeMouseReportId);
            _relativeMouseReport.ReadRequested += (_, args) => RespondToReadAsync(args,
                () => GetLastReport(RelativeMouseReportId, [0, 0, 0, 0, 0, 0]));
            _relativeMouseReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        }
        _consumerReport = await CreateReportCharacteristicAsync(service, 0x04);
        _navigationReport = await CreateReportCharacteristicAsync(service, 0x05);
        _bootKeyboardInput = await CreateCharacteristicAsync(service,
            BootKeyboardInputUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            [0, 0, 0, 0, 0, 0, 0, 0]);
        if (!UsesAbsoluteMouse)
        {
            _bootMouseInput = await CreateCharacteristicAsync(service,
                BootMouseInputUuid,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                [0, 0, 0]);
            _bootMouseInput.ReadRequested += (_, args) => RespondToReadAsync(args,
                () => ToBootMouseReport(GetLastReport(2, [0, 0, 0, 0, 0, 0])));
            _bootMouseInput.SubscribedClientsChanged += OnSubscribedClientsChanged;
        }
        _keyboardReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(1, [0, 0, 0, 0, 0, 0, 0, 0]));
        _consumerReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(4, [0, 0]));
        _navigationReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(5, [0, 0]));
        _mouseReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(2, [0, 0, 0, 0, 0, 0]));
        _bootKeyboardInput.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(1, [0, 0, 0, 0, 0, 0, 0, 0]));
        _mouseReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _keyboardReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _consumerReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _navigationReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _bootKeyboardInput.SubscribedClientsChanged += OnSubscribedClientsChanged;
    }

    private static async Task<GattLocalCharacteristic> CreateCharacteristicAsync(
        GattLocalService service, Guid uuid, GattCharacteristicProperties properties,
        byte[] value)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = properties,
            StaticValue = CryptographicBuffer.CreateFromByteArray(value),
            ReadProtectionLevel = properties.HasFlag(GattCharacteristicProperties.Read)
                ? GattProtectionLevel.EncryptionRequired : GattProtectionLevel.Plain,
            WriteProtectionLevel = properties.HasFlag(GattCharacteristicProperties.Write) ||
                properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattProtectionLevel.EncryptionRequired : GattProtectionLevel.Plain,
        };
        var result = await service.CreateCharacteristicAsync(uuid, parameters);
        if (result.Error != BluetoothError.Success || result.Characteristic is null)
            throw new InvalidOperationException(LocalizationService.Format("BluetoothHidCharacteristicFailedFormat", uuid, result.Error));
        return result.Characteristic;
    }

    private static async Task<GattLocalCharacteristic> CreateReportCharacteristicAsync(
        GattLocalService service, byte reportId)
    {
        var characteristic = await CreateCharacteristicAsync(service, ReportUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            reportId == 1 ? [0, 0, 0, 0, 0, 0, 0, 0] :
                reportId is 4 or 5 ? [0, 0] : [0, 0, 0, 0, 0, 0]);
        var descriptor = new GattLocalDescriptorParameters
        {
            StaticValue = CryptographicBuffer.CreateFromByteArray([reportId, 0x01]),
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        };
        var result = await characteristic.CreateDescriptorAsync(ReportReferenceUuid, descriptor);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException(LocalizationService.Format("BluetoothHidReportDescriptorFailedFormat", result.Error));
        return characteristic;
    }

    private async Task<bool> SendReportAsync(byte reportId, byte[] report,
        string? expectedTargetDeviceUdid = null, int? expectedGeneration = null,
        Func<bool>? canSend = null)
    {
        // A focus-expired report is intentionally discarded, not a transport
        // failure. Complete it normally so the queued release can proceed.
        if (canSend?.Invoke() == false) return true;
        GattLocalCharacteristic? characteristic = null;
        GattSubscribedClient? target = null;
        byte[]? payload = null;
        string? routeDeviceUdid = null;
        string? routeClientId = null;
        var routeGeneration = 0;
        if (!await _targetClientGate.WaitAsync(TargetClientGateTimeout)
                .ConfigureAwait(false))
        {
            DiagnosticLogger.ReverseControlWarning("bluetooth",
                "input_report_target_gate_timeout",
                ("report_id", reportId));
            return false;
        }
        try
        {
            if (canSend?.Invoke() == false) return true;
            if (!IsAdvertising) return false;
            routeDeviceUdid = _targetDeviceUdid;
            if (string.IsNullOrWhiteSpace(routeDeviceUdid)) return false;
            if (expectedTargetDeviceUdid is not null &&
                (!string.Equals(routeDeviceUdid, expectedTargetDeviceUdid,
                    StringComparison.OrdinalIgnoreCase) ||
                 expectedGeneration != Volatile.Read(ref _routeGeneration)))
                return false;
            routeGeneration = Volatile.Read(ref _routeGeneration);
            // Mouse reports retain their existing cache format (including
            // boot conversion). Keyboard/consumer state is published only
            // after the final focus check in the notification channel.
            if (reportId == 2 && !UsesAbsoluteMouse) _lastReports[reportId] = report;
            if (reportId == 2)
            {
                characteristic = SelectTargetCharacteristic(_mouseReport,
                    _bootMouseInput);
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                var state = GetClientState(target.Session.DeviceId.Id);
                if (UsesAbsoluteMouse && state.ProtocolMode == 0) return false;
                if (state.ProtocolMode == 0 &&
                    ReferenceEquals(characteristic, _mouseReport) &&
                    HasTargetSubscriber(_bootMouseInput))
                {
                    characteristic = _bootMouseInput;
                    target = FindTargetSubscriber(characteristic);
                }
                payload = ReferenceEquals(characteristic, _bootMouseInput)
                    ? ToBootMouseReport(report) : report;
            }
            else if (reportId == RelativeMouseReportId && UsesAbsoluteMouse)
            {
                characteristic = _relativeMouseReport;
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                payload = report;
            }
            else if (reportId == 1)
            {
                characteristic = SelectTargetCharacteristic(_keyboardReport,
                    _bootKeyboardInput);
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                var state = GetClientState(target.Session.DeviceId.Id);
                if (state.ProtocolMode == 0 && ReferenceEquals(characteristic, _keyboardReport) &&
                    HasTargetSubscriber(_bootKeyboardInput))
                {
                    characteristic = _bootKeyboardInput;
                    target = FindTargetSubscriber(characteristic);
                }
                payload = report;
            }
            else if (reportId == 4)
            {
                characteristic = _consumerReport;
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                payload = report;
            }
            else if (reportId == 5)
            {
                characteristic = _navigationReport;
                target = FindTargetSubscriber(characteristic);
                if (target is null) return false;
                payload = report;
            }
            else return false;
            routeClientId = target!.Session.DeviceId.Id;
        }
        finally
        {
            _targetClientGate.Release();
        }
        return await NotifyReportAsync(reportId, characteristic, payload!, target!,
            routeDeviceUdid!, routeGeneration, routeClientId!,
            reportId is 2 or RelativeMouseReportId ? MouseNotificationTimeout : NotificationTimeout, canSend)
            .ConfigureAwait(false);
    }

    private bool IsCurrentRoute(string targetDeviceUdid, int generation) =>
        Volatile.Read(ref _routeGeneration) == generation &&
        string.Equals(_targetDeviceUdid, targetDeviceUdid,
            StringComparison.OrdinalIgnoreCase) && IsAdvertising;

    private async Task<bool> NotifyReportAsync(byte reportId,
        GattLocalCharacteristic? characteristic,
        byte[] report, GattSubscribedClient targetClient, string expectedDeviceUdid,
        int expectedGeneration, string expectedClientId, TimeSpan timeout,
        Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return true;
        if (characteristic is null ||
            !IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration,
                expectedClientId)) return false;
        NotificationChannel channel;
        var isMouse = reportId is 2 or RelativeMouseReportId;
        lock (_notificationChannelSync)
        {
            channel = isMouse ? _mouseNotificationChannel : _notificationChannel;
            channel.Enter();
        }
        bool CheckNotificationResult(GattClientNotificationResult result)
        {
            if (NotificationSucceeded(result.Status, result.BytesSent, report.Length, result.ProtocolError))
                return true;
            if (!channel.Cancellation.IsCancellationRequested &&
                IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration, expectedClientId))
                HandleNotificationFailure(reportId, channel, new IOException(
                    $"Bluetooth HID notification failed: status={result.Status}, bytes={result.BytesSent}/{report.Length}, protocol_error={result.ProtocolError}."),
                    expectedGeneration);
            return false;
        }
        try
        {
            if (!IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration,
                    expectedClientId)) return false;
            var buffer = CryptographicBuffer.CreateFromByteArray(report);
            bool acquired;
            if (isMouse)
            {
                try
                {
                    acquired = await _mouseNotificationTransportGate.WaitAsync(
                        timeout, channel.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            else
            {
                try
                {
                    acquired = await channel.Gate.WaitAsync(timeout,
                        channel.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            if (!acquired)
            {
                if (IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration, expectedClientId) &&
                    !channel.Cancellation.IsCancellationRequested)
                    HandleNotificationFailure(reportId, channel,
                        new TimeoutException("The Bluetooth HID notification gate timed out."), expectedGeneration);
                return false;
            }
            var gateReleased = false;
            try
            {
                // The notification gate may have waited behind an earlier
                // report. Revalidate immediately before submitting to WinRT.
                if (canSend?.Invoke() == false) return true;
                if (!IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration,
                        expectedClientId)) return false;
                Interlocked.Or(ref _submittedInputReports, 1 << reportId);
                if (reportId != 2) _lastReports[reportId] = report;
                if (isMouse)
                {
                    // Do not pass a cancellation token to the native mouse
                    // operation. It cannot be cancelled reliably, so watch it
                    // separately and retain the transport slot until it ends.
                    var notifyTask = characteristic.NotifyValueAsync(buffer,
                        targetClient).AsTask();
                    if (!await WaitForMouseNotificationAsync(notifyTask, timeout,
                            channel.Cancellation.Token).ConfigureAwait(false))
                    {
                        // A cancelled managed wait does not end the native call.
                        // Its continuation owns the sole transport slot until
                        // completion, even across stop/restart or route changes.
                        gateReleased = true;
                        _ = ReleaseMouseNotificationGateAsync(notifyTask);
                        if (!channel.Cancellation.IsCancellationRequested &&
                            IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration, expectedClientId))
                            HandleNotificationFailure(reportId, channel,
                                new TimeoutException("The Bluetooth HID mouse notification timed out."), expectedGeneration);
                        return false;
                    }
                    var result = await notifyTask.ConfigureAwait(false);
                    return CheckNotificationResult(result);
                }
                using var notificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    channel.Cancellation.Token);
                notificationTimeout.CancelAfter(timeout);
                try
                {
                    var result = await characteristic.NotifyValueAsync(buffer, targetClient)
                        .AsTask(notificationTimeout.Token).ConfigureAwait(false);
                    return CheckNotificationResult(result);
                }
                catch (OperationCanceledException) when (notificationTimeout.IsCancellationRequested)
                {
                    if (!channel.Cancellation.IsCancellationRequested)
                        HandleNotificationFailure(reportId, channel, new TimeoutException(
                            "The Bluetooth HID notification timed out."), expectedGeneration);
                    return false;
                }
            }
            finally
            {
                if (!gateReleased)
                {
                    if (isMouse) _mouseNotificationTransportGate.Release();
                    else channel.Gate.Release();
                }
            }
        }
        catch (Exception error)
        {
            if (!channel.Cancellation.IsCancellationRequested &&
                IsCurrentRouteForClient(expectedDeviceUdid, expectedGeneration, expectedClientId))
                HandleNotificationFailure(reportId, channel, error, expectedGeneration);
            return false;
        }
        finally { channel.Exit(); }
    }

    internal static async Task<bool> WaitForMouseNotificationAsync(Task notification,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await notification.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!notification.IsCompleted) { return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    internal static bool NotificationSucceeded(GattCommunicationStatus status, ushort bytesSent,
        int expectedBytes, byte? protocolError = null) =>
        status == GattCommunicationStatus.Success && protocolError is null && bytesSent == expectedBytes;

    private async Task ReleaseMouseNotificationGateAsync(Task notifyTask)
    {
        try { await notifyTask.ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            try { _mouseNotificationTransportGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void HandleNotificationFailure(byte reportId,
        NotificationChannel channel, Exception error, int failedRouteGeneration)
    {
        if (failedRouteGeneration != Volatile.Read(ref _routeGeneration)) return;
        if (reportId is 2 or RelativeMouseReportId)
        {
            // A notification beyond the watchdog is no longer usable for
            // interactive control. Retire it and release the local input route;
            // the native operation still owns its transport slot until it ends.
            RetireNotificationChannel(channel);
            LogMouseNotificationStall();
            ScheduleMouseStallRecovery(failedRouteGeneration);
            return;
        }
        MarkNotificationFailure(channel, error);
    }

    private void LogMouseNotificationStall()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastMouseStallLogTimestamp);
        if (last != 0 && (now - last) * 1000.0 / Stopwatch.Frequency <
                MouseStallLogInterval.TotalMilliseconds) return;
        Interlocked.Exchange(ref _lastMouseStallLogTimestamp, now);
        DiagnosticLogger.ReverseControlWarning("bluetooth", "mouse_notification_stall",
            ("timeout_ms", MouseNotificationTimeout.TotalMilliseconds));
    }

    private void ScheduleMouseStallRecovery(int failedRouteGeneration)
    {
        if (Interlocked.Exchange(ref _mouseStallRecoveryInProgress, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            var gateHeld = false;
            try
            {
                gateHeld = await _gate.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (!gateHeld || Volatile.Read(ref _disposed) != 0 ||
                    failedRouteGeneration != Volatile.Read(ref _routeGeneration)) return;
                // The WinRT call is retired independently; stop the retry loop
                // immediately and let the owner tear down the broken route.
                Interlocked.Exchange(ref _transportFailed, 1);
                RetireNotificationChannel();
                lock (_mousePumpSync)
                {
                    Interlocked.Increment(ref _mousePumpGeneration);
                    _mousePumpStopping = true;
                    _mousePumpRunning = false;
                    _pendingMouseReport = null;
                    _pendingMouseCanSend = null;
                    _pendingMouseReportTimestamp = 0;
                    _mousePriorityReports.Clear();
                    while (_keyboardPriorityReports.Count > 0)
                        _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
                    _mouseStateRetryAttempts = 0;
                }
                DiagnosticLogger.ReverseControlWarning("bluetooth",
                    "mouse_stall_recovery_begin");
                StopAndClearProviderState(clearProvider: false);
                // BeginStopAdvertisingSession resets the flag. Publish failure
                // after teardown so the owner always releases raw-input capture.
                Interlocked.Exchange(ref _transportFailed, 1);
                SetStatus(LocalizationService.Get("BluetoothHidNotificationStalled"),
                    LocalizationService.Get("BluetoothHidRouteResetting"));
                DiagnosticLogger.ReverseControl("bluetooth",
                    "mouse_stall_recovery_ready");
            }
            catch (Exception error)
            {
                DiagnosticLogger.ReverseControlError("bluetooth",
                    "mouse_stall_recovery_failed", ("error", error.Message));
            }
            finally
            {
                if (gateHeld) _gate.Release();
                Volatile.Write(ref _mouseStallRecoveryInProgress, 0);
            }
        });
    }

    private bool IsCurrentRouteForClient(string deviceUdid, int generation,
        string clientId) => IsCurrentRoute(deviceUdid, generation) &&
        string.Equals(Volatile.Read(ref _targetClientId), clientId,
            StringComparison.OrdinalIgnoreCase);

    private void MarkNotificationFailure(NotificationChannel channel,
        Exception error)
    {
        RetireNotificationChannel(channel);
        Interlocked.Exchange(ref _transportFailed, 1);
        SetStatus(LocalizationService.Get("BluetoothHidDisconnected"), error.Message);
    }

    private void AdvanceRouteGeneration()
    {
        Interlocked.Increment(ref _routeGeneration);
        RetireNotificationChannel();
    }

    private void RetireNotificationChannel(NotificationChannel? expected = null)
    {
        NotificationChannel[] retired;
        lock (_notificationChannelSync)
        {
            if (expected is not null && ReferenceEquals(_notificationChannel, expected))
            {
                retired = [_notificationChannel];
                _notificationChannel = new NotificationChannel();
            }
            else if (expected is not null && ReferenceEquals(_mouseNotificationChannel, expected))
            {
                retired = [_mouseNotificationChannel];
                _mouseNotificationChannel = new NotificationChannel();
            }
            else if (expected is not null)
            {
                retired = [expected];
            }
            else
            {
                retired = [_notificationChannel, _mouseNotificationChannel];
                _notificationChannel = new NotificationChannel();
                _mouseNotificationChannel = new NotificationChannel();
            }
        }
        foreach (var channel in retired) channel.Retire();
    }

    private byte[] GetLastReport(byte reportId, byte[] fallback) =>
        _lastReports.TryGetValue(reportId, out var report) ? report : fallback;

    private static byte[] ToBootMouseReport(byte[] report)
    {
        if (report.Length < 5) return [0, 0, 0];
        var x = (short)(report[1] | report[2] << 8);
        var y = (short)(report[3] | report[4] << 8);
        return
        [
            (byte)(report[0] & 0x07),
            unchecked((byte)(sbyte)Math.Clamp(x, sbyte.MinValue, sbyte.MaxValue)),
            unchecked((byte)(sbyte)Math.Clamp(y, sbyte.MinValue, sbyte.MaxValue)),
        ];
    }

    private void StopAndClearProviderState(bool clearProvider = true)
    {
        AdvanceRouteGeneration();
        BeginStopAdvertisingSession();
        if (clearProvider && _provider is not null)
        {
            _provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
            _provider = null;
            _mouseReport = null;
            _relativeMouseReport = null;
            _keyboardReport = null;
            _consumerReport = null;
            _navigationReport = null;
            _bootMouseInput = null;
            _bootKeyboardInput = null;
            _protocolModeCharacteristic = null;
            _wheelResolutionCharacteristic = null;
        }
        DetachClientSessions();
        _clientStates.Clear();
        _clientRoutes.Clear();
        Volatile.Write(ref _targetClientId, null);
        _targetDeviceUdid = null;
    }

    private async Task<bool> StopAdvertisingSessionAsync()
    {
        var stopped = BeginStopAdvertisingSession();
        if (stopped is null) return true;
        try
        {
            var completed = await Task.WhenAny(stopped.Task,
                Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            return completed == stopped.Task && await stopped.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_advertisingStopped, stopped))
                _advertisingStopped = null;
        }
    }

    private TaskCompletionSource<bool>? BeginStopAdvertisingSession()
    {
        AdvanceRouteGeneration();
        Interlocked.Exchange(ref _advertisingStopRequested, 1);
        _advertisingStarted?.TrySetResult(false);
        _advertisingStarted = null;
        _advertisingStopped?.TrySetResult(false);
        _advertisingStopped = null;
        _clientConnected?.TrySetResult(false);
        _clientConnected = null;
        _clientRoutes.EndTarget();
        Volatile.Write(ref _targetClientId, null);
        _targetDeviceUdid = null;
        _targetDeviceName = null;
        _preferredClientId = null;
        Interlocked.Exchange(ref _transportFailed, 0);
        _lastReports.Clear();
        Interlocked.Exchange(ref _submittedInputReports, 0);
        if (!IsAdvertising)
        {
            return null;
        }
        var stopped = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _advertisingStopped = stopped;
        try { _provider?.StopAdvertising(); }
        catch
        {
            stopped.TrySetResult(false);
        }
        return stopped;
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender,
        GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _provider)) return;
        if (args.Status == GattServiceProviderAdvertisementStatus.Started ||
            args.Status == GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
        {
            if (Volatile.Read(ref _advertisingStopRequested) != 0) return;
            Interlocked.Exchange(ref _transportFailed, 0);
            _advertisingStarted?.TrySetResult(true);
            SetStatus(LocalizationService.Format("BluetoothHidAdvertisingFormat", args.Status), null);
        }
        else if (args.Status is GattServiceProviderAdvertisementStatus.Stopped or
                 GattServiceProviderAdvertisementStatus.Aborted)
        {
            if (Volatile.Read(ref _advertisingStopRequested) != 0)
            {
                _advertisingStopped?.TrySetResult(true);
                return;
            }
            // MediaTek/Windows can report an Aborted/Success transition while
            // replacing the advertisement and then report Started immediately
            // afterwards. Do not poison a startup that is still in progress.
            if (Volatile.Read(ref _advertisingStartupInProgress) != 0)
                return;
            if (IsAdvertising) return;
            Interlocked.Exchange(ref _transportFailed, 1);
            SetStatus(LocalizationService.Get("BluetoothHidStoppedUnexpectedly"), args.Error.ToString());
        }
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        // This handler runs on the WinRT GATT callback thread. Calling back
        // into the Bluetooth stack from that thread (e.g. reading
        // SubscribedClients inside TrackSubscribedClients) raises
        // RPC_E_CANTCALLOUT_ININPUTSYNCCALL, which can destabilize the
        // dispatcher. Offload the refresh to the thread pool so no WinRT
        // call is made re-entrantly from the callback.
        TrackClientRefresh(() => Task.Run(() => RefreshSubscribedClientsAsync(sender)));
    }

    private async Task RefreshSubscribedClientsAsync(GattLocalCharacteristic sender)
    {
        await _clientRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TrackSubscribedClients();
            await RefreshTargetClientAsync().ConfigureAwait(false);
            if (!IsCurrentCharacteristic(sender)) return;
            if (HasTargetInputSubscriber())
                Interlocked.Exchange(ref _transportFailed, 0);
            var connected = IsConnected;
            if (IsMouseConnected) _clientConnected?.TrySetResult(true);
            SetStatus(connected
                    ? LocalizationService.Format("BluetoothHidSubscribedFormat", GetReportName(sender))
                    : HasAnySubscriber
                        ? Volatile.Read(ref _targetClientId) is null
                            ? LocalizationService.Get("BluetoothHidMultipleClientsWaiting")
                            : LocalizationService.Get("BluetoothHidWaitingMouseReport")
                        : LocalizationService.Get("BluetoothHidWaitingClient"),
                null);
            // Do not probe a freshly subscribed iPhone with synthetic zero
            // notifications. iOS/Windows can still be completing the HID
            // subscription when the callback arrives; a probe at that point
            // is reported as a transport failure and aborts an otherwise
            // healthy connection. The first real input report is the probe.
        }
        catch (Exception error)
        {
            SetStatus(LocalizationService.Get("BluetoothHidClientIdentificationFailed"), error.Message);
        }
        finally
        {
            _clientRefreshGate.Release();
        }
    }

    private static bool HasSubscribers(GattLocalCharacteristic? characteristic)
    {
        try
        {
            return characteristic?.SubscribedClients?.Count > 0;
        }
        catch (Exception)
        {
            // WinRT can reject a synchronous SubscribedClients call while its
            // Bluetooth service is processing a callback (RPC_E_CANTCALLOUT_ININPUTSYNCCALL).
            // Treat the snapshot as unavailable; never let it escape through a
            // WPF property getter and destabilize the dispatcher.
            return false;
        }
    }

    private bool IsCurrentCharacteristic(GattLocalCharacteristic characteristic) =>
        ReferenceEquals(characteristic, _mouseReport) ||
        ReferenceEquals(characteristic, _relativeMouseReport) ||
        ReferenceEquals(characteristic, _keyboardReport) ||
        ReferenceEquals(characteristic, _consumerReport) ||
        ReferenceEquals(characteristic, _navigationReport) ||
        ReferenceEquals(characteristic, _bootMouseInput) ||
        ReferenceEquals(characteristic, _bootKeyboardInput);

    private bool HasTargetSubscriber(GattLocalCharacteristic? characteristic) =>
        FindTargetSubscriber(characteristic) is not null;

    private bool HasTargetInputSubscriber() => HasTargetSubscriber(_mouseReport) ||
        HasTargetSubscriber(_relativeMouseReport) || HasTargetSubscriber(_keyboardReport) || HasTargetSubscriber(_bootMouseInput) ||
        HasTargetSubscriber(_bootKeyboardInput) || HasTargetSubscriber(_consumerReport) ||
        HasTargetSubscriber(_navigationReport);

    private GattSubscribedClient? FindTargetSubscriber(
        GattLocalCharacteristic? characteristic)
    {
        var targetClientId = Volatile.Read(ref _targetClientId);
        if (characteristic is null || string.IsNullOrWhiteSpace(targetClientId))
            return null;
        try
        {
            return characteristic.SubscribedClients.FirstOrDefault(client =>
                string.Equals(client.Session.DeviceId.Id, targetClientId,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private GattLocalCharacteristic? SelectTargetCharacteristic(
        GattLocalCharacteristic? reportCharacteristic,
        GattLocalCharacteristic? bootCharacteristic)
    {
        // The report and boot characteristics each have their own
        // GattSubscribedClient object. Select the characteristic first, then
        // pass that characteristic's client to NotifyValueAsync; passing a
        // client obtained from another characteristic can fail on Windows.
        if (FindTargetSubscriber(reportCharacteristic) is not null)
            return reportCharacteristic;
        if (FindTargetSubscriber(bootCharacteristic) is not null)
            return bootCharacteristic;
        return null;
    }

    private async Task RefreshTargetClientAsync()
    {
        string? targetName;
        int generation;
        string[] clientIds;
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            generation = Volatile.Read(ref _routeGeneration);
            targetName = _targetDeviceName;
            clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _targetClientGate.Release();
        }

        var clients = new List<(string Id, string Name)>(clientIds.Length);
        foreach (var clientId in clientIds)
            clients.Add((clientId, await GetClientNameAsync(clientId).ConfigureAwait(false)));

        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _routeGeneration)) return;
            Volatile.Write(ref _targetClientId,
                _clientRoutes.Refresh(clients, targetName, _preferredClientId));
        }
        finally
        {
            _targetClientGate.Release();
        }
    }

    private async Task BeginTargetRouteAsync(string targetDeviceUdid,
        string? targetDeviceName = null,
        bool clearPreviousBinding = false,
        string? preferredClientId = null)
    {
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AdvanceRouteGeneration();
            var clientIds = EnumerateSubscribedClients()
                .Select(client => client.Session.DeviceId.Id)
                .ToArray();
            _clientRoutes.BeginTarget(targetDeviceUdid, clientIds, clearPreviousBinding);
            _targetDeviceUdid = targetDeviceUdid;
            _targetDeviceName = targetDeviceName;
            _preferredClientId = preferredClientId;
            Volatile.Write(ref _targetClientId,
                _clientRoutes.Refresh(clientIds.Select(id => (id, string.Empty)),
                    targetDeviceName, preferredClientId));
        }
        finally
        {
            _targetClientGate.Release();
        }
    }

    private static async Task<string> GetClientNameAsync(string clientId)
    {
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(clientId);
            if (!string.IsNullOrWhiteSpace(device?.Name)) return device.Name;
        }
        catch { }
        try
        {
            var information = await DeviceInformation.CreateFromIdAsync(clientId);
            return information?.Name ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private IEnumerable<GattSubscribedClient> EnumerateSubscribedClients()
    {
        foreach (var characteristic in new[]
                 { _mouseReport, _relativeMouseReport, _keyboardReport, _consumerReport,
                   _navigationReport,
                   _bootMouseInput, _bootKeyboardInput })
        {
            if (characteristic is null) continue;
            foreach (var client in characteristic.SubscribedClients)
                yield return client;
        }
    }

    private void TrackSubscribedClients()
    {
        foreach (var client in EnumerateSubscribedClients())
        {
            var id = client.Session.DeviceId.Id;
            if (string.IsNullOrWhiteSpace(id)) continue;
            _ = GetClientState(id);
            if (_clientSessions.TryGetValue(id, out var existing) &&
                ReferenceEquals(existing, client.Session))
                continue;
            if (existing is not null)
            {
                existing.SessionStatusChanged -= OnGattSessionStatusChanged;
                _clientSessions.TryRemove(id, out _);
            }
            _clientSessions[id] = client.Session;
            _clientConnectedAt[id] = DateTimeOffset.Now;
            client.Session.SessionStatusChanged += OnGattSessionStatusChanged;
        }
    }

    private void OnGattSessionStatusChanged(GattSession sender,
        GattSessionStatusChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            args.Status != GattSessionStatus.Closed) return;
        // Offload to the thread pool: reading SessionStatus / SubscribedClients
        // directly from the WinRT callback thread triggers the COM re-entrancy
        // rejection (RPC_E_CANTCALLOUT_ININPUTSYNCCALL).
        TrackClientRefresh(() => Task.Run(RefreshClosedSessionAsync));
    }

    private void TrackClientRefresh(Func<Task> factory)
    {
        lock (_clientRefreshTrackingSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var task = factory();
            if (task.IsCompleted)
                return;
            _clientRefreshTasks.TryAdd(task, 0);
            _ = task.ContinueWith(completed =>
            {
                lock (_clientRefreshTrackingSync)
                    _clientRefreshTasks.TryRemove(completed, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task DrainClientRefreshesAsync()
    {
        while (!_clientRefreshTasks.IsEmpty)
            await Task.WhenAll(_clientRefreshTasks.Keys.ToArray()).ConfigureAwait(false);
    }

    private async Task RefreshClosedSessionAsync()
    {
        await _clientRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var pair in _clientSessions.ToArray())
            {
                if (pair.Value.SessionStatus == GattSessionStatus.Closed &&
                    _clientSessions.TryRemove(pair.Key, out var removed))
                {
                    _clientConnectedAt.TryRemove(pair.Key, out _);
                    removed.SessionStatusChanged -= OnGattSessionStatusChanged;
                }
            }
            TrackSubscribedClients();
            await RefreshTargetClientAsync().ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                SetStatus(IsConnected
                        ? LocalizationService.Get("BluetoothHidSelectedConnected")
                        : HasAnySubscriber
                            ? LocalizationService.Get("BluetoothHidWaitingSelected")
                            : LocalizationService.Get("BluetoothHidWaitingClient"),
                    null);
                if (!IsConnected) _clientConnected?.TrySetResult(false);
            }
        }
        catch (Exception error)
        {
            SetStatus(LocalizationService.Get("BluetoothHidClientRefreshFailed"), error.Message);
        }
        finally
        {
            _clientRefreshGate.Release();
        }
    }

    private void DetachClientSessions()
    {
        foreach (var session in _clientSessions.Values)
            session.SessionStatusChanged -= OnGattSessionStatusChanged;
        _clientSessions.Clear();
    }

    private ClientState GetClientState(string clientId) =>
        _clientStates.GetOrAdd(clientId, static _ => new ClientState());

    private ClientState? GetTargetClientState()
    {
        var targetId = Volatile.Read(ref _targetClientId);
        return string.IsNullOrWhiteSpace(targetId) ? null :
            _clientStates.TryGetValue(targetId, out var state) ? state : null;
    }

    private byte[] GetProtocolModeValue(GattReadRequestedEventArgs args)
    {
        var id = args.Session?.DeviceId?.Id;
        return string.IsNullOrWhiteSpace(id)
            ? DefaultProtocolMode.ToArray()
            : [GetClientState(id).ProtocolMode];
    }

    private string GetReportName(GattLocalCharacteristic characteristic) =>
        ReferenceEquals(characteristic, _mouseReport) ? LocalizationService.Get("BluetoothHidReportMouse") :
        ReferenceEquals(characteristic, _bootMouseInput) ? LocalizationService.Get("BluetoothHidReportBootMouse") :
        ReferenceEquals(characteristic, _bootKeyboardInput) ? LocalizationService.Get("BluetoothHidReportBootKeyboard") :
        ReferenceEquals(characteristic, _consumerReport) ? LocalizationService.Get("BluetoothHidReportConsumer") :
        ReferenceEquals(characteristic, _navigationReport) ? LocalizationService.Get("BluetoothHidReportNavigation") :
        LocalizationService.Get("BluetoothHidReportKeyboard");


    private void OnProtocolModeWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            if (request is not null)
            {
                using var reader = DataReader.FromBuffer(request.Value);
                if (request.Value.Length > 0)
                {
                    var mode = reader.ReadByte();
                    if (mode is 0x00 or 0x01)
                    {
                        var id = args.Session?.DeviceId?.Id;
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            GetClientState(id).ProtocolMode = mode;
                            if (string.Equals(id, Volatile.Read(ref _targetClientId),
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                // The first real input report is sent on
                                // demand. Do not inject a synthetic report
                                // while iOS is still switching protocol mode.
                            }
                        }
                    }
                }
                request.Respond();
            }
        }, "protocol_mode_write");

    private void OnWheelResolutionWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            if (request is not null)
            {
                using var reader = DataReader.FromBuffer(request.Value);
                if (request.Value.Length > 0)
                {
                    var multiplier = (byte)Math.Clamp((int)reader.ReadByte(), 1, 10);
                    var id = args.Session?.DeviceId?.Id;
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        GetClientState(id).WheelResolutionMultiplier = multiplier;
                        if (string.Equals(id, Volatile.Read(ref _targetClientId),
                                StringComparison.OrdinalIgnoreCase))
                            SetStatus(LocalizationService.Format("BluetoothHidWheelMultiplierFormat", multiplier), null);
                    }
                }
                request.Respond();
            }
        }, "wheel_resolution_write");

    private async Task<GattLocalCharacteristic> CreateFeatureCharacteristicAsync(
        GattLocalService service)
    {
        var characteristic = await CreateCharacteristicAsync(service, ReportUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Write |
                GattCharacteristicProperties.WriteWithoutResponse,
            [1]);
        var descriptor = new GattLocalDescriptorParameters
        {
            StaticValue = CryptographicBuffer.CreateFromByteArray([3, 0x02]),
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        };
        var result = await characteristic.CreateDescriptorAsync(ReportReferenceUuid, descriptor);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException(LocalizationService.Format("BluetoothHidWheelDescriptorFailedFormat", result.Error));
        characteristic.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetWheelResolutionValue(args));
        characteristic.WriteRequested += OnWheelResolutionWriteRequested;
        return characteristic;
    }

    private byte[] GetWheelResolutionValue(GattReadRequestedEventArgs args)
    {
        var id = args.Session?.DeviceId?.Id;
        return string.IsNullOrWhiteSpace(id) ? [1] :
            [GetClientState(id).WheelResolutionMultiplier];
    }

    private static void OnControlPointWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            request?.Respond();
        }, "control_point_write");

    private static void RespondToReadAsync(GattReadRequestedEventArgs args,
        Func<byte[]> valueFactory) => _ = RunGattCallbackAsync(
        () => args.GetDeferral(), async () =>
        {
            var request = await args.GetRequestAsync();
            request?.RespondWithValue(CryptographicBuffer.CreateFromByteArray(valueFactory()));
        }, "characteristic_read");

    private static async Task RunGattCallbackAsync(Func<Deferral> getDeferral,
        Func<Task> callback, string operation)
    {
        Deferral? deferral = null;
        try
        {
            deferral = getDeferral();
            await callback().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogGattCallbackFailure(operation, error);
        }
        finally
        {
            if (deferral is not null)
            {
                try { deferral.Complete(); }
                catch (Exception error)
                {
                    LogGattCallbackFailure(operation + "_complete", error);
                }
            }
        }
    }

    private static void LogGattCallbackFailure(string operation, Exception error) =>
        DiagnosticLogger.ExceptionOnce(
            $"bluetooth-hid-gatt-{operation}-{error.GetType().FullName}",
            "bluetooth", "gatt_callback_failed", error,
            ("operation", operation));

    private void SetStatus(string status, string? error)
    {
        Status = status;
        Error = error;
        // Event subscribers can run on a native GATT callback thread. A UI
        // teardown or COM exception in one subscriber must not escape to WinRT
        // or prevent the remaining subscribers from releasing local input.
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception callbackError) { LogGattCallbackFailure("status_changed", callbackError); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_clientRefreshTrackingSync)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
        }
        await StopAsync().ConfigureAwait(false);
        await DrainClientRefreshesAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopAndClearProviderState(); }
        finally { _gate.Release(); }
        RetireNotificationChannel();
        _clientRefreshGate.Dispose();
        _targetClientGate.Dispose();
        _mouseNotificationTransportGate.Dispose();
        _gate.Dispose();
    }

    private sealed class ClientState
    {
        public byte ProtocolMode = 0x01;
        public byte WheelResolutionMultiplier = 1;
    }

    private sealed class NotificationChannel
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CancellationTokenSource Cancellation { get; } = new();
        private int _active;
        private int _retired;
        private int _disposed;

        internal void Enter() => Interlocked.Increment(ref _active);

        internal void Exit()
        {
            if (Interlocked.Decrement(ref _active) == 0)
                DisposeWhenIdle();
        }

        internal void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 0)
            {
                try { Cancellation.Cancel(); }
                catch (Exception) { }
            }
            DisposeWhenIdle();
        }

        private void DisposeWhenIdle()
        {
            if (Volatile.Read(ref _retired) == 0 ||
                Volatile.Read(ref _active) != 0 ||
                Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Gate.Dispose();
            Cancellation.Dispose();
        }
    }
}
