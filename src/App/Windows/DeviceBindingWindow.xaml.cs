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
    internal void Update(DeviceBindingProfile profile)
    {
        if (Equals(Profile, profile)) return;
        Profile = profile;
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new(nameof(DeviceName)));
        PropertyChanged?.Invoke(this, new(nameof(ModelName)));
    }
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this, new(null));
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string L(string key) => LocalizationService.Get(key);
    private static string FirstMeaningful(params string?[] values) => values.FirstOrDefault(value =>
        !string.IsNullOrWhiteSpace(value))?.Trim() ?? L("DeviceBindingUnnamedDevice");
}

public partial class DeviceBindingWindow : IPhoneMirror.UI.Controls.RoundedWindow, INotifyPropertyChanged
{
    private readonly MainViewModel _viewModel;
    private readonly bool _previewOnly;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _refreshTask = Task.CompletedTask;
    private Task _receiverTask = Task.CompletedTask;
    private Task _bluetoothTask = Task.CompletedTask;
    private Task<IReadOnlyList<BluetoothClientInfo>>? _bluetoothDiscoveryTask;
    private bool _airPlayStarting, _airPlayReady, _airPlayFailed;
    private bool _working, _closed, _closeRequested;
    private string? _targetIdentity;
    private bool _targetPending;
    private System.Windows.Threading.DispatcherOperation? _pendingDeviceUpdate;
    private readonly HashSet<DeviceViewModel> _observedDevices = [];
    internal Func<CancellationToken, Task<IReadOnlyList<BluetoothClientInfo>>> ReadBluetoothClientsAsync { get; set; }
    internal TimeSpan BluetoothDiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(10);
    internal Func<CancellationToken, Task> ScanDevicesAsync { get; set; }
    internal Func<CancellationToken, Task<bool>> StartAirPlayReceiverAsync { get; set; }
    internal Func<string, CancellationToken, Task<bool>> StartBluetoothPeripheralAsync { get; set; }
    internal Func<string, string, bool> ConfirmOperation { get; set; }
    internal Func<string, string?> RequestProfileName { get; set; }
    private readonly HashSet<string> _removedWiredIdentities = new(StringComparer.OrdinalIgnoreCase);
    private string _activityText = "";
    internal Task CleanupTask => Task.WhenAll(_refreshTask, _receiverTask, _bluetoothTask);
    public bool CanRetryAirPlay => _airPlayFailed && !_airPlayStarting;
    public string AirPlayReceiverText => _airPlayStarting ? L("SetupToolsReceiverStarting") :
        _airPlayReady ? L("DeviceBindingAirPlayReceiving") : _airPlayFailed ? L("SetupAirPlayFailed") : "";
    public bool CanInteract => !_working;
    public bool HasProfile => Profile is not null;
    public bool HasProfiles => Profiles.Count > 0;
    public bool HasBluetoothBinding => Profile?.BluetoothIdentity is not null;
    public string ActivityText { get => _activityText; private set { _activityText = value; Notify(); } }
    public bool IsWorking { get => _working; private set { _working = value; Notify(); Notify(nameof(CanInteract)); } }
    private readonly DeviceBindingManager _manager;
    private readonly ObservableCollection<DeviceViewModel> _sourceDevices;
    private ProfileListItem? _selectedProfile;
    private DeviceViewModel? _selectedWiredDevice;
    private DeviceViewModel? _selectedAirPlayDevice;
    private BluetoothClientInfo? _selectedBluetoothClient;

