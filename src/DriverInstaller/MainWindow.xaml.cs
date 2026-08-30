using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
        }
    }
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AdvancedVisibility => IsAdvancedMode ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SimpleVisibility => IsAdvancedMode ? Visibility.Collapsed : Visibility.Visible;
    public string AdvancedButtonText => L(IsAdvancedMode ? "BackToSimple" : "AdvancedSettings");
    public bool CanInteract => !IsBusy;
    public bool CanQuickInstall => !IsBusy && SelectedDevice is { IsPresent: true };
    public string InstallButtonText => L(_selectedDevice?.HasLibUsb0Filter == true ? "Installed" : "Install");
    public bool CanInstallAppleSupport => !IsBusy && !_appleSupport.Ready;
    public bool CanInstallDriver => !IsBusy && _selectedDevice is { IsPresent: true,
        HasLibUsb0Filter: false } && _appleSupport.Ready;
    public bool CanRepairDriver => !IsBusy && _selectedDevice is { IsPresent: true,
        HasLibUsb0Filter: true } && _appleSupport.Ready;
    public bool CanUninstallDriver => !IsBusy && _selectedDevice is { HasLibUsb0Filter: true };

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

    public MainWindow()
    {
        ThemeChoices =
        [
            new(DriverThemeMode.System, L("ThemeSystem")),
            new(DriverThemeMode.Light, L("ThemeLight")),
            new(DriverThemeMode.Dark, L("ThemeDark")),
        ];
        InitializeComponent();
        DataContext = this;
        _appleInstaller = new AppleSupportInstaller(_catalog);
        StateChanged += OnWindowStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            PromptWindow.Inform(this, L("CannotOpenLogs"), error.Message);
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
            PromptWindow.Inform(this, L("DriverCleanupTitle"),
                F("DriverCleanupScriptStartFailed", error.Message));
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
            if (!result.Success)
            {
                OperationStatus = result.Message;
                DriverLogger.WriteError("ui", "apple_support_install_failed",
                    ("message", result.Message));
                ShowFailure(result.Message);
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
        if (result.Success)
        {
            OperationStatus = L("AppleSupportReady");
            return true;
        }
        OperationStatus = result.Message;
        ShowFailure(result.Message);
        return false;
    }

    private IProgress<string> CreateAppleSupportProgress() =>
        new Progress<string>(status => OperationStatus = status);

    private async Task InstallAllAsync()
    {
        if (IsBusy) return;
        var answer = PromptWindow.Confirm(this,
            L("QuickInstallTitle"), L("QuickInstallBody"), L("StartInstall"));
        if (!answer) return;

        var driverChanged = false;
        IsBusy = true;
        try
        {
            var appleSupportWasReady = _appleSupport.Ready;
            if (!await EnsureAppleSupportReadyAsync()) return;
            driverChanged |= !appleSupportWasReady;
            await RefreshCoreAsync();
            if (!Devices.Any(device => device.IsPresent))
            {
                var action = new RequiredActionWindow(
                    L("ConnectPhoneTitle"), L("ConnectPhoneBody"),
                    L("StartDetection")) { Owner = this }.ShowDialog();
                if (action != true || !await WaitForAnyDeviceAsync(TimeSpan.FromMinutes(3)))
                {
                    ShowFailure(F("WaitDeviceTimeout", DriverLogger.Path));
                    return;
                }
                await RefreshCoreAsync();
            }

            var selectedInstanceId = SelectedDevice?.InstanceId;
            await RefreshCoreAsync();
            var device = Devices.FirstOrDefault(item =>
                string.Equals(item.InstanceId, selectedInstanceId, StringComparison.OrdinalIgnoreCase));
            if (device is not { IsPresent: true })
            {
                ShowFailure(F("SelectedDeviceDisconnected", DriverLogger.Path));
                return;
            }

            if (!string.Equals(device.Service, "usbccgp", StringComparison.OrdinalIgnoreCase))
            {
                OperationStatus = F("RepairingParent", device.DisplayName);
                var parent = await _operations.RunAsync(DriverOperationKind.ParentRepair, device);
                if (!parent.Success)
                {
                    ShowFailure(parent.Message + "\n" + F("LogSuffix", parent.LogPath));
                    return;
                }
                driverChanged = true;
                if (!await GuideReconnectAsync(device.InstanceId,
                        DriverOperationKind.ParentRepair)) return;
                await RefreshCoreAsync();
                device = Devices.FirstOrDefault(item =>
                    string.Equals(item.Serial, device.Serial, StringComparison.OrdinalIgnoreCase));
                if (device is not { IsPresent: true } ||
                    !string.Equals(device.Service, "usbccgp", StringComparison.OrdinalIgnoreCase))
                {
                    ShowFailure(F("ParentRepairFailed", DriverLogger.Path));
                    return;
                }
            }

            if (device.HasLibUsb0Filter && _libUsb.FilesMatch)
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
            if (!result.Success)
            {
                ShowFailure(result.Message + "\n" + F("LogSuffix", result.LogPath));
                return;
            }
            driverChanged = true;
            if (result.RequiresReplug &&
                !await GuideReconnectAsync(device.InstanceId, kind))
            {
                ShowFailure(F("ReconnectTimeout", result.LogPath));
                return;
            }
            OperationStatus = F("QuickInstallComplete", device.DisplayName);
        }
        catch (Exception error)
        {
            DriverLogger.WriteException("ui", "quick_install_failed", error);
            OperationStatus = error.Message;
            ShowFailure(error.Message + "\n" + F("LogSuffix", DriverLogger.Path));
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
            if (driverChanged) ConfirmDeviceTrustAfterChange("quick-install");
        }
    }

    private async Task<bool> WaitForAnyDeviceAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var present = await Task.Run(() => _catalog
                .GetAppleDevices(includeMetadata: false)
                .Any(device => device.IsPresent));
            if (present) return true;
            OperationStatus = L("WaitingForDevice");
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(DriverOperationKind.Install);

    private async void OnRepairClick(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(DriverOperationKind.Repair);

    private async void OnUninstallClick(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(DriverOperationKind.Uninstall);

    private async Task RunOperationAsync(DriverOperationKind kind)
    {
        var device = SelectedDevice;
        if (device is null || IsBusy) return;
        if (kind is DriverOperationKind.Install or DriverOperationKind.Repair &&
            (!device.IsPresent || !_appleSupport.Ready)) return;
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
            F("ConfirmOperationBody", verb, device.SelectionText, device.DetailText),
            verb, kind == DriverOperationKind.Uninstall);
        if (!answer) return;

        var targetInstanceId = device.InstanceId;
        var targetSerial = device.Serial;

        var driverChanged = false;
        IsBusy = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                OperationStatus = F("Operating", verb, device.DisplayName);
                var result = await _operations.RunAsync(kind, device);
                if (!result.Success)
                {
                    var failure = result.Message + (string.IsNullOrWhiteSpace(result.LogPath)
                        ? string.Empty : "\n" + F("LogSuffix", result.LogPath));
                    OperationStatus = failure;
                    DriverLogger.WriteError("ui", "driver_operation_failed",
                        ("kind", kind), ("attempt", attempt + 1),
                        ("message", result.Message),
                        ("operation_log", DriverLogger.DescribePath(result.LogPath)));
                    if (!ShowFailure(failure)) break;
                    await RefreshCoreAsync();
                    device = Devices.FirstOrDefault(candidate =>
                        string.Equals(candidate.InstanceId, targetInstanceId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.Serial, targetSerial,
                            StringComparison.OrdinalIgnoreCase));
                    if (device is null ||
                        kind is DriverOperationKind.Install or DriverOperationKind.Repair &&
                        (!device.IsPresent || !_appleSupport.Ready) ||
                        kind == DriverOperationKind.Uninstall && !device.HasLibUsb0Filter)
                    {
                        DriverLogger.WriteWarning("ui", "driver_retry_target_unavailable",
                            ("kind", kind),
                            ("device", DriverLogger.DeviceFingerprint(targetSerial)));
                        OperationStatus = F("SelectedDeviceDisconnected", DriverLogger.Path);
                        break;
                    }
                    continue;
                }

                driverChanged = true;
                if (result.RequiresReplug)
                {
                    var reconnected = await GuideReconnectAsync(device.InstanceId, kind);
                    if (!reconnected)
                    {
                        ShowFailure(L("ReplugTimeout"));
                        break;
                    }
                }
                OperationStatus = kind == DriverOperationKind.Uninstall
                    ? L("DriverUninstalled")
                    : L("DriverInstalled");
                await RefreshCoreAsync();
                break;
            }
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

    private async Task<bool> GuideReconnectAsync(string instanceId, DriverOperationKind kind)
    {
        var present = _catalog.FindExact(instanceId, instanceId[(instanceId.LastIndexOf('\\') + 1)..])
            ?.IsPresent == true;
        if (present)
        {
            var unplug = PromptWindow.Confirm(this,
                L("UnplugTitle"), L("UnplugBody"), L("Unplugged"));
            if (!unplug) return false;
            if (!await WaitForPresenceAsync(instanceId, false, TimeSpan.FromMinutes(3)))
                return false;
        }

        var reconnect = PromptWindow.Confirm(this,
            L("ReconnectTitle"), L("ReconnectBody"), L("Reconnected"));
        if (!reconnect) return false;
        if (!await WaitForPresenceAsync(instanceId, true, TimeSpan.FromMinutes(3)))
            return false;

        await RefreshCoreAsync();
        var current = _catalog.FindExact(instanceId,
            instanceId[(instanceId.LastIndexOf('\\') + 1)..]);
        if (current is null || !current.IsPresent) return false;
        return kind is DriverOperationKind.Uninstall or DriverOperationKind.ParentRepair ||
               current.HasLibUsb0Filter;
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
            var ready = connected > 0 && _appleSupport.Ready && _libUsb.FilesMatch &&
                        Devices.Where(device => device.IsPresent).All(device =>
                            string.Equals(device.Service, "usbccgp",
                                StringComparison.OrdinalIgnoreCase) && device.HasLibUsb0Filter);
            OperationStatus = connected == 0
                ? L("SimpleNoDevice")
                : ready
                    ? F("DevicesReady", connected)
                    : F("DevicesMissing", connected);
        }
        NotifyCommands();
    }

    private bool ShowFailure(string message) =>
        new FailureHelpWindow(message) { Owner = this }.ShowDialog() == true;

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanQuickInstall));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(CanInstallAppleSupport));
        OnPropertyChanged(nameof(CanInstallDriver));
        OnPropertyChanged(nameof(CanRepairDriver));
        OnPropertyChanged(nameof(CanUninstallDriver));
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
