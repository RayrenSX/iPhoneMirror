using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal enum ParentDriverAction { Bind, Reenumerate }

public sealed record ParentDriverChoice(string InfPath, string Section, string Description,
    string Provider, ulong Version, long Date)
{
    public string VersionText => $"{Version >> 48}.{(Version >> 32) & 65535}.{(Version >> 16) & 65535}.{Version & 65535}";
    public string DisplayText => $"{Description} — {Provider} — {VersionText} ({Path.GetFileName(InfPath)})";
    internal bool IsComposite => Path.GetFileName(InfPath).Equals("usb.inf", StringComparison.OrdinalIgnoreCase)
        && Section.Equals("Composite.Dev", StringComparison.OrdinalIgnoreCase);
    internal bool SameDriver(ParentDriverChoice other) =>
        InfPath.Equals(other.InfPath, StringComparison.OrdinalIgnoreCase) &&
        Section.Equals(other.Section, StringComparison.OrdinalIgnoreCase) &&
        Provider.Equals(other.Provider, StringComparison.OrdinalIgnoreCase) &&
        Description.Equals(other.Description, StringComparison.OrdinalIgnoreCase) &&
        Version == other.Version && Date == other.Date;
}

// One confirmation authorizes one exact device state and one exact action.
internal sealed record ParentDriverConsent(string InstanceId, string StateFingerprint,
    ParentDriverAction Action, ParentDriverChoice? Driver)
{
    internal static ParentDriverConsent Create(AppleDeviceRecord device,
        ParentDriverAction action, ParentDriverChoice? driver) =>
        new(device.InstanceId, Fingerprint(device), action, driver);

    internal bool Matches(AppleDeviceRecord device) => IsValid() && device.IsPresent &&
        InstanceId.Equals(device.InstanceId, StringComparison.OrdinalIgnoreCase) &&
        StateFingerprint.Equals(Fingerprint(device), StringComparison.Ordinal);

    internal bool IsValid() => DriverConstants.IsAppleMobileCaptureParent(InstanceId) &&
        StateFingerprint is { Length: 64 } && StateFingerprint.All(Uri.IsHexDigit) &&
        Enum.IsDefined(Action) && (Action == ParentDriverAction.Reenumerate ? Driver is null :
            Driver is not null && !string.IsNullOrWhiteSpace(Driver.InfPath) &&
            !string.IsNullOrWhiteSpace(Driver.Section) && Driver.Provider is not null &&
            Driver.Description is not null);

    internal string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    internal static ParentDriverConsent? Decode(string encoded)
    {
        if (encoded.Length > 16384) return null;
        try
        {
            var value = JsonSerializer.Deserialize<ParentDriverConsent>(Convert.FromBase64String(encoded));
            return value?.IsValid() == true ? value : null;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string Fingerprint(AppleDeviceRecord device) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Instance = device.InstanceId.ToUpperInvariant(),
            Serial = device.Serial.ToUpperInvariant(),
            Service = device.Service.ToUpperInvariant(),
            Inf = device.DriverInf.ToUpperInvariant(),
            Section = device.DriverSection.ToUpperInvariant(),
            device.DriverVersion,
            Upper = device.UpperFilters.Select(value => value.ToUpperInvariant()).ToArray(),
            Lower = (device.LowerFilters ?? []).Select(value => value.ToUpperInvariant()).ToArray(),
        }))));
}
