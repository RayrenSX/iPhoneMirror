using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    // Keep every transport in Devices for sessions, binding and control. The
    // source picker presents one representative per confirmed physical device.
    public ObservableCollection<DeviceViewModel> DeviceCards { get; } = [];
    private bool _deviceCardRefreshQueued;
    private bool _refreshingDeviceCards;
    private bool _deferDeviceCardRefresh;

    private string PhysicalDeviceCardKey(DeviceViewModel device) => PhysicalDeviceCardKey(device.Udid);

    private string PhysicalDeviceCardKey(string udid)
    {
        var wireless = DeviceViewModel.IsWirelessUdid(udid);
        var profile = _reverseBindings.FindByIdentity(wireless ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired, udid);
        if (profile?.WiredIdentity is { } wired)
            return "apple:" + PairedDeviceDiscovery.Key(wired.Udid);
        if (profile is not null) return "profile:" + profile.Id.ToString("N");
        return wireless || DeviceViewModel.IsMediaCastUdid(udid)
            ? "source:" + udid.ToLowerInvariant()
            : "apple:" + PairedDeviceDiscovery.Key(udid);
    }

    private int DeviceCardPriority(DeviceViewModel device)
    {
        var session = _sessions.Get(device.Udid);
        var live = session is { HasSession: true, IsStopping: false };
        var streaming = live && session!.IsMirroring;
        return (streaming ? 600 : live ? 400 : !device.IsWireless && !device.IsPairedNetwork ? 200 : device.IsPairedNetwork ? 100 : 0)
            + (live && ReferenceEquals(SelectedDevice, device) ? 10 : 0);
    }

    internal DeviceViewModel? GetDevicePowerActionSource(DeviceViewModel device) => device.IsMediaCast ? null :
        Devices.FirstOrDefault(source => PhysicalDeviceCardKey(source) == PhysicalDeviceCardKey(device) &&
            DeviceActionService.CanExecute(source));

    private void QueueDeviceCardRefresh()
    {
        if (_disposed || _deviceCardRefreshQueued || _refreshingDeviceCards || Application.Current is null) return;
        _deviceCardRefreshQueued = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _deviceCardRefreshQueued = false;
            if (!_disposed) RefreshDeviceCards();
        });
    }

    private void RefreshDeviceCards()
    {
        if (_refreshingDeviceCards) return;
        _refreshingDeviceCards = true;
        try
        {
            var selectedKey = SelectedDevice is { } selected ? PhysicalDeviceCardKey(selected) : null;
            var representatives = Devices.GroupBy(PhysicalDeviceCardKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(DeviceCardPriority).First(), StringComparer.OrdinalIgnoreCase);
            var order = DeviceCards.Select(PhysicalDeviceCardKey).Concat(Devices.Select(PhysicalDeviceCardKey))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(representatives.ContainsKey).ToArray();
            var count = DeviceCards.Count;
            for (var index = 0; index < order.Length; index++)
            {
                var incoming = representatives[order[index]];
                if (index < DeviceCards.Count && PhysicalDeviceCardKey(DeviceCards[index]) == order[index])
                {
                    if (!ReferenceEquals(DeviceCards[index], incoming)) DeviceCards[index] = incoming;
                    continue;
                }
                var existing = DeviceCards.FirstOrDefault(card => PhysicalDeviceCardKey(card) == order[index]);
                if (existing is not null)
                {
                    DeviceCards.Move(DeviceCards.IndexOf(existing), index);
                    if (!ReferenceEquals(existing, incoming)) DeviceCards[index] = incoming;
                }
                else DeviceCards.Insert(index, incoming);
            }
            while (DeviceCards.Count > order.Length) DeviceCards.RemoveAt(DeviceCards.Count - 1);
            if (count != DeviceCards.Count) OnPropertyChanged(nameof(DeviceCount));
            if (selectedKey is not null && representatives.TryGetValue(selectedKey, out var representative))
            {
                SetSelectedDevice(representative, updateDriverStatus: false);
            }
        }
        finally { _refreshingDeviceCards = false; }
    }

    private void RefreshSourceCardMirroring(string udid)
    {
        var device = Devices.FirstOrDefault(candidate => DeviceViewModel.UdidEquals(candidate.Udid, udid));
        if (device is not null) RefreshSourceCardMirroring(device);
    }

    private void RefreshSourceCardMirroring(DeviceViewModel device)
    {
        device.SetMirroring(_sessions.Get(device.Udid) is { HasSession: true, IsStopping: false, IsMirroring: true });
        QueueDeviceCardRefresh();
    }

    internal SourceCardFields VisibleSourceCardFields
    {
        get
        {
            var saved = (Application.Current as App)?.UpdateSettings.SourceCardFields ?? SourceCardFields.Default;
            var supported = saved & SourceCardFields.All;
            return supported == 0 ? SourceCardFields.Default : supported;
        }
    }

    public bool ShowSourceCardIcon => VisibleSourceCardFields.HasFlag(SourceCardFields.Icon);
    public bool ShowSourceCardName => VisibleSourceCardFields.HasFlag(SourceCardFields.Name);
    public bool ShowSourceCardModel => VisibleSourceCardFields.HasFlag(SourceCardFields.Model);
    public bool ShowSourceCardSystemVersion => VisibleSourceCardFields.HasFlag(SourceCardFields.SystemVersion);
    public bool ShowSourceCardStatus => VisibleSourceCardFields.HasFlag(SourceCardFields.Status);
    public bool ShowSourceCardTransport => VisibleSourceCardFields.HasFlag(SourceCardFields.Transport);
    public bool ShowSourceCardConnection => ShowSourceCardStatus || ShowSourceCardTransport;

    internal bool TrySetSourceCardFields(SourceCardFields fields)
    {
        fields &= SourceCardFields.All;
        if (fields == 0 || Application.Current is not App app) return false;
        var previous = app.UpdateSettings.SourceCardFields;
        if (previous == fields) return true;
        app.UpdateSettings.SourceCardFields = fields;
        if (!app.SaveUpdateSettings())
        {
            app.UpdateSettings.SourceCardFields = previous;
            return false;
        }

        OnPropertyChanged(nameof(ShowSourceCardIcon));
        OnPropertyChanged(nameof(ShowSourceCardName));
        OnPropertyChanged(nameof(ShowSourceCardModel));
        OnPropertyChanged(nameof(ShowSourceCardSystemVersion));
        OnPropertyChanged(nameof(ShowSourceCardStatus));
        OnPropertyChanged(nameof(ShowSourceCardTransport));
        OnPropertyChanged(nameof(ShowSourceCardConnection));
        return true;
    }
}
