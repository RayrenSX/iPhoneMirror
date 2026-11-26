using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;
using IPhoneMirror.DriverInstaller.Windows;

namespace IPhoneMirror.DriverInstaller;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    public sealed record ThemeChoice(DriverThemeMode Value, string Label);
    private readonly DeviceCatalog _catalog = new();
    private readonly DriverOperationClient _operations = new();
    private readonly AppleSupportInstaller _appleInstaller;
    private AppleDeviceRecord? _selectedDevice;
    private AppleSupportStatus _appleSupport = new(false, false, null, false, null,
        L("CheckPending"));
    private LibUsbStackStatus _libUsb = new(false, false, false, null, L("CheckPending"));
    private bool _isBusy;
    private bool _isAdvancedMode;
    private string _operationStatus = L("SimpleNoDevice");

    public ObservableCollection<AppleDeviceRecord> Devices { get; } = [];
    public IReadOnlyList<ThemeChoice> ThemeChoices { get; }
    public DriverThemeMode SelectedTheme
    {
        get => DriverThemeService.Preference;
        set
        {
            if (value == DriverThemeService.Preference) return;
            DriverThemeService.Apply(value);
            OnPropertyChanged();
        }
    }
    public ObservableCollection<AppleDeviceRecord> ConnectedDevices { get; } = [];
    public AppleDeviceRecord? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (ReferenceEquals(_selectedDevice, value)) return;
            _selectedDevice = value;
            OnPropertyChanged();
            NotifyCommands();
        }
    }

    public string AppleStatusText => _appleSupport.Diagnostic;
    public string LibUsbStatusText => _libUsb.Diagnostic;
    public string OperationStatus
    {
        get => _operationStatus;
        private set { if (Set(ref _operationStatus, value)) { } }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(BusyVisibility));
            NotifyCommands();
            if (!value && _deviceRefreshPending && !_closed) _deviceRefresh.Start();
        }
    }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdvancedVisibility => IsAdvancedMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SimpleVisibility => IsAdvancedMode ? Visibility.Collapsed : Visibility.Visible;
    public string AdvancedButtonText => L(IsAdvancedMode ? "BackToSimple" : "AdvancedSettings");
    private bool AppleSupportReady => _appleSupport.Ready && _appleInstaller?.RequiresRestart != true;
    public bool CanInteract => !IsBusy;
    public bool CanQuickInstall => !IsBusy && SelectedDevice is { IsPresent: true };
    public string InstallButtonText => L(_selectedDevice?.HasLibUsb0Filter == true ? "Installed" : "Install");
    public bool CanInstallAppleSupport => !IsBusy && !AppleSupportReady;
    public bool CanInstallDriver => !IsBusy && _selectedDevice is { IsPresent: true,
        IsCaptureParent: true, IsHealthy: true, HasLibUsb0Filter: false } && AppleSupportReady;
    public bool CanRepairDriver => !IsBusy && _selectedDevice is { IsPresent: true,
        IsCaptureParent: true, HasLibUsb0Filter: true } && AppleSupportReady;
    public bool CanUninstallDriver => !IsBusy && _selectedDevice is { HasLibUsb0Filter: true };
    public bool CanManageParent => !IsBusy && _selectedDevice is { IsPresent: true };

    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        private set
        {
            if (!Set(ref _isAdvancedMode, value)) return;
            OnPropertyChanged(nameof(AdvancedVisibility));
            OnPropertyChanged(nameof(SimpleVisibility));
            OnPropertyChanged(nameof(AdvancedButtonText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string L(string key) => DriverLocalization.Get(key);
    private static string F(string key, params object?[] args) => DriverLocalization.Format(key, args);

    private readonly bool _previewOnly;
    private bool _deviceRefreshPending, _closed;
    private System.Windows.Interop.HwndSource? _deviceSource;
    private readonly System.Windows.Threading.DispatcherTimer _deviceRefresh = new() { Interval = TimeSpan.FromMilliseconds(650) };

    public MainWindow() : this(false) { }
    internal MainWindow(bool previewOnly)
    {
        _previewOnly = previewOnly;
        ThemeChoices =
        [
            new(DriverThemeMode.System, L("ThemeSystem")),
            new(DriverThemeMode.Light, L("ThemeLight")),
            new(DriverThemeMode.Dark, L("ThemeDark")),
        ];
        InitializeComponent();
        DataContext = this;
        _appleInstaller = new AppleSupportInstaller(_catalog);
        _operations.StatusChanged += status => OperationStatus = status;
        StateChanged += OnWindowStateChanged;
        _deviceRefresh.Tick += async (_, _) =>
        {
            _deviceRefresh.Stop();
            if (_closed || IsBusy) return;
            _deviceRefreshPending = false;
            await RefreshAsync();
        };
        Closing += (_, e) => { if (IsBusy) e.Cancel = true; };
        Closed += (_, _) => { _closed = true; _deviceRefresh.Stop(); _deviceSource?.RemoveHook(DeviceMessage); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IPhoneMirror.UI.Controls.RoundedWindow.SizeForReadingText(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        _deviceSource = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _deviceSource?.AddHook(DeviceMessage);
        await RefreshAsync();
    }
    private nint DeviceMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0219 && !_closed && wParam.ToInt64() is 0x8000 or 0x8004 or 0x0007)
        {
            // Remember hotplug during an operation or scan and refresh once it has settled.
            _deviceRefreshPending = true;
            _deviceRefresh.Stop();
            if (!IsBusy) _deviceRefresh.Start();
        }
        return 0;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var flushToDisplayEdge = WindowState == WindowState.Maximized;
        DriverWindowChrome.ResizeBorderThickness = flushToDisplayEdge
            ? new Thickness(0) : new Thickness(7);
        DriverWindowChrome.CornerRadius = flushToDisplayEdge
            ? new CornerRadius(0) : new CornerRadius(20);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnConnectedDeviceSelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<AppleDeviceRecord>().FirstOrDefault() is { } device)
            SelectedDevice = device;
    }

    private void OnConnectedDevicePreviewMouseLeftButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        ToggleComboBoxDropDown(sender, e);
    }

    private void OnThemePreviewMouseLeftButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e) => ToggleComboBoxDropDown(sender, e);

    private static void ToggleComboBoxDropDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ComboBox combo || !combo.IsEnabled) return;
        // Popup input also routes through the ComboBox, but belongs to a separate
        // visual tree. Let its items and scrollbars handle their own mouse input.
        if (e.OriginalSource is not System.Windows.Media.Visual source ||
            !combo.IsAncestorOf(source)) return;
        combo.Focus();
        combo.IsDropDownOpen = !combo.IsDropDownOpen;
        e.Handled = true;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void OnAdvancedClick(object sender, RoutedEventArgs e) => IsAdvancedMode = !IsAdvancedMode;

    private async void OnQuickInstallClick(object sender, RoutedEventArgs e) =>
        await InstallAllAsync();

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DriverLogger.EnsureCreated();
            DriverLogger.Write("Log file opened from advanced mode.");
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{DriverLogger.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "open_log_failed", error);
            PromptWindow.Inform(this, L("CannotOpenLogs"), L("OpenLogFailedBody"), isError: true);
        }
    }

    private void OnForceDriverCleanupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DriverCleanupHost.LaunchElevated();
            DriverLogger.Write(
                "Trusted driver cleanup host launched from advanced mode.");
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "driver_cleanup_script_start_failed", error);
            PromptWindow.Inform(this, L("DriverCleanupUnavailableTitle"),
                L("DriverCleanupScriptStartFailed"), isError: true);
        }
    }

    private async void OnInstallAppleSupportClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var driverChanged = false;
        IsBusy = true;
        try
        {
            DriverLogger.Write("Apple support install requested.");
            OperationStatus = L("AppleSupportPreparing");
            var progress = CreateAppleSupportProgress();
            var result = await _appleInstaller.InstallAsync(progress);
            if (result.RequiresStoreInteraction)
            {
                var action = new RequiredActionWindow(
                    L("RequiredAppleInstallTitle"),
                    result.Message + "\n\n" + L("RequiredAppleInstallBody"),
                    L("Recheck")) { Owner = this }.ShowDialog();
                if (action == true)
                    result = await _appleInstaller.InstallAsync(progress);
            }
            if (result.RequiresRestart)
            {
                OperationStatus = result.Message;
                PromptWindow.Inform(this, L("RequiredAppleInstallTitle"), result.Message);
            }
            else if (!result.Success)
            {
                OperationStatus = result.Message;
                DriverLogger.WriteError("ui", "apple_support_install_failed",
                    ("message", result.Message));
                ShowFailure(result.Message, result.LogPath);
            }
            else
            {
                driverChanged = true;
                OperationStatus = L("AppleSupportReady");
            }
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "apple_support_install_exception", error);
            OperationStatus = error.Message;
            ShowFailure(error.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            if (driverChanged) ConfirmDeviceTrustAfterChange("apple-support");
        }
    }

    private async Task<bool> EnsureAppleSupportReadyAsync()
    {
        var progress = CreateAppleSupportProgress();
        var result = await _appleInstaller.InstallAsync(progress);
        if (result.RequiresStoreInteraction)
        {
            var action = new RequiredActionWindow(
                L("RequiredAppleInstallTitle"),
                result.Message + "\n\n" + L("RequiredAppleInstallBody"),
                L("Recheck")) { Owner = this }.ShowDialog();
            if (action != true) return false;
            result = await _appleInstaller.InstallAsync(progress);
        }
        if (result.Success && !result.RequiresRestart)
        {
            OperationStatus = L("AppleSupportReady");
            return true;
        }
        OperationStatus = result.Message;
        if (result.RequiresRestart) PromptWindow.Inform(this, L("RequiredAppleInstallTitle"), result.Message);
        else ShowFailure(result.Message, result.LogPath);
        return false;
    }

    private IProgress<string> CreateAppleSupportProgress() =>
        new Progress<string>(status => OperationStatus = status);

    private async Task InstallAllAsync()
    {
        if (IsBusy || SelectedDevice is not { IsPresent: true } selected) return;
        var answer = PromptWindow.Confirm(this,
            L("QuickInstallTitle"), L("QuickInstallBody"), L("StartInstall"));
        if (!answer) return;

        var driverChanged = false;
        IsBusy = true;
        try
        {
            var appleSupportWasReady = AppleSupportReady;
            if (!await EnsureAppleSupportReadyAsync()) return;
            driverChanged |= !appleSupportWasReady;
            await RefreshCoreAsync();
            if (!Devices.Any(device => device.IsPresent && SameDevice(device, selected)))
            {
                var action = new RequiredActionWindow(
                    L("ConnectPhoneTitle"), L("ConnectPhoneBody"),
                    L("StartDetection")) { Owner = this }.ShowDialog();
                if (action != true) return;
                if (!await WaitForPresenceAsync(selected.InstanceId, true, TimeSpan.FromMinutes(3)))
                {
                    ShowFailure(L("SelectedDeviceDisconnected"), DriverLogger.Path);
                    return;
                }
                await RefreshCoreAsync();
            }

            var device = Devices.FirstOrDefault(item => SameDevice(item, selected));
            if (device is not { IsPresent: true })
            {
                ShowFailure(L("SelectedDeviceDisconnected"), DriverLogger.Path);
                return;
            }

            if (!device.IsCaptureParent || !device.IsHealthy)
            {
                var captureDriver = await Task.Run(() =>
                {
                    using var candidates = new ParentDriverNative(device.InstanceId);
                    return ParentDriverChoice.SelectCaptureDriver(candidates.Choices);
                }) ??
                    throw new InvalidOperationException(L("ParentCompositeUnavailable"));
                var consent = ParentDriverWindow.ConfirmChange(this, device, ParentDriverAction.Bind, captureDriver);
                if (consent is null) return;
                OperationStatus = F("RepairingParent", device.DisplayName);
                var parent = await _operations.RunAsync(DriverOperationKind.ParentRepair, device, consent);
                if (parent.RequiresRestart)
                {
                    PromptWindow.Inform(this, L("ParentManagerTitle"), DriverResultText(parent));
                    return;
                }
                if (!parent.Success)
                {
                    ShowFailure(parent.Message, parent.LogPath);
                    return;
                }
                if (parent.RequiresReplug && !await GuideReconnectAsync(device.InstanceId,
                        DriverOperationKind.ParentRepair, parent.LogPath, captureDriver)) return;
                await RefreshCoreAsync();
                device = Devices.FirstOrDefault(item => SameDevice(item, selected));
                if (device is not { IsHealthy: true, IsCaptureParent: true } ||
                    !ParentDriverChange.BindingMatches(device, captureDriver))
                {
                    ShowFailure(L("ParentRepairFailed"), parent.LogPath);
                    return;
                }
            }

            if (DriverOperationSafety.IsFilterReady(device, _libUsb))
            {
                OperationStatus = F("DeviceReady", device.DisplayName);
                return;
            }

            var kind = device.HasLibUsb0Filter
                ? DriverOperationKind.Repair
                : DriverOperationKind.Install;
            OperationStatus = F(kind == DriverOperationKind.Install ? "InstallingDriver" : "RepairingDriver",
                device.DisplayName);
            var result = await _operations.RunAsync(kind, device);
            if (result.RequiresRestart)
            {
                PromptWindow.Inform(this, L("QuickInstallTitle"), DriverResultText(result));
                return;
            }
            if (!result.Success)
            {
                ShowFailure(result.Message, result.LogPath);
                return;
            }
            if (result.RequiresReplug &&
                !await GuideReconnectAsync(device.InstanceId, kind, result.LogPath)) return;
            driverChanged = true;
            OperationStatus = F("QuickInstallComplete", device.DisplayName);
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "quick_install_failed", error);
            OperationStatus = error.Message;
            ShowFailure(error.Message, DriverLogger.Path);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            if (driverChanged) ConfirmDeviceTrustAfterChange("quick-install");
        }
    }

    private static bool SameDevice(AppleDeviceRecord candidate, AppleDeviceRecord selected) =>
        string.Equals(candidate.InstanceId, selected.InstanceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(candidate.Serial, selected.Serial, StringComparison.OrdinalIgnoreCase);

    private async void OnInstallClick(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(DriverOperationKind.Install);

    private async void OnRepairClick(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(DriverOperationKind.Repair);

    private async void OnUninstallClick(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(DriverOperationKind.Uninstall);

    private async void OnManageParentClick(object sender, RoutedEventArgs e)
    {
        if (!CanManageParent || SelectedDevice is not { } selected) return;
        IsBusy = true;
        string? status = null;
        try
        {
            var device = await Task.Run(() => _catalog.FindExact(selected.InstanceId, selected.Serial));
            if (device is not { IsPresent: true })
                throw new InvalidOperationException(L("ParentDeviceUnavailable"));
            device = device with
            {
                DeviceNumber = selected.DeviceNumber, DeviceName = selected.DeviceName,
                ProductType = selected.ProductType, ModelName = selected.ModelName, OsVersion = selected.OsVersion,
            };
            IReadOnlyList<ParentDriverChoice> choices = [];
            var diagnostic = string.Empty;
            try
            {
                var enumeration = await Task.Run(() => ParentDriverNative.EnumerateWithDiagnostics(device.InstanceId));
                choices = enumeration.Choices;
                if (enumeration.Errors.Count > 0) diagnostic = L("ParentListPartial") + "\n" + string.Join("\n", enumeration.Errors);
            }
            catch (Exception error)
            {
                DriverLogger.WriteException("ui", "parent_driver_enumeration_failed", error);
                diagnostic = L("ParentListUnavailable");
            }
            var dialog = new ParentDriverWindow(device, choices, diagnostic) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Consent is null) return;
            OperationStatus = F("RepairingParent", device.DisplayName);
            var result = await _operations.RunAsync(DriverOperationKind.ParentRepair, device, dialog.Consent);
            status = DriverResultText(result);
            PromptWindow.Inform(this, L("ParentManagerTitle"), status, isError: !result.Success && !result.RequiresRestart);
            if (result.Success && result.RequiresReplug && !result.RequiresRestart)
            {
                if (!await GuideReconnectAsync(device.InstanceId, DriverOperationKind.ParentRepair,
                        result.LogPath, dialog.Consent.Driver))
                    status = OperationStatus;
            }
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "parent_management_failed", error);
            status = error.Message;
            PromptWindow.Inform(this, L("ParentManagerTitle"), status, isError: true);
        }
        finally
        {
            try { await RefreshCoreAsync(); }
            catch (Exception error)
            {
                DriverLogger.WriteException("ui", "parent_final_refresh_failed", error);
                status = (status is null ? string.Empty : status + "\n") +
                    F("DriverRefreshFailed", error.Message, DriverLogger.Path);
            }
            finally { IsBusy = false; if (status is not null) OperationStatus = status; }
        }
    }

    private static string DriverResultText(DriverOperationResult result) => result.Message +
        (string.IsNullOrEmpty(result.BackupPath) ? string.Empty : "\n\n" + F("ParentBackupFormat", result.BackupPath)) +
        (string.IsNullOrEmpty(result.LogPath) ? string.Empty : "\n" + F("LogSuffix", result.LogPath));

    private async Task RunOperationAsync(DriverOperationKind kind)
    {
        var device = SelectedDevice;
        if (device is null || IsBusy) return;
        if ((kind is DriverOperationKind.Install or DriverOperationKind.Repair) &&
            (!AppleSupportReady || !DriverOperationSafety.CanApplyCaptureFilter(device, kind))) return;
        if (kind == DriverOperationKind.Uninstall && !device.HasLibUsb0Filter) return;

        var verbKey = kind switch
        {
            DriverOperationKind.Install => "Install",
            DriverOperationKind.Repair => "Repair",
            _ => "Uninstall",
        };
        var verb = L(verbKey);
        var answer = PromptWindow.Confirm(this,
            F("ConfirmOperation", verb),
            F("ConfirmOperationBody", device.SelectionText),
            verb, kind == DriverOperationKind.Uninstall);
        if (!answer) return;

        var driverChanged = false;
        IsBusy = true;
        try
        {
            OperationStatus = F("Operating", verb, device.DisplayName);
            var result = await _operations.RunAsync(kind, device);
            if (result.RequiresRestart)
            {
                OperationStatus = DriverResultText(result);
                PromptWindow.Inform(this, verb, OperationStatus);
                return;
            }
            if (!result.Success)
            {
                OperationStatus = DriverResultText(result);
                DriverLogger.WriteError("ui", "driver_operation_failed", ("kind", kind),
                    ("message", result.Message), ("operation_log", DriverLogger.DescribePath(result.LogPath)));
                ShowFailure(result.Message, result.LogPath);
                return;
            }
            if (result.RequiresReplug && !await GuideReconnectAsync(device.InstanceId, kind, result.LogPath))
                return;

            driverChanged = true;
            OperationStatus = kind == DriverOperationKind.Uninstall
                ? L("DriverUninstalled")
                : L("DriverInstalled");
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "driver_operation_exception", error,
                ("kind", kind));
            OperationStatus = error.Message;
            ShowFailure(error.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            if (driverChanged) ConfirmDeviceTrustAfterChange(kind.ToString());
        }
    }

    private void ConfirmDeviceTrustAfterChange(string change)
    {
        var response = DeviceTrustWindow.Ask(this);
        DriverLogger.WriteEvent("ui", "device_trust_response",
            ("change", change), ("response", response));
        if (response == DeviceTrustResponse.NotHandled)
            OperationStatus = L("DeviceTrustPending");
    }

    private async Task<bool> GuideReconnectAsync(string instanceId, DriverOperationKind kind,
        string logPath, ParentDriverChoice? expectedDriver = null)
    {
        var present = _catalog.FindExact(instanceId, instanceId[(instanceId.LastIndexOf('\\') + 1)..])
            ?.IsPresent == true;
        if (present)
        {
            var unplug = PromptWindow.Confirm(this,
                L("UnplugTitle"), L("UnplugBody"), L("Unplugged"));
            if (!unplug) return false;
            if (!await WaitForPresenceAsync(instanceId, false, TimeSpan.FromMinutes(3)))
                return ReportReconnectFailure(L("ReconnectTimeout"), logPath);
        }

        var reconnect = PromptWindow.Confirm(this,
            L("ReconnectTitle"), L("ReconnectBody"), L("Reconnected"));
        if (!reconnect) return false;
        if (!await WaitForPresenceAsync(instanceId, true, TimeSpan.FromMinutes(3)))
            return ReportReconnectFailure(L("ReconnectTimeout"), logPath);

        var timer = Stopwatch.StartNew();
        do
        {
            var current = await Task.Run(() => _catalog.FindExact(instanceId,
                instanceId[(instanceId.LastIndexOf('\\') + 1)..]));
            if (current is not null && DriverOperationSafety.IsReconnectComplete(current, kind, expectedDriver))
            {
                await RefreshCoreAsync();
                var refreshed = Devices.FirstOrDefault(device => SameDevice(device, current));
                if (refreshed is not null && DriverOperationSafety.IsReconnectComplete(refreshed, kind, expectedDriver) &&
                    (kind is not (DriverOperationKind.Install or DriverOperationKind.Repair) ||
                     DriverOperationSafety.IsFilterReady(refreshed, _libUsb)))
                    return true;
                return ReportReconnectFailure(L("ReconnectVerificationFailed"), logPath);
            }
            await Task.Delay(500);
        } while (timer.Elapsed < TimeSpan.FromSeconds(20));
        await RefreshCoreAsync();
        return ReportReconnectFailure(L("ReconnectVerificationFailed"), logPath);
    }

    private bool ReportReconnectFailure(string message, string logPath)
    {
        OperationStatus = message;
        ShowFailure(message, logPath);
        return false;
    }

    private async Task<bool> WaitForPresenceAsync(string instanceId, bool expected,
        TimeSpan timeout)
    {
        var serial = instanceId[(instanceId.LastIndexOf('\\') + 1)..];
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var device = await Task.Run(() => _catalog.FindExact(instanceId, serial));
            var present = device?.IsPresent == true;
            if (present == expected) return true;
            OperationStatus = L(expected ? "WaitingReconnect" : "WaitingDisconnect");
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "status_refresh_failed", error);
            OperationStatus = error.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshCoreAsync()
    {
        var previous = SelectedDevice?.InstanceId;
        var result = await Task.Run(() =>
            (_catalog.GetAppleDevices(), _catalog.InspectAppleSupport(),
                _catalog.InspectLibUsbStack()));
        _appleSupport = result.Item2;
        _libUsb = result.Item3;
        OnPropertyChanged(nameof(AppleStatusText));
        OnPropertyChanged(nameof(LibUsbStatusText));
        Devices.Clear();
        ConnectedDevices.Clear();
        foreach (var device in result.Item1)
        {
            Devices.Add(device);
            if (device.IsPresent) ConnectedDevices.Add(device);
        }
        var previousDevice = Devices.FirstOrDefault(device =>
            string.Equals(device.InstanceId, previous, StringComparison.OrdinalIgnoreCase));
        SelectedDevice = IsAdvancedMode
            ? previousDevice ?? Devices.FirstOrDefault(device => device.IsPresent)
                ?? Devices.FirstOrDefault()
            : previousDevice is { IsPresent: true }
                ? previousDevice
                : Devices.FirstOrDefault(device => device.IsPresent);
        if (!IsAdvancedMode)
        {
            var connected = Devices.Count(device => device.IsPresent);
            var ready = connected > 0 && AppleSupportReady && _libUsb.FilesMatch &&
                        Devices.Where(device => device.IsPresent).All(device =>
                            DriverOperationSafety.IsFilterReady(device, _libUsb));
            OperationStatus = connected == 0
                ? L("SimpleNoDevice")
                : ready
                    ? F("DevicesReady", connected)
                    : F("DevicesMissing", connected);
        }
        NotifyCommands();
    }

    private void ShowFailure(string message, string? logPath = null) =>
        new FailureHelpWindow(message, string.IsNullOrWhiteSpace(logPath) ? DriverLogger.Path : logPath)
            { Owner = this }.ShowDialog();

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanQuickInstall));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(CanInstallAppleSupport));
        OnPropertyChanged(nameof(CanInstallDriver));
        OnPropertyChanged(nameof(CanRepairDriver));
        OnPropertyChanged(nameof(CanUninstallDriver));
        OnPropertyChanged(nameof(CanManageParent));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Give the inner content a finite height while keeping the page scrollable when
// large text or a small display cannot fit the header, footer and usable body.
public sealed class DriverPageHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double Height(int index) => index < values.Length && values[index] is double value && double.IsFinite(value)
            ? Math.Max(0, value) : 0;
        var advanced = values.Length > 4 && values[4] is true;
        var chrome = Height(1) + Height(3) + (advanced ? Height(2) + 12 : 0) + 22;
        return Math.Max(Height(0), chrome + 220);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
