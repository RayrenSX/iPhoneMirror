using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Models;

public sealed class DeviceViewModel : INotifyPropertyChanged
{
    internal const string WirelessUdidPrefix = "airplay://";
    internal const string MediaCastUdid = "media-cast://active";

    private string _name;
    private string _productType;
    private string _osVersion;
    private string _connectionType;
    private string _status;
    private ConnectionState _state;

    private DeviceViewModel(string udid, string name, string productType,
        string osVersion, string connectionType, string status, ConnectionState state)
    {
        Udid = udid;
        _name = name;
        _productType = productType;
        _osVersion = osVersion;
        _connectionType = connectionType;
        _status = status;
        _state = state;
    }

    public string Udid { get; }
    public string Name => _name;
    public string ProductType => _productType;
    public string OsVersion => _osVersion;
    public string ConnectionType => _connectionType;
    public string Status => _status;
    internal ConnectionState State => _state;
    public bool IsWireless => IsWirelessUdid(Udid);
    public bool IsMediaCast => IsMediaCastUdid(Udid);
    public string AutomationId => IsMediaCast ? "MediaCastDeviceCard" : "DeviceCard";

    public string DisplayName => IsMediaCast
        ? LocalizationService.Get("MediaCastDeviceName")
        : string.IsNullOrWhiteSpace(Name) ? "iPhone" : Name;
    public string ModelDisplay => IsMediaCast
        ? LocalizationService.Get("MediaCastDeviceModel")
        : string.IsNullOrWhiteSpace(ProductType)
        ? IsWireless ? "AirPlay" : LocalizationService.Get("ModelLoading")
        : AppleProductNames.Resolve(ProductType);
    public string OsDisplay => IsMediaCast
        ? LocalizationService.Get("MediaCastDeviceConnection")
        : string.IsNullOrWhiteSpace(OsVersion)
        ? IsWireless ? LocalizationService.Get("WirelessLocalNetwork") : "iOS -"
        : IsWireless
            ? $"iOS {OsVersion} · {LocalizationService.Get("WirelessLocalNetwork")}"
            : $"iOS {OsVersion}";
    public string ShortUdid => IsMediaCast ? "AirPlay / DLNA" : IsWireless ? "AirPlay" :
        Udid.Length <= 18 ? Udid : $"{Udid[..8]}...{Udid[^6..]}";
    public bool Ready => State is ConnectionState.Ready;
    public string StatusDisplay => IsMediaCast
        ? LocalizationService.Get("MediaCastDeviceActive")
        : IsWireless ? LocalizationService.Get("WirelessConnected") : LocalizationService.Get(State switch
        {
            ConnectionState.Disconnected => "ConnectionDisconnected",
            ConnectionState.UsbPresentNoMux => "ConnectionUsbNoMux",
            ConnectionState.Connected => "ConnectionConnected",
            ConnectionState.Paired => "ConnectionPaired",
            ConnectionState.Ready => "ConnectionReady",
            _ => "ConnectionError",
        });

    internal bool UpdateFrom(DeviceViewModel source)
    {
        if (!UdidEquals(Udid, source.Udid))
            throw new ArgumentException("Cannot update a device item from a different UDID.",
                nameof(source));

        var changed = false;
        changed |= Set(ref _name, source.Name, nameof(Name), nameof(DisplayName));
        changed |= Set(ref _productType, source.ProductType, nameof(ProductType), nameof(ModelDisplay));
        changed |= Set(ref _osVersion, source.OsVersion, nameof(OsVersion), nameof(OsDisplay));
        changed |= Set(ref _connectionType, source.ConnectionType, nameof(ConnectionType));
        changed |= Set(ref _status, source.Status, nameof(Status));
        changed |= Set(ref _state, source.State, nameof(State), nameof(Ready), nameof(StatusDisplay));
        return changed;
    }

    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(OsDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
    }

    private bool Set<T>(ref T field, T value, params string[] propertyNames)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        foreach (var propertyName in propertyNames) OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static bool UdidEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static bool IsWirelessUdid(string? udid) => udid?.StartsWith(
        WirelessUdidPrefix, StringComparison.OrdinalIgnoreCase) == true;

    internal static string? GetUsbUdid(string? udid)
    {
        if (string.IsNullOrWhiteSpace(udid)) return null;
        return IsWirelessUdid(udid) ? udid[WirelessUdidPrefix.Length..] : udid;
    }

    internal static bool IsMediaCastUdid(string? udid) => string.Equals(
        udid, MediaCastUdid, StringComparison.OrdinalIgnoreCase);

    internal static DeviceViewModel CreateMediaCast() => new(
        MediaCastUdid,
        LocalizationService.Get("MediaCastDeviceName"),
        string.Empty,
        string.Empty,
        "AirPlay / DLNA",
        string.Empty,
        ConnectionState.Ready);

    internal static DeviceViewModel FromNative(NativeDeviceInfo info) => new(
        info.Udid ?? string.Empty,
        info.Name ?? "iPhone",
        info.ProductType ?? string.Empty,
        info.OsVersion ?? string.Empty,
        info.ConnectionType ?? "USB",
        info.Status ?? string.Empty,
        info.State);

    internal DeviceViewModel AsUsbPresentNoMux() => new(
        Udid, Name, ProductType, OsVersion, ConnectionType, Status,
        ConnectionState.UsbPresentNoMux);
}
