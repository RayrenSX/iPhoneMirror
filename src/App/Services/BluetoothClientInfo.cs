using IPhoneMirror.App.Localization;
using System.Globalization;

namespace IPhoneMirror.App.Services;

public sealed record BluetoothClientInfo(string Id, string Name, string Address,
    DateTimeOffset ConnectedAt, string? BoundDeviceName = null)
{
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
