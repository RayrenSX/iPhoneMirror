using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPhoneMirror.App.Services;

internal enum AppProxyMode
{
    Default,
    Direct,
    Manual,
}

[JsonConverter(typeof(AppProxySettingsJsonConverter))]
internal sealed class AppProxySettings
{
    internal const string DefaultAddress = "http://127.0.0.1:7897";

    public AppProxyMode Mode { get; set; } = AppProxyMode.Default;
    public string Address { get; set; } = DefaultAddress;

    internal AppProxySettings Clone() => new() { Mode = Mode, Address = Address };
}

// A malformed proxy preference must not discard unrelated application
// preferences during UpdateSettings deserialization. URI validation remains
// centralized in AppProxyPolicy, including for hand-edited settings files.
internal sealed class AppProxySettingsJsonConverter : JsonConverter<AppProxySettings>
{
    public override AppProxySettings Read(ref Utf8JsonReader reader, Type type,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
        var settings = new AppProxySettings();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals(nameof(AppProxySettings.Mode), StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var number))
                    settings.Mode = (AppProxyMode)number;
                else if (property.Value.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<AppProxyMode>(property.Value.GetString(), ignoreCase: true, out var mode))
                    settings.Mode = mode;
                else return Invalid();
            }
            else if (property.Name.Equals(nameof(AppProxySettings.Address), StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return Invalid();
                settings.Address = property.Value.GetString()!;
            }
        }
        return settings;
    }

    public override void Write(Utf8JsonWriter writer, AppProxySettings value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(nameof(AppProxySettings.Mode), (int)value.Mode);
        writer.WriteString(nameof(AppProxySettings.Address), value.Address);
        writer.WriteEndObject();
    }

    private static AppProxySettings Invalid() => new() { Mode = (AppProxyMode)(-1) };
}
