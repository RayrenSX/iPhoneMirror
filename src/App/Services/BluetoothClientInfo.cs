using IPhoneMirror.App.Localization;
using System.Globalization;

namespace IPhoneMirror.App.Services;

public sealed record BluetoothClientInfo(string Id, string Name, string Address,
    DateTimeOffset ConnectedAt, string? BoundDeviceName = null) : System.ComponentModel.INotifyPropertyChanged
{
    // UI event subscriptions are not device data and must not change Selector identity.
    public bool Equals(BluetoothClientInfo? other) => other is not null &&
        Id == other.Id && Name == other.Name && Address == other.Address &&
        ConnectedAt == other.ConnectedAt && BoundDeviceName == other.BoundDeviceName;
    public override int GetHashCode() => HashCode.Combine(Id, Name, Address, ConnectedAt, BoundDeviceName);
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void NotifyLanguageChanged() => PropertyChanged?.Invoke(this, new(null));
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? LocalizationService.Get("BluetoothClientUnknownName") : Name;
    public string IdentifierText => string.IsNullOrWhiteSpace(Address)
        ? Id : LocalizationService.Format("BluetoothClientAddressFormat", Address);
    public string ConnectionTimeText => LocalizationService.Format(
        "BluetoothClientConnectionTimeFormat",
        ConnectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",
            CultureInfo.CurrentCulture));
    public bool IsBound => !string.IsNullOrWhiteSpace(BoundDeviceName);
    public bool CanBind => !IsBound;
    public string BindingText => IsBound
        ? LocalizationService.Format("BluetoothClientBoundToFormat", BoundDeviceName!)
        : string.Empty;
}
