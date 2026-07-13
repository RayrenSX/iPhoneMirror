using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using IPhoneMirror.DriverInstaller.Models;
using IPhoneMirror.DriverInstaller.Services;
using IPhoneMirror.DriverInstaller.Windows;

namespace IPhoneMirror.DriverInstaller;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DeviceCatalog _catalog = new();
    private readonly DriverOperationClient _operations = new();
    private readonly AppleSupportInstaller _appleInstaller;
    private AppleDeviceRecord? _selectedDevice;
    private AppleSupportStatus _appleSupport = new(false, false, null, "正在检查");
    private LibUsbStackStatus _libUsb = new(false, false, false, null, "正在检查");
    private bool _isBusy;
    private bool _isAdvancedMode;
    private string _operationStatus = "点击按钮后将自动检查并补齐缺失驱动。";

    public ObservableCollection<AppleDeviceRecord> Devices { get; } = [];
    public IEnumerable<AppleDeviceRecord> ConnectedDevices =>
        Devices.Where(device => device.IsPresent);
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
    public string AdvancedButtonText => IsAdvancedMode ? "返回简洁界面" : "高级设置";
    public bool CanInteract => !IsBusy;
    public bool CanQuickInstall => !IsBusy && SelectedDevice is { IsPresent: true };
    public string InstallButtonText => _selectedDevice?.HasLibUsb0Filter == true ? "已安装" : "安装";
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _appleInstaller = new AppleSupportInstaller(_catalog);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

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
            PromptWindow.Inform(this, "无法打开日志", error.Message);
        }
    }

    private async void OnInstallAppleSupportClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            DriverLogger.Write("Apple support install requested.");
            OperationStatus = "正在准备 Apple USB 支持…";
            var result = await _appleInstaller.InstallAsync();
            if (result.RequiresStoreInteraction)
            {
                var action = new RequiredActionWindow(
                    "需要完成 Apple 官方安装",
                    result.Message + "\n\n请完成当前这一步后点击“重新检测”。工具不会自动操作商店窗口。",
                    "重新检测") { Owner = this }.ShowDialog();
                if (action == true)
                    result = await _appleInstaller.InstallAsync();
            }
            if (!result.Success)
            {
                OperationStatus = result.Message;
                DriverLogger.Write("Apple support install failed: " + result.Message);
                ShowFailure(result.Message);
            }
            else OperationStatus = "Apple USB 支持已就绪。";
        }
        catch (Exception error)
        {
            DriverLogger.Write("Apple support UI error: " + error);
            OperationStatus = error.Message;
            ShowFailure(error.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task<bool> EnsureAppleSupportReadyAsync()
    {
        var result = await _appleInstaller.InstallAsync();
        if (result.RequiresStoreInteraction)
        {
            var action = new RequiredActionWindow(
                "需要完成 Apple 官方安装",
                result.Message + "\n\n请完成当前这一步后点击“重新检测”。工具不会自动操作商店窗口。",
                "重新检测") { Owner = this }.ShowDialog();
            if (action != true) return false;
            result = await _appleInstaller.InstallAsync();
        }
        if (result.Success)
        {
            OperationStatus = "Apple USB 支持已就绪。";
            return true;
        }
        OperationStatus = result.Message;
        return ShowFailure(result.Message);
    }

    private async Task InstallAllAsync()
    {
        if (IsBusy) return;
        var answer = PromptWindow.Confirm(this,
            "一键安装全部驱动",
            "工具将自动检查 Apple USB 支持、共享采集驱动和当前选中的 iPhone。发现缺失项会自动安装；发现明确属于 WinUSB/libusb 的错误父驱动会先移除，再提示重新连接。",
            "开始安装");
        if (!answer) return;

        IsBusy = true;
        try
        {
            if (!await EnsureAppleSupportReadyAsync()) return;
            await RefreshCoreAsync();
            if (!Devices.Any(device => device.IsPresent))
            {
                var action = new RequiredActionWindow(
                    "请连接 iPhone",
                    "Apple USB 支持已准备好。请连接并解锁 iPhone；手机出现提示时点击“信任此电脑”。检测到设备后会自动继续。",
                    "开始检测") { Owner = this }.ShowDialog();
                if (action != true || !await WaitForAnyDeviceAsync(TimeSpan.FromMinutes(3)))
                {
                    ShowFailure("等待 Apple 设备连接超时。\n日志：" + DriverLogger.Path);
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
                ShowFailure("选中的设备已经断开，请重新选择后再试。\n日志：" + DriverLogger.Path);
                return;
            }

            if (!string.Equals(device.Service, "usbccgp", StringComparison.OrdinalIgnoreCase))
            {
                OperationStatus = $"正在修复 {device.DisplayName} 的父驱动…";
                var parent = await _operations.RunAsync(DriverOperationKind.ParentRepair, device);
                if (!parent.Success)
                {
                    ShowFailure(parent.Message + $"\n日志：{parent.LogPath}");
                    return;
                }
                if (!await GuideReconnectAsync(device.InstanceId,
                        DriverOperationKind.ParentRepair)) return;
                await RefreshCoreAsync();
                device = Devices.FirstOrDefault(item =>
                    string.Equals(item.Serial, device.Serial, StringComparison.OrdinalIgnoreCase));
                if (device is not { IsPresent: true } ||
                    !string.Equals(device.Service, "usbccgp", StringComparison.OrdinalIgnoreCase))
                {
                    ShowFailure("父驱动重新绑定后仍不是 usbccgp，已停止自动安装。\n日志：" +
                                DriverLogger.Path);
                    return;
                }
            }

            if (device.HasLibUsb0Filter && _libUsb.FilesMatch)
            {
                OperationStatus = $"{device.DisplayName} 的投屏驱动已经全部就绪。";
                return;
            }

            var kind = device.HasLibUsb0Filter
                ? DriverOperationKind.Repair
                : DriverOperationKind.Install;
            OperationStatus = $"正在为 {device.DisplayName} {(
                kind == DriverOperationKind.Install ? "安装" : "修复")}采集驱动…";
            var result = await _operations.RunAsync(kind, device);
            if (!result.Success)
            {
                ShowFailure(result.Message + $"\n日志：{result.LogPath}");
                return;
            }
            if (result.RequiresReplug &&
                !await GuideReconnectAsync(device.InstanceId, kind))
            {
                ShowFailure("等待设备重新连接超时。\n日志：" + result.LogPath);
                return;
            }
            OperationStatus = $"一键安装完成，{device.DisplayName} 的投屏驱动已就绪。";
        }
        catch (Exception error)
        {
            DriverLogger.Write("Quick install failed: " + error);
            OperationStatus = error.Message;
            ShowFailure(error.Message + $"\n日志：{DriverLogger.Path}");
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task<bool> WaitForAnyDeviceAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (_catalog.GetAppleDevices().Any(device => device.IsPresent)) return true;
            OperationStatus = "等待 Apple 设备连接…";
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

        var verb = kind switch
        {
            DriverOperationKind.Install => "安装",
            DriverOperationKind.Repair => "修复",
            _ => "卸载",
        };
        var answer = PromptWindow.Confirm(this,
            $"确认{verb}",
            $"将对以下设备执行{verb}：\n\n{device.SelectionText}\n{device.DetailText}\n\n" +
            "操作只修改该设备的 libusb0 过滤器，不替换 Apple 官方驱动。Windows 可能请求管理员授权。",
            verb, kind == DriverOperationKind.Uninstall);
        if (!answer) return;

        IsBusy = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                OperationStatus = $"正在{verb} {device.DisplayName}…";
                var result = await _operations.RunAsync(kind, device);
                if (!result.Success)
                {
                    var failure = result.Message + (string.IsNullOrWhiteSpace(result.LogPath)
                        ? string.Empty : $"\n日志：{result.LogPath}");
                    OperationStatus = failure;
                    DriverLogger.Write(failure);
                    if (!ShowFailure(failure)) break;
                    await RefreshCoreAsync();
                    device = SelectedDevice;
                    if (device is null) break;
                    continue;
                }

                if (result.RequiresReplug)
                {
                    var reconnected = await GuideReconnectAsync(device.InstanceId, kind);
                    if (!reconnected)
                    {
                        ShowFailure("等待设备拔下或重新连接超时。驱动操作本身已经回滚或完成，请查看日志后重试。");
                        break;
                    }
                }
                OperationStatus = kind == DriverOperationKind.Uninstall
                    ? "当前设备的采集过滤驱动已卸载。"
                    : "当前设备的采集过滤驱动已安装并验证。";
                await RefreshCoreAsync();
                break;
            }
        }
        catch (Exception error)
        {
            DriverLogger.Write($"Driver operation UI error: {error}");
            OperationStatus = error.Message;
            ShowFailure(error.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task<bool> GuideReconnectAsync(string instanceId, DriverOperationKind kind)
    {
        var present = _catalog.FindExact(instanceId, instanceId[(instanceId.LastIndexOf('\\') + 1)..])
            ?.IsPresent == true;
        if (present)
        {
            var unplug = PromptWindow.Confirm(this,
                "请拔线",
                "驱动更改已经完成。现在请拔掉这台 iPhone 的数据线，等待设备从列表中消失。",
                "已经拔线");
            if (!unplug) return false;
            if (!await WaitForPresenceAsync(instanceId, false, TimeSpan.FromMinutes(3)))
                return false;
        }

        var reconnect = PromptWindow.Confirm(this,
            "请重新连接",
            "已检测到设备断开。现在请重新连接 iPhone，解锁设备，并在手机出现提示时点击“信任此电脑”。",
            "已经连接");
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
            var device = _catalog.FindExact(instanceId, serial);
            if (device?.IsPresent == expected) return true;
            OperationStatus = expected ? "等待设备重新连接…" : "等待设备断开…";
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await RefreshCoreAsync(); }
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
        foreach (var device in result.Item1) Devices.Add(device);
        OnPropertyChanged(nameof(ConnectedDevices));
        SelectedDevice = Devices.FirstOrDefault(device =>
            string.Equals(device.InstanceId, previous, StringComparison.OrdinalIgnoreCase))
            ?? Devices.FirstOrDefault(device => device.IsPresent)
            ?? Devices.FirstOrDefault();
        if (!IsAdvancedMode)
        {
            var connected = Devices.Count(device => device.IsPresent);
            var ready = connected > 0 && _appleSupport.Ready && _libUsb.FilesMatch &&
                        Devices.Where(device => device.IsPresent).All(device =>
                            string.Equals(device.Service, "usbccgp",
                                StringComparison.OrdinalIgnoreCase) && device.HasLibUsb0Filter);
            OperationStatus = connected == 0
                ? "连接 iPhone 后点击按钮，工具会自动完成检测和安装。"
                : ready
                    ? $"已检测到 {connected} 台设备，投屏驱动均已就绪。"
                    : $"已检测到 {connected} 台设备，点击按钮自动安装缺失驱动。";
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
