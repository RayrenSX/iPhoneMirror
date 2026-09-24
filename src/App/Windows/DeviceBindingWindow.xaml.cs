using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.ViewModels;

namespace IPhoneMirror.App.Windows;

public sealed class ProfileListItem : INotifyPropertyChanged
{
    internal ProfileListItem(DeviceBindingProfile profile) => Profile = profile;
    internal DeviceBindingProfile Profile { get; private set; }
    public Guid Id => Profile.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(Profile.DisplayName)
        ? L("DeviceBindingUnnamedDevice") : Profile.DisplayName;
    public string DeviceName => FirstMeaningful(Profile.WiredIdentity?.DeviceName,
        Profile.AirPlayIdentity?.DeviceName, Profile.BluetoothIdentity?.DeviceName,
        Profile.DisplayName);
    public string ModelName => FirstMeaningful((Profile.WiredIdentity?.Fingerprint ??
        Profile.AirPlayIdentity?.Fingerprint ?? Profile.DeviceFingerprint)?.ProductName,
        (Profile.WiredIdentity?.Fingerprint ?? Profile.AirPlayIdentity?.Fingerprint ??
        Profile.DeviceFingerprint)?.ProductType, L("DeviceBindingModelUnknown"));
    internal void Update(DeviceBindingProfile profile) { Profile = profile; PropertyChanged?.Invoke(this, new(null)); }
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this, new(null));
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string L(string key) => LocalizationService.Get(key);
    private static string FirstMeaningful(params string?[] values) => values.FirstOrDefault(value =>
        !string.IsNullOrWhiteSpace(value))?.Trim() ?? L("DeviceBindingUnnamedDevice");
}

public partial class DeviceBindingWindow : Wpf.Ui.Controls.FluentWindow, INotifyPropertyChanged
{
    private readonly MainViewModel _viewModel;
    private readonly bool _previewOnly;
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
    public string ProfileTitle => string.IsNullOrWhiteSpace(Profile?.DisplayName)
        ? Profile is null ? L("DeviceBindingSelectProfile") : L("DeviceBindingUnnamedDevice")
        : Profile.DisplayName;
    public string WiredStatus => Profile?.WiredIdentity is null ? L("DeviceBindingBound") : IsWiredConnected ? L("DeviceBindingBoundConnected") : L("DeviceBindingBoundUnavailable");
    public string WiredIdentity => Profile?.WiredIdentity?.Udid ?? "";
    public string AirPlayStatus => Profile?.AirPlayIdentity is null ? L("DeviceBindingBound") : IsAirPlayCurrent ? L("DeviceBindingBoundMirroring") : L("DeviceBindingBoundUnavailable");
    public string AirPlayIdentity => Profile?.AirPlayIdentity?.StableId ?? "";
    public string BluetoothStatus => Profile?.BluetoothIdentity is null ? L("DeviceBindingBound") : BluetoothClients.Any(client => string.Equals(client.Id, Profile.BluetoothIdentity.StableId, StringComparison.OrdinalIgnoreCase)) ? L("DeviceBindingBoundConnected") : L("DeviceBindingBoundDisconnected");
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

    internal DeviceBindingWindow(Window owner, ObservableCollection<DeviceViewModel> devices,
        MainViewModel viewModel, bool previewOnly = false)
    {
        Owner = owner; _sourceDevices = devices; _viewModel = viewModel;
        _previewOnly = previewOnly;
        _sourceDevices.CollectionChanged += OnDevicesChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _sourceDevices.CollectionChanged -= OnDevicesChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
        if (!previewOnly)
        {
            CreateProfilesForConnectedUsbDevices();
            SynchronizeProfiles();
        }
        DataContext = this; InitializeComponent();
        if (!previewOnly) _ = RefreshBluetoothClientsAsync();
    }

    internal static void ShowDeveloperPreview(Window owner, MainViewModel viewModel) =>
        new DeviceBindingWindow(owner, [], viewModel, previewOnly: true).Show();

    private void AddAirPlayProfileClick(object sender, RoutedEventArgs e)
    {
        var device = AirPlayDeviceSelectionWindow.Show(this, AirPlayDevices);
        if (device is not null) CreateProfile(device);
    }

