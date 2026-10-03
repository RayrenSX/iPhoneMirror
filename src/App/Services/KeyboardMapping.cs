using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPhoneMirror.App.Services;

internal enum MappedTouchAction { Tap, LongPress, DoubleTap, Swipe, SwipeUp, SwipeDown, SwipeLeft, SwipeRight }

// Scan code + extended flag identify a physical key, independent of layout and
// NumLock. VirtualKey is retained for display, accessibility and shortcut checks.
internal sealed record MappedKey(int VirtualKey, int ScanCode, bool Extended)
{
    internal bool IsModifier => VirtualKey is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5;
    internal bool IsWindows => VirtualKey is 0x5B or 0x5C;
    internal bool SamePhysicalKey(MappedKey other) => ScanCode != 0 && other.ScanCode != 0
        ? ScanCode == other.ScanCode && Extended == other.Extended
        : VirtualKey == other.VirtualKey && Extended == other.Extended;
    internal string? Validate() => IsWindows ? "MappingWindowsKeyUnsupported" :
        VirtualKey is < 0x08 or > 0xFE or 0xE5 or 0xE7 or 0xFF || ScanCode is < 0 or > 0x1FF
            ? "MappingUnsupportedKey" : null;
}

internal sealed record KeyboardMappingEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MappedKey? Key { get; init; }
    public MappedTouchAction Action { get; init; }
    public bool Enabled { get; init; } = true;
    public double X { get; init; } = .5;
    public double Y { get; init; } = .5;
    public double EndX { get; init; } = .5;
    public double EndY { get; init; } = .75;
    public double Distance { get; init; } = .25;
    public int DurationMs { get; init; } = 400;
    public int IntervalMs { get; init; } = 100;

    internal bool IsSwipe => Action >= MappedTouchAction.Swipe;
    internal bool IsDirectional => Action >= MappedTouchAction.SwipeUp;
    internal (double X, double Y) EndPoint => Action switch
    {
        MappedTouchAction.SwipeUp => (X, Y - Distance),
        MappedTouchAction.SwipeDown => (X, Y + Distance),
        MappedTouchAction.SwipeLeft => (X - Distance, Y),
        MappedTouchAction.SwipeRight => (X + Distance, Y),
        _ => (EndX, EndY),
    };

    internal string? Validate()
    {
        if (Key is null) return "MappingCaptureHint";
        if (Key.Validate() is { } keyError) return keyError;
        if (!Enum.IsDefined(Action)) return "MappingUnsupportedAction";
        if (!Coordinate(X) || !Coordinate(Y)) return "MappingInvalidCoordinates";
        if (Action is MappedTouchAction.LongPress || IsSwipe)
            if (DurationMs is < 50 or > 10000) return "MappingInvalidDuration";
        if (Action == MappedTouchAction.DoubleTap && IntervalMs is < 40 or > 1000)
            return "MappingInvalidInterval";
        if (IsDirectional && (!double.IsFinite(Distance) || Distance is <= 0 or > 1))
            return "MappingInvalidDistance";
        if (IsSwipe)
        {
            var end = EndPoint;
            if (!Coordinate(end.X) || !Coordinate(end.Y) || (end.X == X && end.Y == Y))
                return "MappingInvalidSwipe";
        }
        return null;
    }

    private static bool Coordinate(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}

[JsonConverter(typeof(KeyboardMappingSettingsConverter))]
internal sealed class KeyboardMappingSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool SuppressOriginalKey { get; set; }
    public List<KeyboardMappingEntry> Mappings { get; set; } = [];
    [JsonIgnore] public bool HadInvalidEntries { get; set; }

    internal KeyboardMappingSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion, Enabled = Enabled,
        SuppressOriginalKey = SuppressOriginalKey, Mappings = [.. Mappings],
        HadInvalidEntries = HadInvalidEntries,
    };
}

// A damaged mapping must not cause UpdateSettingsStore to discard unrelated
// settings. Read each entry separately and keep mapping capture disabled until
// the user has reviewed any rejected data.
internal sealed class KeyboardMappingSettingsConverter : JsonConverter<KeyboardMappingSettings>
{
    public override bool HandleNull => true;
    public override KeyboardMappingSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = new KeyboardMappingSettings();
        var rejectedReasons = new HashSet<string>();
        var root = document.RootElement;
        try
        {
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(nameof(result.SchemaVersion), StringComparison.OrdinalIgnoreCase))
                    result.SchemaVersion = property.Value.GetInt32();
                else if (property.Name.Equals(nameof(result.Enabled), StringComparison.OrdinalIgnoreCase))
                    result.Enabled = property.Value.GetBoolean();
                else if (property.Name.Equals(nameof(result.SuppressOriginalKey), StringComparison.OrdinalIgnoreCase))
                    result.SuppressOriginalKey = property.Value.GetBoolean();
                else if (property.Name.Equals(nameof(result.Mappings), StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        try
                        {
                            var entry = item.Deserialize<KeyboardMappingEntry>(options);
                            if (entry?.Validate() is { } reason) rejectedReasons.Add(reason);
                            if (entry is null || entry.Id == Guid.Empty || entry.Validate() is not null ||
                                result.Mappings.Count >= 256 || result.Mappings.Any(existing =>
                                    existing.Id == entry.Id || existing.Key!.SamePhysicalKey(entry.Key!)))
                                result.HadInvalidEntries = true;
                            else result.Mappings.Add(entry);
                        }
                        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                        { result.HadInvalidEntries = true; }
                    }
                }
            }
            if (result.SchemaVersion != 1)
            {
                result.HadInvalidEntries = true;
                // Retained entries have been validated against this schema.
                // A user's subsequent review/save must not persist an unknown
                // version and disable the feature again on every restart.
                result.SchemaVersion = 1;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { result.HadInvalidEntries = true; }
        if (result.HadInvalidEntries)
        {
            result.Enabled = false;
            DiagnosticLogger.Warning("keyboard_mapping", "invalid_mapping_configuration");
            // Aggregate validation categories once per load, never once per
            // malformed entry, so damaged settings cannot flood diagnostics.
            foreach (var reason in rejectedReasons)
                DiagnosticLogger.Warning("keyboard_mapping", reason switch
                {
                    "MappingUnsupportedAction" => "unsupported_action",
                    "MappingUnsupportedKey" or "MappingWindowsKeyUnsupported" => "unsupported_key",
                    _ => "invalid_mapping",
                }, ("reason", reason));
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, KeyboardMappingSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber(nameof(value.SchemaVersion), value.SchemaVersion);
        writer.WriteBoolean(nameof(value.Enabled), value.Enabled);
        writer.WriteBoolean(nameof(value.SuppressOriginalKey), value.SuppressOriginalKey);
        writer.WritePropertyName(nameof(value.Mappings));
        JsonSerializer.Serialize(writer, value.Mappings, options);
        writer.WriteEndObject();
    }
}
