using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace IPhoneMirror.App.Services;

/// <summary>
/// Exposes the Windows Bluetooth radio as a standard BLE HID mouse/keyboard.
/// iOS consumes this as a normal pointer device when AssistiveTouch is enabled.
/// </summary>
internal sealed class BluetoothHidMouseService : IAsyncDisposable
{
    private static readonly Guid HidServiceUuid = GattServiceUuids.HumanInterfaceDevice;
    private static readonly Guid ReportUuid = GattCharacteristicUuids.Report;
    private static readonly Guid BootKeyboardInputUuid =
        Guid.Parse("00002a22-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BootMouseInputUuid =
        Guid.Parse("00002a33-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ReportReferenceUuid = Guid.Parse("00002908-0000-1000-8000-00805f9b34fb");
    private static readonly byte[] HidInformation = [0x11, 0x01, 0x00, 0x03];
    private static readonly byte[] ProtocolMode = [0x01];

    // Report 1 is keyboard and report 2 is mouse. Keeping both reports in one
    // HID service lets iOS expose pointer and keyboard input from one pairing.
    private static readonly byte[] ReportMap =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,
        0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x08, 0x81, 0x02, 0x75, 0x08, 0x95, 0x01,
        0x81, 0x01, 0x75, 0x08, 0x95, 0x06, 0x15, 0x00, 0x25, 0x65,
        0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00, 0xC0,
        0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, 0x02, 0x09, 0x01,
        0xA1, 0x00, 0x05, 0x09, 0x19, 0x01, 0x29, 0x03, 0x15, 0x00,
        0x25, 0x01, 0x75, 0x01, 0x95, 0x03, 0x81, 0x02, 0x75, 0x05,
        0x95, 0x01, 0x81, 0x01, 0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x01, 0x80, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02,
        0x81, 0x06, 0x09, 0x38, 0x15, 0x81, 0x25, 0x7F, 0x75, 0x08,
        0x95, 0x01, 0x81, 0x06,
        0x85, 0x03,
        0x09, 0x48, 0x15, 0x00, 0x25, 0x0A, 0x75, 0x08, 0x95, 0x01,
        0xB1, 0x02, 0xC0, 0xC0
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _notifyGate = new(1, 1);
    private readonly SemaphoreSlim _targetClientGate = new(1, 1);
    private readonly object _mousePumpSync = new();
    private readonly ConcurrentDictionary<byte, byte[]> _lastReports = new();
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _mouseReport;
    private GattLocalCharacteristic? _keyboardReport;
    private GattLocalCharacteristic? _bootMouseInput;
    private GattLocalCharacteristic? _bootKeyboardInput;
    private GattLocalCharacteristic? _protocolModeCharacteristic;
    private GattLocalCharacteristic? _wheelResolutionCharacteristic;
    private TaskCompletionSource<bool>? _advertisingStarted;
    private TaskCompletionSource<bool>? _advertisingStopped;
    private TaskCompletionSource<bool>? _clientConnected;
    private Task? _mousePumpTask;
    private byte[]? _pendingMouseReport;
    private readonly Queue<byte[]> _mousePriorityReports = new();
    private readonly Queue<(byte[] Report, TaskCompletionSource<bool> Completion)>
        _keyboardPriorityReports = new();
    private bool _mousePumpRunning;
    private bool _mousePumpStopping;
    private byte _lastQueuedMouseButtons;
    private int _transportFailed;
    private int _advertisingStopRequested;
    private int _disposed;
    private byte _protocolMode = 0x01;
    private byte _wheelResolutionMultiplier = 1;
    private string? _targetDeviceName;
    private string? _targetClientId;
    private string? _lastTargetClientId;
    private int _targetBindingGeneration;

    public bool IsAdvertising => _provider?.AdvertisementStatus is
        GattServiceProviderAdvertisementStatus.Started or
        GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
    public bool IsConnected => Volatile.Read(ref _transportFailed) == 0 && IsMouseConnected;
    public int WheelResolutionMultiplier => Volatile.Read(ref _wheelResolutionMultiplier);
    private bool IsMouseConnected => HasTargetSubscriber(_mouseReport) ||
        HasTargetSubscriber(_bootMouseInput);
    private bool HasAnySubscriber => HasSubscribers(_mouseReport) ||
        HasSubscribers(_bootMouseInput) || HasSubscribers(_keyboardReport) ||
        HasSubscribers(_bootKeyboardInput);
    public string SuggestedDeviceName { get; } = Environment.MachineName;
    public string Status { get; private set; } = "Bluetooth control is off";
    public string? Error { get; private set; }

    public event EventHandler? StatusChanged;

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

    public async Task<bool> StartAsync(string? targetDeviceName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestedTargetDeviceName = targetDeviceName?.Trim();
            Interlocked.Increment(ref _targetBindingGeneration);
            _targetDeviceName = requestedTargetDeviceName;
            Volatile.Write(ref _targetClientId, null);
            _lastTargetClientId = null;
            if (IsAdvertising && Volatile.Read(ref _advertisingStopRequested) == 0)
            {
                await RefreshTargetClientAsync().ConfigureAwait(false);
                return true;
            }
            if (IsAdvertising)
            {
                StopAndClearProviderState();
                _targetDeviceName = requestedTargetDeviceName;
            }
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported ||
                !adapter.IsPeripheralRoleSupported)
            {
                SetStatus("This Bluetooth adapter cannot act as a BLE peripheral.",
                    "The Bluetooth adapter does not support peripheral mode.");
                return false;
            }
            SetStatus("Bluetooth adapter supports BLE peripheral mode.", null);

            if (_provider is null)
            {
                var result = await GattServiceProvider.CreateAsync(HidServiceUuid);
                if (result.Error != BluetoothError.Success || result.ServiceProvider is null)
                {
                    SetStatus("Could not create the Bluetooth HID service.",
                        $"GattServiceProvider.CreateAsync returned {result.Error}.");
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
            _protocolMode = 0x01;
            _lastReports.Clear();
            _advertisingStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _provider.StartAdvertising(advertising);
            var completed = await Task.WhenAny(_advertisingStarted.Task,
                Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            if (completed != _advertisingStarted.Task || !IsAdvertising)
            {
                StopAndClearProviderState();
                SetStatus("Bluetooth HID control did not start advertising.",
                    "The Bluetooth stack did not report an advertising state.");
                return false;
            }
            SetStatus($"Bluetooth HID control is advertising ({_provider.AdvertisementStatus}).",
                null);
            await RefreshTargetClientAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            StopAndClearProviderState();
            SetStatus("Bluetooth HID control could not start.", error.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? mousePump;
        lock (_mousePumpSync)
        {
            _mousePumpStopping = true;
            _pendingMouseReport = null;
            _mousePriorityReports.Clear();
            while (_keyboardPriorityReports.Count > 0)
                _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
            mousePump = _mousePumpTask;
        }
        if (mousePump is not null)
            await mousePump.ConfigureAwait(false);
        lock (_mousePumpSync)
        {
            _mousePumpStopping = false;
            _mousePumpTask = null;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await StopAdvertisingSessionAsync().ConfigureAwait(false))
                StopAndClearProviderState();
            SetStatus("Bluetooth control is off", null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SendMouseAsync(int dx, int dy, byte buttons = 0, int wheel = 0)
    {
        var x = (short)Math.Clamp(dx, short.MinValue + 1, short.MaxValue);
        var y = (short)Math.Clamp(dy, short.MinValue + 1, short.MaxValue);
        var encodedWheel = Math.Clamp(wheel, -127, 127);
        if (!IsAdvertising) return Task.CompletedTask;
        byte[] report =
        [buttons, (byte)(x & 0xFF), (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF), (byte)((y >> 8) & 0xFF),
            unchecked((byte)(sbyte)encodedWheel)];
        lock (_mousePumpSync)
        {
            _lastReports[2] = report;
            if (buttons != _lastQueuedMouseButtons)
            {
                _lastQueuedMouseButtons = buttons;
                _pendingMouseReport = null;
                _mousePriorityReports.Enqueue(report);
            }
            else if (wheel != 0)
            {
                _mousePriorityReports.Enqueue(report);
            }
            else
            {
                _pendingMouseReport = report;
            }
            if (_mousePumpRunning) return Task.CompletedTask;
            _mousePumpRunning = true;
            _mousePumpTask = PumpReportsAsync();
        }
        return Task.CompletedTask;
    }

    private async Task PumpReportsAsync()
    {
        while (true)
        {
            byte[]? report = null;
            byte reportId = 2;
            TaskCompletionSource<bool>? completion = null;
            lock (_mousePumpSync)
            {
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
                    reportId = 1;
                }
                else if (_mousePriorityReports.Count > 0)
                {
                    report = _mousePriorityReports.Dequeue();
                }
                else if (_pendingMouseReport is not null)
                {
                    report = _pendingMouseReport;
                    _pendingMouseReport = null;
                }
                else
                {
                    _mousePumpRunning = false;
                    return;
                }
            }
            try
            {
                await SendReportAsync(reportId, report).ConfigureAwait(false);
                completion?.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion?.TrySetException(error);
            }
        }
    }

    internal async Task ReleaseAllAsync()
    {
        Task? mousePump;
        lock (_mousePumpSync)
        {
            _pendingMouseReport = null;
            _mousePriorityReports.Clear();
            _lastQueuedMouseButtons = 0;
            while (_keyboardPriorityReports.Count > 0)
                _keyboardPriorityReports.Dequeue().Completion.TrySetCanceled();
            mousePump = _mousePumpTask;
        }
        if (mousePump is not null)
            await mousePump.ConfigureAwait(false);
        await SendReportAsync(2, new byte[6]).ConfigureAwait(false);
        await SendReportAsync(1, new byte[8]).ConfigureAwait(false);
    }

    public Task SendKeyboardAsync(byte modifiers, IReadOnlyCollection<byte> usages)
    {
        if (!IsAdvertising) return Task.CompletedTask;
        var report = new byte[8];
        report[0] = modifiers;
        var index = 2;
        foreach (var usage in usages.Take(6)) report[index++] = usage;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mousePumpSync)
        {
            if (_mousePumpStopping) return Task.CompletedTask;
            _keyboardPriorityReports.Enqueue((report, completion));
            if (!_mousePumpRunning)
            {
                _mousePumpRunning = true;
                _mousePumpTask = PumpReportsAsync();
            }
        }
        return completion.Task;
    }

    private async Task CreateCharacteristicsAsync(GattLocalService service)
    {
        _protocolModeCharacteristic = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.ProtocolMode,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
            ProtocolMode);
        _protocolModeCharacteristic.WriteRequested += OnProtocolModeWriteRequested;
        _protocolModeCharacteristic.ReadRequested += (_, args) => RespondToReadAsync(args, () => [_protocolMode]);
        _wheelResolutionCharacteristic = await CreateFeatureCharacteristicAsync(service);

        var hidInfo = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.HidInformation,
            GattCharacteristicProperties.Read, HidInformation);
        hidInfo.ReadRequested += (_, args) => RespondToReadAsync(args, () => HidInformation);
        _ = hidInfo;
        var reportMap = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.ReportMap,
            GattCharacteristicProperties.Read, ReportMap);
        reportMap.ReadRequested += (_, args) => RespondToReadAsync(args, () => ReportMap);
        _ = reportMap;
        var controlPoint = await CreateCharacteristicAsync(service,
            GattCharacteristicUuids.HidControlPoint,
            GattCharacteristicProperties.WriteWithoutResponse, [0x00]);
        controlPoint.WriteRequested += OnControlPointWriteRequested;

        _keyboardReport = await CreateReportCharacteristicAsync(service, 0x01);
        _mouseReport = await CreateReportCharacteristicAsync(service, 0x02);
        _bootKeyboardInput = await CreateCharacteristicAsync(service,
            BootKeyboardInputUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            [0, 0, 0, 0, 0, 0, 0, 0]);
        _bootMouseInput = await CreateCharacteristicAsync(service,
            BootMouseInputUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            [0, 0, 0]);
        _keyboardReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(1, [0, 0, 0, 0, 0, 0, 0, 0]));
        _mouseReport.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(2, [0, 0, 0, 0, 0, 0]));
        _bootKeyboardInput.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => GetLastReport(1, [0, 0, 0, 0, 0, 0, 0, 0]));
        _bootMouseInput.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => ToBootMouseReport(GetLastReport(2, [0, 0, 0, 0, 0, 0])));
        _mouseReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _keyboardReport.SubscribedClientsChanged += OnSubscribedClientsChanged;
        _bootMouseInput.SubscribedClientsChanged += OnSubscribedClientsChanged;
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
            throw new InvalidOperationException($"Could not create HID characteristic {uuid}: {result.Error}");
        return result.Characteristic;
    }

    private static async Task<GattLocalCharacteristic> CreateReportCharacteristicAsync(
        GattLocalService service, byte reportId)
    {
        var characteristic = await CreateCharacteristicAsync(service, ReportUuid,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            reportId == 1 ? [0, 0, 0, 0, 0, 0, 0, 0] : [0, 0, 0, 0, 0, 0]);
        var descriptor = new GattLocalDescriptorParameters
        {
            StaticValue = CryptographicBuffer.CreateFromByteArray([reportId, 0x01]),
            ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
            WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
        };
        var result = await characteristic.CreateDescriptorAsync(ReportReferenceUuid, descriptor);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Could not create HID report descriptor: {result.Error}");
        return characteristic;
    }

    private async Task SendReportAsync(byte reportId, byte[] report)
    {
        if (!IsAdvertising) return;
        _lastReports[reportId] = report;
        if (reportId == 2)
        {
            if (HasTargetSubscriber(_mouseReport))
            {
                await NotifyReportAsync(_mouseReport, report).ConfigureAwait(false);
            }
            else if (HasTargetSubscriber(_bootMouseInput))
            {
                await NotifyReportAsync(_bootMouseInput,
                    ToBootMouseReport(report)).ConfigureAwait(false);
            }
        }
        else if (HasTargetSubscriber(_keyboardReport))
        {
            await NotifyReportAsync(_keyboardReport, report).ConfigureAwait(false);
        }
        else if (HasTargetSubscriber(_bootKeyboardInput))
        {
            await NotifyReportAsync(_bootKeyboardInput, report).ConfigureAwait(false);
        }
    }

    private async Task NotifyReportAsync(GattLocalCharacteristic? characteristic, byte[] report)
    {
        var targetClient = FindTargetSubscriber(characteristic);
        if (characteristic is null || targetClient is null) return;
        try
        {
            var buffer = CryptographicBuffer.CreateFromByteArray(report);
            await _notifyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await characteristic.NotifyValueAsync(buffer, targetClient)
                    .AsTask().ConfigureAwait(false);
            }
            finally
            {
                _notifyGate.Release();
            }
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _transportFailed, 1);
            SetStatus("Bluetooth HID control disconnected.", error.Message);
        }
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

    private void StopAndClearProviderState()
    {
        var provider = _provider;
        BeginStopAdvertisingSession();
        _provider = null;
        if (provider is not null)
        {
            provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        }
        _mouseReport = null;
        _keyboardReport = null;
        _bootMouseInput = null;
        _bootKeyboardInput = null;
        _protocolModeCharacteristic = null;
        _wheelResolutionCharacteristic = null;
        _targetDeviceName = null;
        Interlocked.Increment(ref _targetBindingGeneration);
        Volatile.Write(ref _targetClientId, null);
        _lastTargetClientId = null;
        Volatile.Write(ref _wheelResolutionMultiplier, (byte)1);
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
        Interlocked.Exchange(ref _advertisingStopRequested, 1);
        _advertisingStarted?.TrySetResult(false);
        _advertisingStarted = null;
        _advertisingStopped?.TrySetResult(false);
        _advertisingStopped = null;
        _clientConnected?.TrySetResult(false);
        _clientConnected = null;
        _protocolMode = 0x01;
        _targetDeviceName = null;
        Interlocked.Increment(ref _targetBindingGeneration);
        Volatile.Write(ref _targetClientId, null);
        _lastTargetClientId = null;
        Volatile.Write(ref _wheelResolutionMultiplier, (byte)1);
        Interlocked.Exchange(ref _transportFailed, 0);
        _lastReports.Clear();
        if (!IsAdvertising) return null;
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
            _advertisingStarted?.TrySetResult(true);
            SetStatus($"Bluetooth HID control is advertising ({args.Status}).", null);
        }
        else if (args.Status == GattServiceProviderAdvertisementStatus.Aborted)
        {
            if (Volatile.Read(ref _advertisingStopRequested) != 0)
            {
                _advertisingStopped?.TrySetResult(true);
                return;
            }
            if (IsAdvertising) return;
            Interlocked.Exchange(ref _transportFailed, 1);
            SetStatus("Bluetooth HID control stopped unexpectedly.", args.Error.ToString());
        }
    }

    private async void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        if (!IsCurrentCharacteristic(sender)) return;
        try
        {
            await RefreshTargetClientAsync().ConfigureAwait(false);
            if (!IsCurrentCharacteristic(sender)) return;
            if ((ReferenceEquals(sender, _mouseReport) ||
                 ReferenceEquals(sender, _bootMouseInput)) &&
                HasTargetSubscriber(sender))
                Interlocked.Exchange(ref _transportFailed, 0);
            var connected = IsConnected;
            if (IsMouseConnected) _clientConnected?.TrySetResult(true);
            SetStatus(connected
                    ? $"Selected iPhone/iPad subscribed to the {GetReportName(sender)}."
                    : HasAnySubscriber
                        ? Volatile.Read(ref _targetClientId) is null
                            ? "Multiple Bluetooth clients are connected; waiting for the selected iPhone/iPad."
                            : "The selected Bluetooth client is connected; waiting for its HID mouse report."
                        : "Bluetooth HID control is advertising; waiting for a client.",
                null);
            if (Volatile.Read(ref _targetClientId) is not null)
                await SendInitialReportsAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            SetStatus("Could not identify the selected Bluetooth client.", error.Message);
        }
    }

    private static bool HasSubscribers(GattLocalCharacteristic? characteristic) =>
        characteristic?.SubscribedClients?.Count > 0;

    private bool IsCurrentCharacteristic(GattLocalCharacteristic characteristic) =>
        ReferenceEquals(characteristic, _mouseReport) ||
        ReferenceEquals(characteristic, _keyboardReport) ||
        ReferenceEquals(characteristic, _bootMouseInput) ||
        ReferenceEquals(characteristic, _bootKeyboardInput);

    private bool HasTargetSubscriber(GattLocalCharacteristic? characteristic) =>
        FindTargetSubscriber(characteristic) is not null;

    private GattSubscribedClient? FindTargetSubscriber(
        GattLocalCharacteristic? characteristic)
    {
        var targetClientId = Volatile.Read(ref _targetClientId);
        if (characteristic is null || string.IsNullOrWhiteSpace(targetClientId))
            return null;
        return characteristic.SubscribedClients.FirstOrDefault(client =>
            string.Equals(client.Session.DeviceId.Id, targetClientId,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshTargetClientAsync()
    {
        await _targetClientGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _targetBindingGeneration);
            var clients = EnumerateSubscribedClients()
                .GroupBy(client => client.Session.DeviceId.Id,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (generation != Volatile.Read(ref _targetBindingGeneration)) return;
            var boundClientId = Volatile.Read(ref _targetClientId);
            if (!string.IsNullOrWhiteSpace(boundClientId))
            {
                if (clients.Any(client => string.Equals(client.Session.DeviceId.Id,
                        boundClientId, StringComparison.OrdinalIgnoreCase)))
                    return;
                // Do not retarget an active session to another phone when the
                // original client disconnects, even if both phones share a
                // friendly name. The same DeviceId may rebind when it returns;
                // another device requires an explicit restart.
                Volatile.Write(ref _targetClientId, null);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_lastTargetClientId) &&
                clients.Any(client => string.Equals(client.Session.DeviceId.Id,
                    _lastTargetClientId, StringComparison.OrdinalIgnoreCase)))
            {
                Volatile.Write(ref _targetClientId, _lastTargetClientId);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_lastTargetClientId)) return;
            var candidates = new List<(string Id, string Name)>(clients.Length);
            foreach (var client in clients)
            {
                var id = client.Session.DeviceId.Id;
                string name;
                try
                {
                    using var bluetoothDevice = await BluetoothLEDevice.FromIdAsync(id);
                    if (!string.IsNullOrWhiteSpace(bluetoothDevice?.Name))
                    {
                        name = bluetoothDevice.Name;
                    }
                    else
                    {
                        var information = await DeviceInformation.CreateFromIdAsync(id);
                        name = information?.Name ?? string.Empty;
                    }
                }
                catch
                {
                    try
                    {
                        var information = await DeviceInformation.CreateFromIdAsync(id);
                        name = information?.Name ?? string.Empty;
                    }
                    catch
                    {
                        name = string.Empty;
                    }
                }
                candidates.Add((id, name));
            }
            if (generation != Volatile.Read(ref _targetBindingGeneration)) return;
            var selected = BluetoothSubscribedClientSelector.Select(_targetDeviceName,
                candidates);
            Volatile.Write(ref _targetClientId, selected);
            if (!string.IsNullOrWhiteSpace(selected)) _lastTargetClientId = selected;
        }
        finally
        {
            _targetClientGate.Release();
        }
    }

    private IEnumerable<GattSubscribedClient> EnumerateSubscribedClients()
    {
        foreach (var characteristic in new[]
                 { _mouseReport, _keyboardReport, _bootMouseInput, _bootKeyboardInput })
        {
            if (characteristic is null) continue;
            foreach (var client in characteristic.SubscribedClients)
                yield return client;
        }
    }

    private string GetReportName(GattLocalCharacteristic characteristic) =>
        ReferenceEquals(characteristic, _mouseReport) ? "HID mouse report" :
        ReferenceEquals(characteristic, _bootMouseInput) ? "HID boot mouse report" :
        ReferenceEquals(characteristic, _bootKeyboardInput) ? "HID boot keyboard report" :
        "HID keyboard report";

    private async Task SendInitialReportsAsync()
    {
        await SendReportAsync(2, new byte[6]).ConfigureAwait(false);
        await SendReportAsync(1, new byte[8]).ConfigureAwait(false);
    }

    private async void OnProtocolModeWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
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
                        _protocolMode = mode;
                        _ = SendInitialReportsAsync();
                    }
                }
                request.Respond();
            }
        }
        finally { deferral.Complete(); }
    }

    private async void OnWheelResolutionWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is not null)
            {
                using var reader = DataReader.FromBuffer(request.Value);
                if (request.Value.Length > 0)
                {
                    var multiplier = (byte)Math.Clamp((int)reader.ReadByte(), 1, 10);
                    Volatile.Write(ref _wheelResolutionMultiplier, multiplier);
                    SetStatus($"HID wheel resolution multiplier set to {multiplier}.", null);
                }
                request.Respond();
            }
        }
        finally { deferral.Complete(); }
    }

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
            throw new InvalidOperationException($"Could not create HID wheel feature descriptor: {result.Error}");
        characteristic.ReadRequested += (_, args) => RespondToReadAsync(args,
            () => [(byte)WheelResolutionMultiplier]);
        characteristic.WriteRequested += OnWheelResolutionWriteRequested;
        return characteristic;
    }
    private static async void OnControlPointWriteRequested(GattLocalCharacteristic sender,
        GattWriteRequestedEventArgs args) => await RespondToWriteAsync(args);

    private static async Task RespondToWriteAsync(GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            request?.Respond();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static async void RespondToReadAsync(GattReadRequestedEventArgs args,
        Func<byte[]> valueFactory)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            request?.RespondWithValue(CryptographicBuffer.CreateFromByteArray(valueFactory()));
        }
        finally { deferral.Complete(); }
    }

    private void SetStatus(string status, string? error)
    {
        Status = status;
        Error = error;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopAndClearProviderState(); }
        finally { _gate.Release(); }
        _notifyGate.Dispose();
        _gate.Dispose();
    }
}