    private void CreateProfile(DeviceViewModel device)
    {
        if (_previewOnly) return;
        var result = _manager.CreateProfileFromIdentity(device.DisplayName,
            device.IsWireless ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired,
            device.Udid, GetFingerprint(device));
        if (!result.Success || result.Profile is null)
        {
            AppPromptWindow.Inform(L("DeviceBindingTitle"), L("DeviceBindingCreateFailed"), this);
            return;
        }
        SynchronizeProfiles();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == result.Profile.Id);
    }

    private void RenameProfileClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        SelectProfileFromMenu(sender);
        if (Profile is null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox(L("DeviceBindingRenamePrompt"), L("DeviceBindingRenameTitle"), Profile.DisplayName);
        if (_manager.RenameProfile(Profile.Id, name)) SynchronizeProfiles();
    }

    private void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        SelectProfileFromMenu(sender);
        if (Profile is null || !AppPromptWindow.Confirm(L("DeviceBindingDeleteTitle"), L("DeviceBindingDeleteConfirmation"), this)) return;
        if (_manager.DeleteProfile(Profile.Id)) { SynchronizeProfiles(); SelectedProfile = Profiles.FirstOrDefault(); }
    }

    private void BindWiredClick(object sender, RoutedEventArgs e) => BindCurrent(DeviceIdentityType.Wired, SelectedWiredDevice);
    private void BindAirPlayClick(object sender, RoutedEventArgs e) => BindCurrent(DeviceIdentityType.AirPlay, SelectedAirPlayDevice);
    private void BindCurrent(DeviceIdentityType type, DeviceViewModel? device)
    {
        if (_previewOnly) return;
        if (Profile is null || device is null) return;
        var result = _manager.Bind(Profile.Id, type, device.Udid, device.DisplayName, GetFingerprint(device));
        if (!result.Success && result.Compatibility is DeviceBindingCompatibility.Compatible or DeviceBindingCompatibility.Unknown &&
            AppPromptWindow.Confirm(L("DeviceBindingConfirmTitle"), F("DeviceBindingConfirmBodyFormat", result.Error), this))
            result = _manager.Bind(Profile.Id, type, device.Udid, device.DisplayName, GetFingerprint(device), true);
        if (result.Success) SynchronizeProfiles();
    }

    private async void ConnectBluetoothClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly) return;
        if (Profile is null) return;
        var profileId = Profile.Id;
        var profileName = Profile.DisplayName;
        var target = Profile.WiredIdentity?.Udid ?? Profile.AirPlayIdentity?.StableId;
        if (string.IsNullOrWhiteSpace(target))
        {
            AppPromptWindow.Inform(L("DeviceBindingConnect"), L("DeviceBindingUsbOrAirPlayRequired"), this);
            return;
        }
        if (!await _viewModel.StartBluetoothPeripheralForConfigurationAsync(target))
        {
            AppPromptWindow.Inform(L("DeviceBindingConnect"), L("DeviceBindingBluetoothStartFailed"), this);
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
                AppPromptWindow.Inform(L("DeviceBindingConnect"), L("DeviceBindingBluetoothSaveFailed"), this);
            }
        }
        await _viewModel.StopBluetoothPeripheralConfigurationAsync();
        await RefreshBluetoothClientsAsync();
    }
    private void UnbindWiredClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.Wired);
    private void UnbindAirPlayClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.AirPlay);
    private void UnbindBluetoothClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.Bluetooth);
    private void Unbind(DeviceIdentityType type) { if (!_previewOnly && Profile is not null && _manager.Unbind(Profile.Id, type)) SynchronizeProfiles(); }
    private async Task RefreshBluetoothClientsAsync()
    {
        if (_previewOnly) return;
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
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var profile in Profiles) profile.NotifyLanguageChanged();
        NotifyAll();
    }
    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.InvokeAsync(() => { if (!_previewOnly) { CreateProfilesForConnectedUsbDevices(); SynchronizeProfiles(); } Notify(nameof(WiredDevices)); Notify(nameof(AirPlayDevices)); });
    private void NotifyAll() { Notify(nameof(ProfileTitle)); Notify(nameof(WiredStatus)); Notify(nameof(WiredIdentity)); Notify(nameof(AirPlayStatus)); Notify(nameof(AirPlayIdentity)); Notify(nameof(BluetoothStatus)); Notify(nameof(BluetoothIdentity)); Notify(nameof(HasWiredBinding)); Notify(nameof(HasAirPlayBinding)); Notify(nameof(CanEditWired)); Notify(nameof(CanEditAirPlay)); Notify(nameof(CanBindWired)); Notify(nameof(CanBindAirPlay)); Notify(nameof(CanBindBluetooth)); }
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object?[] args) => LocalizationService.Format(key, args);
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
