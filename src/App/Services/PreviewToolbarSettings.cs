using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPhoneMirror.App.Services;

[JsonConverter(typeof(PreviewToolbarSettingsConverter))]
internal sealed class PreviewToolbarSettings
{
    internal static readonly string[] DefaultItems = ["WirelessControlButton", "UsbControlButton", "BluetoothControlButton",
        "QuickMediaOutputButton", "QuickImageSettingsButton", "QuickPreviewWindowButton", "QuickScreenshotButton", "QuickFullScreenButton"];
    public List<string> Items { get; set; } = DefaultItems.ToList();
    public bool IconsOnly { get; set; }
    public PreviewToolbarSettings Clone() => new() { Items = Items.ToList(), IconsOnly = IconsOnly };
    internal PreviewToolbarSettings Normalize()
    {
        var valid = (Items ?? DefaultItems.ToList()).Where(id => DefaultItems.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        if (Items is { Count: > 0 } && valid.Count == 0) valid = DefaultItems.ToList();
        return new() { Items = valid, IconsOnly = IconsOnly };
    }
}

internal sealed class PreviewToolbarSettingsConverter : JsonConverter<PreviewToolbarSettings>
{
    public override PreviewToolbarSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var value = new PreviewToolbarSettings();
        if (root.ValueKind != JsonValueKind.Object) return value;
        if (root.TryGetProperty("IconsOnly", out var mode) && mode.ValueKind is JsonValueKind.True or JsonValueKind.False)
            value.IconsOnly = mode.GetBoolean();
        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<string>();
            // Read the earlier experimental format once, flattening containers in
            // place. New saves contain only the eight original action IDs.
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var id = item.GetString()!;
                if (id.StartsWith("group:", StringComparison.Ordinal) && root.TryGetProperty("Groups", out var groups) &&
                    groups.ValueKind == JsonValueKind.Object && groups.TryGetProperty(id, out var children) && children.ValueKind == JsonValueKind.Array)
                    ids.AddRange(children.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                else ids.Add(id);
            }
            if (items.GetArrayLength() == 0 || ids.Count > 0) value.Items = ids;
        }
        return value.Normalize();
    }
    public override void Write(Utf8JsonWriter writer, PreviewToolbarSettings value, JsonSerializerOptions options)
    {
        var normalized = value.Normalize();
        writer.WriteStartObject(); writer.WriteBoolean("IconsOnly", normalized.IconsOnly);
        writer.WritePropertyName("Items"); JsonSerializer.Serialize(writer, normalized.Items, options); writer.WriteEndObject();
    }
}