    public ObservableCollection<ProfileListItem> Profiles { get; } = [];
    public ObservableCollection<BluetoothClientInfo> BluetoothClients { get; } = [];
    public ObservableCollection<DeviceViewModel> WiredDevices { get; } = [];
    public ObservableCollection<DeviceViewModel> AirPlayDevices { get; } = [];
    public ProfileListItem? SelectedProfile { get => _selectedProfile; set { if (ReferenceEquals(_selectedProfile, value)) return; _targetPending = false; _selectedProfile = value; _selectedWiredDevice = _selectedAirPlayDevice = null; _selectedBluetoothClient = null; SynchronizeSelectedDevices(); Notify(nameof(SelectedProfile)); NotifyAll(); } }
    public DeviceViewModel? SelectedWiredDevice { get => _selectedWiredDevice; set { _selectedWiredDevice = value; Notify(nameof(CanBindWired)); } }
    public DeviceViewModel? SelectedAirPlayDevice { get => _selectedAirPlayDevice; set { _selectedAirPlayDevice = value; Notify(nameof(CanBindAirPlay)); } }
    public BluetoothClientInfo? SelectedBluetoothClient { get => _selectedBluetoothClient; set { _selectedBluetoothClient = value; Notify(nameof(CanBindBluetooth)); Notify(nameof(CanConnectBluetooth)); } }
    private DeviceBindingProfile? Profile => SelectedProfile?.Profile;
    public string ProfileTitle => string.IsNullOrWhiteSpace(Profile?.DisplayName)
        ? Profile is null ? L("DeviceBindingSelectProfile") : L("DeviceBindingUnnamedDevice")
        : Profile.DisplayName;
    public string WiredStatus => Profile?.WiredIdentity is null ? L("DeviceBindingBound") : IsWiredConnected ? L("DeviceBindingBoundConnected") : L("DeviceBindingBoundUnavailable");
    public string WiredDisplayName => Profile?.WiredIdentity?.DeviceName ?? ProfileTitle;
    public string AirPlayDisplayName => Profile?.AirPlayIdentity?.DeviceName ?? ProfileTitle;
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
    public bool CanBindAirPlay => CanEditAirPlay && SelectedAirPlayDevice is { Ready: true };
    public bool CanBindBluetooth => Profile is not null && SelectedBluetoothClient is not null;
    public bool CanConnectBluetooth => Profile?.WiredIdentity is { Udid.Length: > 0 } || Profile?.AirPlayIdentity is { StableId.Length: > 0 };
    private bool IsWiredConnected => Profile?.WiredIdentity is { } wired && WiredDevices.Any(device => DeviceViewModel.UdidEquals(device.Udid, wired.Udid));
    private bool IsAirPlayCurrent => Profile?.AirPlayIdentity is { } airPlay && AirPlayDevices.Any(device => device.Ready && DeviceViewModel.UdidEquals(device.Udid, airPlay.StableId));

    internal DeviceBindingWindow(Window owner, ObservableCollection<DeviceViewModel> devices,
        MainViewModel viewModel, bool previewOnly = false, DeviceBindingManager? manager = null)
    {
        Owner = owner; _sourceDevices = devices; _viewModel = viewModel;
        _previewOnly = previewOnly;
        _manager = manager ?? DeviceBindingManager.Shared;
        ReadBluetoothClientsAsync = _viewModel.GetReverseBluetoothClientsAsync;
        StartAirPlayReceiverAsync = EnsureAirPlayReceiverAsync;
        StartBluetoothPeripheralAsync = (target, token) =>
            _viewModel.StartBluetoothPeripheralForConfigurationAsync(target, this, token);
        ScanDevicesAsync = async token =>
        {
            await _viewModel.SetupScanAsync(false, token);
            await _viewModel.SetupScanAsync(true, token);
        };
        ConfirmOperation = (title, body) => AppPromptWindow.Confirm(title, body, this);
        RequestProfileName = name => TextInputWindow.Show(this, "DeviceBindingRenameTitle", "DeviceBindingRenamePrompt", name);
        // Attach external events only after construction succeeds.
        DataContext = this; InitializeComponent();
        SynchronizeDeviceLists();
        if (!previewOnly)
        {
            _manager.RefreshFromDisk();
            CreateProfilesForConnectedUsbDevices();
            SynchronizeProfiles();
        }
        _sourceDevices.CollectionChanged += OnDevicesChanged;
        ObserveDevices();
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _pendingDeviceUpdate?.Abort();
            _sourceDevices.CollectionChanged -= OnDevicesChanged;
            foreach (var device in _observedDevices) device.PropertyChanged -= OnDevicePropertyChanged;
            _observedDevices.Clear();
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
        Closing += (_, e) =>
        {
            if (!IsWorking) return;
            e.Cancel = true; _closeRequested = true; _lifetime.Cancel();
        };
        Loaded += (_, _) =>
        {
            if (previewOnly) return;
            _receiverTask = StartAirPlayAsync();
            _refreshTask = RefreshLoopAsync();
        };
    }

    internal static void ShowDeveloperPreview(Window owner, MainViewModel viewModel) =>
        new DeviceBindingWindow(owner, [], viewModel, previewOnly: true).Show();

