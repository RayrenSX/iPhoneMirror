using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public sealed class ProfileListItem : INotifyPropertyChanged
{
    internal ProfileListItem(DeviceBindingProfile profile) => Profile = profile;
    internal DeviceBindingProfile Profile { get; private set; }
    public Guid Id => Profile.Id;
    public string DisplayName => Profile.DisplayName;
    public string DeviceName => FirstMeaningful(Profile.WiredIdentity?.DeviceName,
        Profile.AirPlayIdentity?.DeviceName, Profile.BluetoothIdentity?.DeviceName,
        Profile.DisplayName);
    public string ModelName => FirstMeaningful((Profile.WiredIdentity?.Fingerprint ??
        Profile.AirPlayIdentity?.Fingerprint ?? Profile.DeviceFingerprint)?.ProductName,
        (Profile.WiredIdentity?.Fingerprint ?? Profile.AirPlayIdentity?.Fingerprint ??
        Profile.DeviceFingerprint)?.ProductType, "型号信息待获取");
    internal void Update(DeviceBindingProfile profile) { Profile = profile; PropertyChanged?.Invoke(this, new(null)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string FirstMeaningful(params string?[] values) => values.FirstOrDefault(value =>
        !string.IsNullOrWhiteSpace(value))?.Trim() ?? "未命名设备";
}

public partial class DeviceBindingWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private readonly MainViewModel _viewModel;
    private readonly DeviceBindingManager _manager = DeviceBindingManager.Shared;
    private readonly ObservableCollection<DeviceViewModel> _sourceDevices;
    private ProfileListItem? _selectedProfile;
    private DeviceViewModel? _selectedWiredDevice;
    private DeviceViewModel? _selectedAirPlayDevice;
    private BluetoothClientInfo? _selectedBluetoothClient;

    public ObservableCollection<ProfileListItem> Profiles { get; } = [];
    public ObservableCollection<BluetoothClientInfo> BluetoothClients { get; } = [];
    public IReadOnlyList<DeviceViewModel> WiredDevices => _sourceDevices.Where(device =>
        !device.IsWireless && !device.IsMediaCast).ToArray();
    public IReadOnlyList<DeviceViewModel> AirPlayDevices => _sourceDevices.Where(device =>
        device.IsWireless && !device.IsMediaCast).ToArray();
    public ProfileListItem? SelectedProfile { get => _selectedProfile; set { _selectedProfile = value; SynchronizeSelectedDevices(); NotifyAll(); } }
    public DeviceViewModel? SelectedWiredDevice { get => _selectedWiredDevice; set { _selectedWiredDevice = value; Notify(nameof(CanBindWired)); } }
    public DeviceViewModel? SelectedAirPlayDevice { get => _selectedAirPlayDevice; set { _selectedAirPlayDevice = value; Notify(nameof(CanBindAirPlay)); } }
    public BluetoothClientInfo? SelectedBluetoothClient { get => _selectedBluetoothClient; set { _selectedBluetoothClient = value; Notify(nameof(CanBindBluetooth)); } }
    private DeviceBindingProfile? Profile => SelectedProfile?.Profile;
    public string ProfileTitle => Profile?.DisplayName ?? "选择设备档案";
    public string WiredStatus => Profile?.WiredIdentity is null ? "未绑定" : IsWiredConnected ? "已绑定 · 已连接" : "已绑定 · 当前不可用";
    public string WiredIdentity => Profile?.WiredIdentity?.Udid ?? "";
    public string AirPlayStatus => Profile?.AirPlayIdentity is null ? "未绑定" : IsAirPlayCurrent ? "已绑定 · 当前镜像" : "已绑定 · 当前不可用";
    public string AirPlayIdentity => Profile?.AirPlayIdentity?.StableId ?? "";
    public string BluetoothStatus => Profile?.BluetoothIdentity is null ? "未绑定" : BluetoothClients.Any(client => string.Equals(client.Id, Profile.BluetoothIdentity.StableId, StringComparison.OrdinalIgnoreCase)) ? "已绑定 · 已连接" : "已绑定 · 已断开";
    public string BluetoothIdentity => Profile?.BluetoothIdentity?.StableId ?? "";
    public bool HasWiredBinding => Profile?.WiredIdentity is not null;
    public bool HasAirPlayBinding => Profile?.AirPlayIdentity is not null;
    public bool CanEditWired => Profile is not null && !HasWiredBinding;
    public bool CanEditAirPlay => Profile is not null && !HasAirPlayBinding;
    public bool CanBindWired => CanEditWired && SelectedWiredDevice is not null;
    public bool CanBindAirPlay => CanEditAirPlay && SelectedAirPlayDevice is not null;
    public bool CanBindBluetooth => Profile is not null && SelectedBluetoothClient is not null;
    private bool IsWiredConnected => Profile?.WiredIdentity is { } wired && WiredDevices.Any(device => DeviceViewModel.UdidEquals(device.Udid, wired.Udid));
    private bool IsAirPlayCurrent => Profile?.AirPlayIdentity is { } airPlay && AirPlayDevices.Any(device => DeviceViewModel.UdidEquals(device.Udid, airPlay.StableId));

    internal DeviceBindingWindow(Window owner, ObservableCollection<DeviceViewModel> devices, MainViewModel viewModel)
    {
        Owner = owner; _sourceDevices = devices; _viewModel = viewModel;
        _sourceDevices.CollectionChanged += OnDevicesChanged;
        Closed += (_, _) => _sourceDevices.CollectionChanged -= OnDevicesChanged;
        CreateProfilesForConnectedUsbDevices();
        SynchronizeProfiles();
        DataContext = this; InitializeComponent();
        _ = RefreshBluetoothClientsAsync();
    }

    private void AddAirPlayProfileClick(object sender, RoutedEventArgs e)
    {
        var device = AirPlayDeviceSelectionWindow.Show(this, AirPlayDevices);
        if (device is not null) CreateProfile(device);
    }

    private void CreateProfile(DeviceViewModel device)
    {
        var result = _manager.CreateProfileFromIdentity(device.DisplayName,
            device.IsWireless ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired,
            device.Udid, GetFingerprint(device));
        if (!result.Success || result.Profile is null)
        {
            MessageBox.Show(this, result.Error ?? "无法创建设备档案。", "设备绑定器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SynchronizeProfiles();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == result.Profile.Id);
    }

    private void RenameProfileClick(object sender, RoutedEventArgs e)
    {
        SelectProfileFromMenu(sender);
        if (Profile is null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox("输入设备档案名称", "重命名设备", Profile.DisplayName);
        if (_manager.RenameProfile(Profile.Id, name)) SynchronizeProfiles();
    }

    private void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        SelectProfileFromMenu(sender);
        if (Profile is null || MessageBox.Show(this, "删除该设备档案将解除所有已绑定的 USB、AirPlay 和 Bluetooth 身份。是否继续？", "删除设备档案", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_manager.DeleteProfile(Profile.Id)) { SynchronizeProfiles(); SelectedProfile = Profiles.FirstOrDefault(); }
    }

    private void BindWiredClick(object sender, RoutedEventArgs e) => BindCurrent(DeviceIdentityType.Wired, SelectedWiredDevice);
    private void BindAirPlayClick(object sender, RoutedEventArgs e) => BindCurrent(DeviceIdentityType.AirPlay, SelectedAirPlayDevice);
    private void BindCurrent(DeviceIdentityType type, DeviceViewModel? device)
    {
        if (Profile is null || device is null) return;
        var result = _manager.Bind(Profile.Id, type, device.Udid, device.DisplayName, GetFingerprint(device));
        if (!result.Success && result.Compatibility is DeviceBindingCompatibility.Compatible or DeviceBindingCompatibility.Unknown &&
            MessageBox.Show(this, result.Error + "\n\n确认这是同一台真实设备吗？", "确认设备绑定", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            result = _manager.Bind(Profile.Id, type, device.Udid, device.DisplayName, GetFingerprint(device), true);
        if (result.Success) SynchronizeProfiles();
    }

    private async void ConnectBluetoothClick(object sender, RoutedEventArgs e)
    {
        if (Profile is null) return;
        var profileId = Profile.Id;
        var profileName = Profile.DisplayName;
        var target = Profile.WiredIdentity?.Udid ?? Profile.AirPlayIdentity?.StableId;
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请先为该档案绑定 USB 或 AirPlay 设备。", "连接 Bluetooth",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!await _viewModel.StartBluetoothPeripheralForConfigurationAsync(target))
        {
            MessageBox.Show(this, "无法启动 Bluetooth HID 连接。请确认电脑蓝牙已开启。", "连接 Bluetooth",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var clientId = BluetoothConnectionWindow.Show(this, profileName,
            _viewModel.GetReverseBluetoothClientsAsync, _viewModel.UnbindBluetoothControlBinding);
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            var result = _manager.Bind(profileId, DeviceIdentityType.Bluetooth, clientId,
                clientId, null, userConfirmed: true);
            if (result.Success)
            {
                SynchronizeProfiles();
            }
            else
            {
                MessageBox.Show(this, result.Error ?? "无法保存 Bluetooth 设备绑定。", "连接 Bluetooth",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        await _viewModel.StopBluetoothPeripheralConfigurationAsync();
        await RefreshBluetoothClientsAsync();
    }
    private void UnbindWiredClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.Wired);
    private void UnbindAirPlayClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.AirPlay);
    private void UnbindBluetoothClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.Bluetooth);
    private void Unbind(DeviceIdentityType type) { if (Profile is not null && _manager.Unbind(Profile.Id, type)) SynchronizeProfiles(); }
    private async Task RefreshBluetoothClientsAsync()
    {
        BluetoothClients.Clear();
        foreach (var client in await _viewModel.GetReverseBluetoothClientsAsync()) BluetoothClients.Add(client);
        SynchronizeSelectedDevices();
        NotifyAll();
    }

    private void CreateProfilesForConnectedUsbDevices()
    {
        foreach (var device in WiredDevices)
        {
            if (_manager.FindByIdentity(DeviceIdentityType.Wired, device.Udid) is null)
                _manager.CreateProfileFromIdentity(device.DisplayName, DeviceIdentityType.Wired, device.Udid, GetFingerprint(device));
        }
    }

    private void SynchronizeProfiles()
    {
        var profiles = _manager.Profiles;
        foreach (var item in Profiles.ToArray())
        {
            var profile = profiles.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (profile is null) Profiles.Remove(item); else item.Update(profile);
        }
        foreach (var profile in profiles.Where(profile => Profiles.All(item => item.Id != profile.Id))) Profiles.Add(new ProfileListItem(profile));
        NotifyAll();
    }

    private void SynchronizeSelectedDevices()
    {
        _selectedWiredDevice = Profile?.WiredIdentity is { } wired
            ? WiredDevices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, wired.Udid)) : null;
        _selectedAirPlayDevice = Profile?.AirPlayIdentity is { } airPlay
            ? AirPlayDevices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, airPlay.StableId)) : null;
        _selectedBluetoothClient = Profile?.BluetoothIdentity is { } bluetooth
            ? BluetoothClients.FirstOrDefault(client => string.Equals(client.Id, bluetooth.StableId,
                StringComparison.OrdinalIgnoreCase)) : null;
    }

    private void SelectProfileFromMenu(object sender)
    {
        if (sender is MenuItem { DataContext: ProfileListItem item }) SelectedProfile = item;
    }
    private static DeviceFingerprint GetFingerprint(DeviceViewModel device) => new(device.ProductType, device.ModelDisplay, null, null, device.OsVersion);
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.InvokeAsync(() => { CreateProfilesForConnectedUsbDevices(); SynchronizeProfiles(); Notify(nameof(WiredDevices)); Notify(nameof(AirPlayDevices)); });
    private void NotifyAll() { Notify(nameof(ProfileTitle)); Notify(nameof(WiredStatus)); Notify(nameof(WiredIdentity)); Notify(nameof(AirPlayStatus)); Notify(nameof(AirPlayIdentity)); Notify(nameof(BluetoothStatus)); Notify(nameof(BluetoothIdentity)); Notify(nameof(HasWiredBinding)); Notify(nameof(HasAirPlayBinding)); Notify(nameof(CanEditWired)); Notify(nameof(CanEditAirPlay)); Notify(nameof(CanBindWired)); Notify(nameof(CanBindAirPlay)); Notify(nameof(CanBindBluetooth)); }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
