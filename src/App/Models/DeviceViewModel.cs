using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Models;

/// <summary>
/// A stable, mutable list item keyed by UDID. Device refreshes update this
/// instance in place so WPF never publishes a transient null selection while
/// a second phone is attached or an existing phone changes connection state.
/// </summary>
internal sealed class DeviceViewModel : INotifyPropertyChanged
{
    private string _name;
    private string _productType;
    private string _osVersion;
    private string _connectionType;
    private string _status;
    private ConnectionState _state;

    private DeviceViewModel(
        string udid,
        string name,
        string productType,
        string osVersion,
        string connectionType,
        string status,
        ConnectionState state)
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
    public ConnectionState State => _state;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "iPhone" : Name;
    public string ModelDisplay => string.IsNullOrWhiteSpace(ProductType)
        ? LocalizationService.Get("ModelLoading")
        : ProductType;
    public string OsDisplay => string.IsNullOrWhiteSpace(OsVersion) ? "iOS —" : $"iOS {OsVersion}";
    public string ShortUdid => Udid.Length <= 18 ? Udid : $"{Udid[..8]}…{Udid[^6..]}";
    public bool Ready => State is ConnectionState.Ready;
    public string StatusDisplay => LocalizationService.Get(State switch
    {
        ConnectionState.Disconnected => "ConnectionDisconnected",
        ConnectionState.UsbPresentNoMux => "ConnectionUsbNoMux",
        ConnectionState.Connected => "ConnectionConnected",
        ConnectionState.Paired => "ConnectionPaired",
        ConnectionState.Ready => "ConnectionReady",
        _ => "ConnectionError",
    });

    /// <summary>Updates all volatile fields while retaining list identity.</summary>
    internal bool UpdateFrom(DeviceViewModel source)
    {
        if (!UdidEquals(Udid, source.Udid))
            throw new ArgumentException("Cannot update a device item from a different UDID.", nameof(source));

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
        OnPropertyChanged(nameof(ModelDisplay));
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

    public static DeviceViewModel FromNative(NativeDeviceInfo info) => new(
        info.Udid ?? string.Empty,
        info.Name ?? "iPhone",
        info.ProductType ?? string.Empty,
        info.OsVersion ?? string.Empty,
        info.ConnectionType ?? "USB",
        info.Status ?? string.Empty,
        info.State);
}