    private void AddAirPlayProfileClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || IsWorking || _closed) return;
        var device = AirPlayDeviceSelectionWindow.Show(this, _sourceDevices);
        if (device is not null) CreateProfile(device);
    }

    private void CreateProfile(DeviceViewModel device)
    {
        if (_previewOnly || IsWorking || _closed) return;
        var existing = _manager.FindByIdentity(device.IsWireless ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired, device.Udid);
        if (existing is not null)
        {
            SynchronizeProfiles(); SelectedProfile = Profiles.FirstOrDefault(p => p.Id == existing.Id);
            ActivityText = L("SetupBindingSaved"); return;
        }
        var result = _manager.CreateProfileFromIdentity(device.DisplayName,
            device.IsWireless ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired,
            device.Udid, GetFingerprint(device));
        if (!result.Success || result.Profile is null)
        {
            AppPromptWindow.Inform(L("DeviceBindingTitle"), result.Error ?? L("DeviceBindingCreateFailed"), this);
            return;
        }
        SynchronizeProfiles();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == result.Profile.Id);
    }

    private void RenameProfileClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || IsWorking || _closed) return;
        SelectProfileFromMenu(sender);
        if (Profile is not { } profile) return;
        var name = RequestProfileName(profile.DisplayName);
        if (name is null || _closed) return;
        if (_manager.RenameProfile(profile.Id, name)) { SynchronizeProfiles(); ActivityText = L("DeviceBindingSavedFeedback"); }
        else AppPromptWindow.Inform(L("DeviceBindingTitle"), L("DeviceBindingSaveFailed"), this);
    }

    private void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || IsWorking || _closed) return;
        SelectProfileFromMenu(sender);
        if (Profile is not { } profile || !ConfirmOperation(L("DeviceBindingDeleteTitle"), L("DeviceBindingDeleteConfirmation")) || _closed) return;
        var wiredId = profile.WiredIdentity?.Udid;
        if (_manager.DeleteProfile(profile.Id))
        {
            if (wiredId is not null) _removedWiredIdentities.Add(IPhoneFilterDriverService.NormalizeSerial(wiredId));
            SynchronizeProfiles(); ActivityText = L("DeviceBindingDeletedFeedback");
        }
        else AppPromptWindow.Inform(L("DeviceBindingTitle"), L("DeviceBindingSaveFailed"), this);
    }

    private void BindWiredClick(object sender, RoutedEventArgs e) => BindCurrent(DeviceIdentityType.Wired, SelectedWiredDevice);
    private void BindAirPlayClick(object sender, RoutedEventArgs e) => BindCurrent(DeviceIdentityType.AirPlay, SelectedAirPlayDevice);
    private void BindCurrent(DeviceIdentityType type, DeviceViewModel? device)
    {
        if (_previewOnly || IsWorking || _closed || Profile is not { } profile || device is null) return;
        var available = type == DeviceIdentityType.Wired ? WiredDevices : AirPlayDevices;
        if (!available.Any(d => DeviceViewModel.UdidEquals(d.Udid, device.Udid))) { ActivityText = L("SetupDeviceLost"); return; }
        if (type == DeviceIdentityType.AirPlay && !device.Ready) { ActivityText = L("SetupDeviceLost"); return; }
        var result = _manager.Bind(profile.Id, type, device.Udid, device.DisplayName, GetFingerprint(device));
        if (result.RequiresConfirmation)
        {
            if (!ConfirmOperation(L("DeviceBindingConfirmTitle"), F("DeviceBindingConfirmBodyFormat", result.Error)) || _closed) return;
            var candidates = type == DeviceIdentityType.Wired ? WiredDevices : AirPlayDevices;
            if (!candidates.Any(d => DeviceViewModel.UdidEquals(d.Udid, device.Udid) && (type != DeviceIdentityType.AirPlay || d.Ready)))
            { ActivityText = L("SetupDeviceLost"); return; }
            result = _manager.Bind(profile.Id, type, device.Udid, device.DisplayName, GetFingerprint(device), true);
        }
        if (result.Success) { SynchronizeProfiles(); ActivityText = L("DeviceBindingSavedFeedback"); }
        else AppPromptWindow.Inform(L("DeviceBindingTitle"), result.Error ?? L("DeviceBindingCreateFailed"), this);
    }

    private void ConnectBluetoothClick(object sender, RoutedEventArgs e)
    {
        if (IsWorking || _closed) return;
        _bluetoothTask = ConnectBluetoothAsync();
    }

    private async Task ConnectBluetoothAsync()
    {
        if (_previewOnly || _closed || IsWorking || Profile is null) return;
        var profileId = Profile.Id;
        var profileName = Profile.DisplayName;
        var target = Profile.WiredIdentity?.Udid ?? Profile.AirPlayIdentity?.StableId;
        if (string.IsNullOrWhiteSpace(target)) { ActivityText = L("DeviceBindingUsbOrAirPlayRequired"); return; }
        IsWorking = true; ActivityText = L("SetupToolsBluetoothStarting");
        try
        {
            if (!await StartBluetoothPeripheralAsync(target, _lifetime.Token))
                throw new InvalidOperationException(L("DeviceBindingBluetoothStartFailed"));
            _lifetime.Token.ThrowIfCancellationRequested();
            var clientId = BluetoothConnectionWindow.Show(this, profileName,
                ReadBluetoothClientsForWindowAsync, _viewModel.UnbindBluetoothControlBinding);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                var result = _manager.Bind(profileId, DeviceIdentityType.Bluetooth, clientId,
                    clientId, null, userConfirmed: true);
                if (!result.Success) throw new InvalidOperationException(result.Error ?? L("DeviceBindingBluetoothSaveFailed"));
                SynchronizeProfiles();
                ActivityText = L("DeviceBindingSavedFeedback");
            }
            else ActivityText = L("SetupCancelled");
        }
        catch (OperationCanceledException) { ActivityText = L("SetupCancelled"); }
        catch (Exception error) { ActivityText = RecordFailure("device_binding_bluetooth_start_failed", "DeviceBindingOperationFailed", error); }
        finally
        {
            try { await _viewModel.StopBluetoothPeripheralConfigurationAsync(); }
            catch (Exception error) { ActivityText = RecordFailure("device_binding_bluetooth_stop_failed", "DeviceBindingOperationFailed", error); }
            IsWorking = false;
            if (_closeRequested) Close();
        }
    }

    private void StartAirPlayClick(object sender, RoutedEventArgs e)
    {
        if (_previewOnly || _closed || _airPlayStarting || _airPlayReady) return;
        _receiverTask = StartAirPlayAsync();
    }

    private async Task<bool> EnsureAirPlayReceiverAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_viewModel.SelectedWirelessReceiverBackend.Backend == WirelessReceiverBackend.UxPlay &&
            !new WirelessReceiverService().IsBackendAvailable(WirelessReceiverBackend.UxPlay))
        {
            var download = new ComponentDownloadWindow { Owner = this };
            if (download.ShowDialog() != true) return false;
        }
        token.ThrowIfCancellationRequested();
        return await _viewModel.SetupReceiverAsync(token);
    }

    private async Task StartAirPlayAsync()
    {
        _airPlayStarting = true; _airPlayFailed = false; NotifyAirPlayReceiver();
        try
        {
            // Let the binder render before checking components or opening a download window.
            await Task.Yield();
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!await StartAirPlayReceiverAsync(_lifetime.Token))
                throw new InvalidOperationException(L("SetupAirPlayFailed"));
            _lifetime.Token.ThrowIfCancellationRequested();
            _airPlayReady = true;
            if (!IsWorking) ActivityText = L("DeviceBindingAirPlayReady");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            _airPlayFailed = true;
            if (!_closed && !IsWorking) ActivityText = RecordFailure("device_binding_airplay_failed", "SetupAirPlayFailed", error);
        }
        finally { _airPlayStarting = false; if (!_closed) NotifyAirPlayReceiver(); }
    }

    private void NotifyAirPlayReceiver() { Notify(nameof(AirPlayReceiverText)); Notify(nameof(CanRetryAirPlay)); }

    internal void SelectIdentity(string? identity)
    {
        _targetIdentity = identity;
        _targetPending = !string.IsNullOrWhiteSpace(identity);
        var profile = string.IsNullOrEmpty(identity) ? null :
            _manager.FindByIdentity(DeviceIdentityType.Wired, identity) ?? _manager.FindByIdentity(DeviceIdentityType.AirPlay, identity);
        var target = Profiles.FirstOrDefault(p => p.Id == profile?.Id);
        if (target is not null) _targetPending = false;
        SelectedProfile = target ?? SelectedProfile ?? Profiles.FirstOrDefault();
        _targetPending = target is null && !string.IsNullOrWhiteSpace(identity);
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (!IsWorking)
                {
                    try
                    {
                        // Startup suspends the main refresh loop. The binder owns discovery while open.
                        if (_viewModel.SetupActive)
                        {
                            await ScanDevicesAsync(_lifetime.Token);
                        }
                        _lifetime.Token.ThrowIfCancellationRequested();
                        if (IsWorking) continue;
                        _manager.RefreshFromDisk();
                        CreateProfilesForConnectedUsbDevices(); SynchronizeProfiles();
                        await RefreshBluetoothClientsAsync();
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                    catch (Exception error) { if (!_closed) ActivityText = RecordFailure("device_binding_refresh_failed", "DeviceBindingRefreshFailed", error); }
                }
                await Task.Delay(1800, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string RecordFailure(string eventName, string resourceKey, Exception error)
    {
        DiagnosticLogger.Exception("device-binding", eventName, error);
        return LocalizationService.ErrorMessage(error, resourceKey);
    }
    private void UnbindWiredClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.Wired);
    private void UnbindAirPlayClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.AirPlay);
    private void UnbindBluetoothClick(object sender, RoutedEventArgs e) => Unbind(DeviceIdentityType.Bluetooth);
    private void Unbind(DeviceIdentityType type)
    {
        if (_previewOnly || IsWorking || _closed || Profile is null) return;
        var wiredId = type == DeviceIdentityType.Wired ? Profile.WiredIdentity?.Udid : null;
        if (_manager.Unbind(Profile.Id, type))
        {
            if (wiredId is not null) _removedWiredIdentities.Add(IPhoneFilterDriverService.NormalizeSerial(wiredId));
            SynchronizeProfiles(); ActivityText = L("DeviceBindingUnboundFeedback");
        }
        else AppPromptWindow.Inform(L("DeviceBindingTitle"), L("DeviceBindingSaveFailed"), this);
    }
    private async Task RefreshBluetoothClientsAsync()
    {
        if (_previewOnly) return;
        var clients = await ReadBluetoothClientsForWindowAsync();
        if (_closed || IsWorking || _lifetime.IsCancellationRequested) return;
        for (var i = BluetoothClients.Count - 1; i >= 0; i--)
            if (!clients.Any(c => string.Equals(c.Id, BluetoothClients[i].Id, StringComparison.OrdinalIgnoreCase))) BluetoothClients.RemoveAt(i);
        foreach (var client in clients)
        {
            var index = BluetoothClients.ToList().FindIndex(c => string.Equals(c.Id, client.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) BluetoothClients.Add(client);
            else if (!Equals(BluetoothClients[index], client)) BluetoothClients[index] = client;
        }
        SynchronizeSelectedDevices();
        NotifyAll();
    }

    private async Task<IReadOnlyList<BluetoothClientInfo>> ReadBluetoothClientsForWindowAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(BluetoothDiscoveryTimeout);
        cancellation.Token.ThrowIfCancellationRequested();
        // Reuse an in-flight native query even if a driver ignores cancellation.
        // Closing the window can finish cleanup without starting more queries
        // or allowing a late result to update the UI.
        if (_bluetoothDiscoveryTask is null || _bluetoothDiscoveryTask.IsCompleted)
        {
            _bluetoothDiscoveryTask = ReadBluetoothClientsAsync(cancellation.Token);
            _ = _bluetoothDiscoveryTask.ContinueWith(task =>
                DiagnosticLogger.Exception("device-binding", "bluetooth_discovery_failed",
                    task.Exception!.GetBaseException()), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        try { return await _bluetoothDiscoveryTask.WaitAsync(cancellation.Token); }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException(L("UiBluetoothRefreshFailed"));
        }
    }

    private void CreateProfilesForConnectedUsbDevices()
    {
        foreach (var device in WiredDevices.Where(d => !string.IsNullOrWhiteSpace(d.Udid)))
        {
            if (!_removedWiredIdentities.Contains(IPhoneFilterDriverService.NormalizeSerial(device.Udid)) &&
                _manager.FindByIdentity(DeviceIdentityType.Wired, device.Udid) is null)
            {
                var result = _manager.CreateProfileFromIdentity(device.DisplayName, DeviceIdentityType.Wired, device.Udid, GetFingerprint(device));
                if (!result.Success) ActivityText = result.Error ?? L("DeviceBindingCreateFailed");
            }
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
        if (_targetPending)
        {
            var target = profiles.FirstOrDefault(p => DeviceViewModel.UdidEquals(p.WiredIdentity?.Udid, _targetIdentity) ||
                DeviceViewModel.UdidEquals(p.AirPlayIdentity?.StableId, _targetIdentity));
            if (target is not null) { SelectedProfile = Profiles.First(p => p.Id == target.Id); _targetPending = false; }
        }
        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile)) SelectedProfile = Profiles.FirstOrDefault();
        SynchronizeSelectedDevices(); NotifyAll();
    }

    private void SynchronizeSelectedDevices()
    {
        _selectedWiredDevice = Profile?.WiredIdentity is { } wired
            ? WiredDevices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, wired.Udid)) : WiredDevices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, _selectedWiredDevice?.Udid));
        _selectedAirPlayDevice = Profile?.AirPlayIdentity is { } airPlay
            ? AirPlayDevices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, airPlay.StableId)) : AirPlayDevices.FirstOrDefault(device => DeviceViewModel.UdidEquals(device.Udid, _selectedAirPlayDevice?.Udid));
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
        ActivityText = LocalizationService.RefreshText(ActivityText);
        foreach (var profile in Profiles) profile.NotifyLanguageChanged();
        foreach (var client in BluetoothClients) client.NotifyLanguageChanged();
        NotifyAirPlayReceiver();
        NotifyAll();
    }
    private void SynchronizeDeviceLists()
    {
        void Reconcile(ObservableCollection<DeviceViewModel> target, bool wireless)
        {
            var current = _sourceDevices.Where(d => d.IsWireless == wireless && !d.IsMediaCast).ToArray();
            foreach (var old in target.Where(d => !current.Contains(d)).ToArray()) target.Remove(old);
            foreach (var device in current.Where(d => !target.Contains(d))) target.Add(device);
        }
        Reconcile(WiredDevices, false); Reconcile(AirPlayDevices, true);
    }
    private void ObserveDevices()
    {
        foreach (var old in _observedDevices.Where(d => !_sourceDevices.Contains(d)).ToArray())
        { old.PropertyChanged -= OnDevicePropertyChanged; _observedDevices.Remove(old); }
        foreach (var device in _sourceDevices.Where(d => !_observedDevices.Contains(d)))
        { _observedDevices.Add(device); device.PropertyChanged += OnDevicePropertyChanged; }
    }
    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { ObserveDevices(); SynchronizeDeviceLists(); ScheduleDeviceUpdate(); }
    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleDeviceUpdate();
    private void ScheduleDeviceUpdate()
    {
        if (_closed || _pendingDeviceUpdate is { Status: System.Windows.Threading.DispatcherOperationStatus.Pending }) return;
        _pendingDeviceUpdate = Dispatcher.InvokeAsync(() =>
        {
            _pendingDeviceUpdate = null;
            if (_closed) return;
            if (!_previewOnly && !IsWorking) { CreateProfilesForConnectedUsbDevices(); SynchronizeProfiles(); }
            if (!IsWorking) SynchronizeSelectedDevices();
            NotifyAll();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
    private void NotifyAll() { Notify(nameof(WiredDisplayName)); Notify(nameof(AirPlayDisplayName)); Notify(nameof(HasProfiles)); Notify(nameof(HasProfile)); Notify(nameof(HasBluetoothBinding)); Notify(nameof(SelectedWiredDevice)); Notify(nameof(SelectedAirPlayDevice)); Notify(nameof(SelectedBluetoothClient)); Notify(nameof(ProfileTitle)); Notify(nameof(WiredStatus)); Notify(nameof(WiredIdentity)); Notify(nameof(AirPlayStatus)); Notify(nameof(AirPlayIdentity)); Notify(nameof(BluetoothStatus)); Notify(nameof(BluetoothIdentity)); Notify(nameof(HasWiredBinding)); Notify(nameof(HasAirPlayBinding)); Notify(nameof(CanEditWired)); Notify(nameof(CanEditAirPlay)); Notify(nameof(CanBindWired)); Notify(nameof(CanBindAirPlay)); Notify(nameof(CanBindBluetooth)); Notify(nameof(CanConnectBluetooth)); }
    private static string L(string key) => LocalizationService.Get(key);
    private static string F(string key, params object?[] args) => LocalizationService.Format(key, args);
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
