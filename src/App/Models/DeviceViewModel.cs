using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.Models;

internal sealed record WiredDeviceMetadata(string Name, string ProductType, string OsVersion);

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
    private bool _captureNeedsPreparation;
    private bool _isMirroring;
    private DeviceAppearance _appearance = DeviceAppearance.Unknown;
    internal DeviceAppearance Appearance => _appearance;

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
    public bool IsPairedNetwork => string.Equals(ConnectionType, "Network", StringComparison.OrdinalIgnoreCase);
    public bool IsMediaCast => IsMediaCastUdid(Udid);
    public bool IsTablet => ProductType.StartsWith("iPad", StringComparison.OrdinalIgnoreCase);
    public string AutomationId => IsMediaCast ? "MediaCastDeviceCard" : "DeviceCard";

    public string DisplayName => IsMediaCast
        ? LocalizationService.Get("MediaCastDeviceName")
        : IsWindowsUsbName(Name)
        ? string.IsNullOrWhiteSpace(ProductType) ? LocalizationService.Get("WiredAppleDeviceName") : ModelDisplay
        : string.IsNullOrWhiteSpace(Name) ? "iPhone" : Name;
    public string ModelDisplay => IsMediaCast
        ? LocalizationService.Get("MediaCastDeviceModel")
        : string.IsNullOrWhiteSpace(ProductType)
        ? IsWireless ? "AirPlay" : LocalizationService.Get(
            State == ConnectionState.UsbPresentNoMux ? "DeviceBindingModelUnknown" : "ModelLoading")
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
    public bool IsDisconnected => State is ConnectionState.Disconnected;
    public bool HasConnectionError => State is ConnectionState.Error;
    internal bool HasWiredDisplayMetadata => !string.IsNullOrWhiteSpace(Name) && !IsWindowsUsbName(Name) &&
        !string.IsNullOrWhiteSpace(ProductType) && !string.IsNullOrWhiteSpace(OsVersion);
    public bool HasPendingWiredReadiness => !IsWireless && !IsMediaCast && !IsPairedNetwork &&
        (!Ready || _captureNeedsPreparation);
    public string CardTransport => IsMediaCast ? "AirPlay / DLNA" : IsWireless ? "AirPlay" : IsPairedNetwork ? "Wi-Fi" :
        string.IsNullOrWhiteSpace(ConnectionType) ? "—" : ConnectionType;
    public string CardModel => IsMediaCast || string.IsNullOrWhiteSpace(ProductType) ? "—" : ModelDisplay;
    public string CardSystemVersion => IsMediaCast || string.IsNullOrWhiteSpace(OsVersion)
        ? "—" : $"{(IsTablet ? "iPadOS" : "iOS")} {OsVersion}";
    public string StatusDisplay => LocalizationService.Get(StatusDisplayKey);
    public bool IsMirroring => _isMirroring;
    public string CardStatusDetail => IsMirroring ? CardStatusDisplay : StatusDisplay;
    public string CardStatusDisplay => LocalizationService.Get(IsMirroring ? "DeviceCardStatusMirroring" : StatusDisplayKey switch
    {
        "ConnectionUsbNoMux" or "ConnectionUsbCommunicationPending" => "DeviceCardStatusWaitingForConnection",
        "ConnectionUsbCapturePending" => "DeviceCardStatusCapturePending",
        "ConnectionPaired" => "DeviceCardStatusUnlock",
        "ConnectionReady" => "DeviceCardStatusReady",
        "WirelessConnected" => "DeviceCardStatusConnected",
        "MediaCastDeviceActive" => "DeviceCardStatusCasting",
        var key => key,
    });

    private string StatusDisplayKey => IsMediaCast ? "MediaCastDeviceActive"
        : IsPairedNetwork ? "PairedNetworkOnline"
        : IsWireless ? "WirelessConnected" : State switch
        {
            ConnectionState.Disconnected => "ConnectionDisconnected",
            ConnectionState.UsbPresentNoMux => "ConnectionUsbNoMux",
            ConnectionState.Connected => "ConnectionUsbCommunicationPending",
            ConnectionState.Paired => "ConnectionPaired",
            ConnectionState.Ready => _captureNeedsPreparation
                ? "ConnectionUsbCapturePending" : "ConnectionReady",
            _ => "ConnectionError",
        };

    internal bool UpdateFrom(DeviceViewModel source)
    {
        if (!UdidEquals(Udid, source.Udid))
            throw new ArgumentException("Cannot update a device item from a different UDID.",
                nameof(source));

        var wired = !source.IsWireless && !source.IsMediaCast;
        var incomingName = wired && !source.Ready && string.Equals(source.Name, "iPhone", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : source.Name;
        var changed = UpdateDisplayMetadata(new(incomingName, source.ProductType, source.OsVersion),
            preserveKnown: wired);
        changed |= Set(ref _appearance, source.Appearance, nameof(Appearance));
        changed |= Set(ref _connectionType, source.ConnectionType, nameof(ConnectionType), nameof(CardTransport),
            nameof(IsPairedNetwork), nameof(HasPendingWiredReadiness), nameof(StatusDisplay), nameof(CardStatusDisplay), nameof(CardStatusDetail));
        changed |= Set(ref _status, source.Status, nameof(Status));
        changed |= Set(ref _state, source.State, nameof(State), nameof(Ready), nameof(ModelDisplay),
            nameof(StatusDisplay), nameof(CardStatusDisplay), nameof(CardStatusDetail), nameof(HasPendingWiredReadiness), nameof(IsDisconnected), nameof(HasConnectionError));
        changed |= Set(ref _captureNeedsPreparation, source._captureNeedsPreparation,
            nameof(StatusDisplay), nameof(CardStatusDisplay), nameof(CardStatusDetail), nameof(HasPendingWiredReadiness));
        return changed;
    }

    internal bool UpdateDisplayMetadata(WiredDeviceMetadata metadata, bool preserveKnown = true)
    {
        var changed = false;
        if (!preserveKnown || (!string.IsNullOrWhiteSpace(metadata.Name) && !IsWindowsUsbName(metadata.Name)))
            changed |= Set(ref _name, metadata.Name, nameof(Name), nameof(DisplayName));
        if (!preserveKnown || !string.IsNullOrWhiteSpace(metadata.ProductType))
            changed |= Set(ref _productType, metadata.ProductType, nameof(ProductType), nameof(ModelDisplay),
                nameof(DisplayName), nameof(IsTablet), nameof(CardModel), nameof(CardSystemVersion));
        if (!preserveKnown || !string.IsNullOrWhiteSpace(metadata.OsVersion))
            changed |= Set(ref _osVersion, metadata.OsVersion, nameof(OsVersion), nameof(OsDisplay), nameof(CardSystemVersion));
        return changed;
    }

    internal static bool IsWindowsUsbName(string name) =>
        name.StartsWith("Apple Mobile Device USB", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("iPhone/iPad", StringComparison.OrdinalIgnoreCase);

    internal void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(OsDisplay));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(CardStatusDisplay));
        OnPropertyChanged(nameof(CardStatusDetail));
    }

    internal void SetMirroring(bool active) => Set(ref _isMirroring, active,
        nameof(IsMirroring), nameof(CardStatusDisplay), nameof(CardStatusDetail));

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

    internal static DeviceViewModel FromPairedNetwork(PairedNetworkDevice info, DeviceBindingProfile? profile)
    {
        var metadata = DeviceAppearanceCache.Shared.GetMetadata(info.Udid);
        var fingerprint = profile?.WiredIdentity?.Fingerprint ?? profile?.DeviceFingerprint;
        var device = new DeviceViewModel(info.Udid,
            info.Name ?? metadata?.Name ?? profile?.DisplayName ?? "iPhone",
            info.ProductType ?? metadata?.ProductType ?? fingerprint?.ProductType ?? "",
            info.OsVersion ?? metadata?.OsVersion ?? fingerprint?.SystemVersion ?? "",
            "Network", "", ConnectionState.Connected);
        device._appearance = DeviceAppearanceCache.Shared.Remember(device.Udid, device.ProductType, DeviceAppearance.Unknown);
        return device;
    }

    internal static DeviceViewModel FromNative(NativeDeviceInfo info)
    {
        var device = FromNative(info, DeviceAppearanceCache.Shared.Remember(info.Udid, info.ProductType,
            NativeCore.GetDeviceAppearance(info.Udid)));
        if (device.IsWireless || device.IsMediaCast || string.IsNullOrWhiteSpace(device.Udid)) return device;
        if (info.LockdownAccessible != 0)
            DeviceAppearanceCache.Shared.RememberMetadata(device.Udid,
                new(device.Name, device.ProductType, device.OsVersion));
        else if (DeviceAppearanceCache.Shared.GetMetadata(device.Udid) is { } cached)
            device.UpdateDisplayMetadata(cached);
        return device;
    }

    internal static DeviceViewModel FromInventory(NativeDeviceInfo info, bool captureNeedsPreparation)
    {
        var device = FromNative(info);
        device._captureNeedsPreparation = captureNeedsPreparation;
        return device;
    }

    internal static DeviceViewModel FromNative(NativeDeviceInfo info, DeviceAppearance appearance) => new(
        info.Udid ?? string.Empty,
        info.Name ?? "iPhone",
        info.ProductType ?? string.Empty,
        info.OsVersion ?? string.Empty,
        info.ConnectionType ?? "USB",
        info.Status ?? string.Empty,
        info.State) { _appearance = appearance };

    internal DeviceViewModel AsUsbPresentNoMux() => new(
        Udid, Name, ProductType, OsVersion, ConnectionType, Status,
        ConnectionState.UsbPresentNoMux) { _appearance = _appearance };
}
