using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPhoneMirror.App.Services;

internal enum MappedTouchAction { Tap, LongPress, DoubleTap, Swipe, SwipeUp, SwipeDown, SwipeLeft, SwipeRight, HoldUntilRelease, Joystick, RelativeDrag, CycleTargets, ReleasePointer }

// Scan code + extended flag identify a physical key, independent of layout and
// NumLock. VirtualKey is retained for display, accessibility and shortcut checks.
internal sealed record MappedKey(int VirtualKey, int ScanCode, bool Extended)
{
    internal bool IsModifier => VirtualKey is 0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5;
    internal bool IsWindows => VirtualKey is 0x5B or 0x5C;
    internal bool SamePhysicalKey(MappedKey other) => ScanCode != 0 && other.ScanCode != 0
        ? ScanCode == other.ScanCode && Extended == other.Extended
        : VirtualKey == other.VirtualKey && Extended == other.Extended;
    internal string? Validate() =>
        VirtualKey is < 0x08 or > 0xFE or 0xE5 or 0xE7 or 0xFF || ScanCode is < 0 or > 0x1FF
            ? "MappingUnsupportedKey" : null;
}

internal sealed record KeyboardMappingEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MappedKey? Key { get; init; }
    public MappedTouchAction Action { get; init; }
    public bool Enabled { get; init; } = true;
    public string Name { get; init; } = "";
    public MappingInputKind InputKind { get; init; }
    public int MouseButton { get; init; } = 1;
    public uint Modifiers { get; init; }
    public MappingJoystick Joystick { get; init; } = new();
    public MappingRelativeDrag RelativeDrag { get; init; } = new();
    public MappingPoint[] Targets { get; init; } = [];
    public bool ReleasePointerAfter { get; init; }
    [JsonIgnore] public bool IsContinuous => Action is MappedTouchAction.Joystick or MappedTouchAction.RelativeDrag;
    [JsonIgnore] public bool NeedsPosition => Action != MappedTouchAction.ReleasePointer;
    [JsonIgnore] public bool IsWheel => InputKind is MappingInputKind.WheelUp or MappingInputKind.WheelDown;
    [JsonIgnore] public IEnumerable<MappedKey> InputKeys => InputKind != MappingInputKind.Keyboard ? [] :
        Action == MappedTouchAction.Joystick ? new[] { Key, Joystick.Down, Joystick.Left, Joystick.Right }.OfType<MappedKey>() :
        Key is null ? [] : [Key];
    internal bool MatchesKey(MappedKey key, uint modifiers) => InputKind == MappingInputKind.Keyboard &&
        (Action == MappedTouchAction.Joystick ? modifiers == 0 && InputKeys.Any(k => k.SamePhysicalKey(key)) :
            Key?.SamePhysicalKey(key) == true && Modifiers == modifiers);
    internal bool ConflictsWith(KeyboardMappingEntry other) => InputKind == other.InputKind &&
        (InputKind == MappingInputKind.Keyboard ?
            InputKeys.Any(k => other.InputKeys.Any(k.SamePhysicalKey)) &&
            (Action == MappedTouchAction.Joystick || other.Action == MappedTouchAction.Joystick || Modifiers == other.Modifiers) :
            Modifiers == other.Modifiers && (InputKind != MappingInputKind.MouseButton || MouseButton == other.MouseButton));
    internal KeyboardMappingEntry Copy() => this with { Targets = [.. Targets] };
    internal bool ContentEquals(KeyboardMappingEntry other) =>
        (this with { Targets = Array.Empty<MappingPoint>() }) == (other with { Targets = Array.Empty<MappingPoint>() }) && Targets.SequenceEqual(other.Targets);
    internal string? ValidateInput()
    {
        if (Joystick is null) return "MappingInvalidParameters";
        if (!Enum.IsDefined(InputKind) || (Modifiers & ~15u) != 0) return "MappingUnsupportedKey";
        if (InputKind == MappingInputKind.Keyboard)
        {
            if (Key is null) return "MappingCaptureHint";
            if (Key.Validate() is { } error) return error;
        }
        if (InputKind == MappingInputKind.MouseButton && MouseButton is not (1 or 2 or 4 or 8 or 16)) return "MappingUnsupportedKey";
        if (IsWheel && Action is MappedTouchAction.HoldUntilRelease or MappedTouchAction.Joystick or MappedTouchAction.RelativeDrag)
            return "MappingPulseNeedsTimedAction";
        if (Action == MappedTouchAction.Joystick)
        {
            if (InputKind != MappingInputKind.Keyboard || Modifiers != 0 || InputKeys.Count() != 4)
                return "MappingFourKeysRequired";
            var keys = InputKeys.ToArray();
            if (keys.Any(k => k.Validate() is not null) || keys.Where((key, i) => keys.Take(i).Any(key.SamePhysicalKey)).Any())
                return "MappingFourKeysRequired";
        }
        return null;
    }
    public double X { get; init; } = .5;
    public double Y { get; init; } = .5;
    public double EndX { get; init; } = .5;
    public double EndY { get; init; } = .75;
    public double Distance { get; init; } = .25;
    public int DurationMs { get; init; } = 400;
    public int IntervalMs { get; init; } = 100;
    // New visual picks store normalized device space; legacy entries retain
    // their original preview-space interpretation until explicitly repicked.
    public bool DeviceCoordinates { get; init; }

    internal bool IsSwipe => Action is >= MappedTouchAction.Swipe and <= MappedTouchAction.SwipeRight;
    internal bool IsDirectional => Action is >= MappedTouchAction.SwipeUp and <= MappedTouchAction.SwipeRight;
    internal (double X, double Y) EndPoint => DeviceCoordinates ? (EndX, EndY) : Action switch
    {
        MappedTouchAction.SwipeUp => (X, Y - Distance),
        MappedTouchAction.SwipeDown => (X, Y + Distance),
        MappedTouchAction.SwipeLeft => (X - Distance, Y),
        MappedTouchAction.SwipeRight => (X + Distance, Y),
        _ => (EndX, EndY),
    };

    internal string? Validate()
    {
        if (Joystick is null || RelativeDrag is null || Targets is null) return "MappingInvalidParameters";
        if (ValidateInput() is { } inputError) return inputError;
        if (!Enum.IsDefined(Action)) return "MappingUnsupportedAction";
        if (Name is null || Name.Length > 80 || Joystick is null || RelativeDrag is null || Targets is null) return "MappingInvalidParameters";
        if (IsContinuous && ReleasePointerAfter) return "MappingInvalidParameters";
        if (Action == MappedTouchAction.ReleasePointer) return null;
        if (Action == MappedTouchAction.Joystick && Joystick.Validate() is { } joystickError) return joystickError;
        if (Action == MappedTouchAction.RelativeDrag && RelativeDrag.Validate() is { } dragError) return dragError;
        if (Action == MappedTouchAction.CycleTargets && (Targets.Length is < 1 or > 31 ||
            Targets.Any(p => p is null || !Coordinate(p.X) || !Coordinate(p.Y)))) return "MappingCycleTargetsRequired";
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

internal enum MappingInputKind { Keyboard, MouseButton, WheelUp, WheelDown }
internal sealed record MappingPoint(double X, double Y);
internal sealed record MappingJoystick
{
    public MappedKey? Down { get; init; }
    public MappedKey? Left { get; init; }
    public MappedKey? Right { get; init; }
    public double Radius { get; init; } = .1;
    public int StartupMs { get; init; }
    public int TurnMs { get; init; }
    public bool OppositeNeutral { get; init; } = true;
    internal string? Validate() => !double.IsFinite(Radius) || Radius is < .01 or > .45 ||
        StartupMs is < 0 or > 500 || TurnMs is < 0 or > 500 ? "MappingInvalidParameters" : null;
}
internal sealed record MappingRelativeDrag
{
    public double Radius { get; init; } = .18;
    public double SensitivityX { get; init; } = .65;
    public double SensitivityY { get; init; } = .65;
    public bool InvertX { get; init; }
    public bool InvertY { get; init; }
    public bool Toggle { get; init; }
    public bool Recenter { get; init; } = true;
    internal string? Validate() => !double.IsFinite(Radius) || Radius is < .01 or > .45 ||
        !double.IsFinite(SensitivityX) || !double.IsFinite(SensitivityY) ||
        SensitivityX is < .01 or > 5 || SensitivityY is < .01 or > 5 ? "MappingInvalidParameters" : null;
}
internal sealed class KeyboardMappingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool ModifiersAsButtons { get; set; }
    public List<KeyboardMappingEntry> Mappings { get; set; } = [];
    internal KeyboardMappingProfile Clone() => new() { Id = Id, Name = Name,
        ModifiersAsButtons = ModifiersAsButtons, Mappings = Mappings.Select(m => m.Copy()).ToList() };
}

[JsonConverter(typeof(KeyboardMappingSettingsConverter))]
internal sealed class KeyboardMappingSettings
{
    public const int CurrentVersion = 2;
    public int SchemaVersion { get; set; } = CurrentVersion;
    public bool Enabled { get; set; }
    public bool SuppressOriginalKey { get; set; }
    public List<KeyboardMappingProfile> Profiles { get; set; } = [new()];
    public Guid SelectedProfileId { get; set; }
    [JsonIgnore] public KeyboardMappingProfile Selected => Profiles.FirstOrDefault(p => p.Id == SelectedProfileId) ?? Profiles[0];
    [JsonIgnore] public List<KeyboardMappingEntry> Mappings { get => Selected.Mappings; set => Selected.Mappings = value; }
    [JsonIgnore] public bool HadInvalidEntries { get; set; }
    internal KeyboardMappingSettings Clone() => new()
    {
        SchemaVersion = SchemaVersion, Enabled = Enabled, SuppressOriginalKey = SuppressOriginalKey,
        Profiles = Profiles.Select(p => p.Clone()).ToList(), SelectedProfileId = Selected.Id, HadInvalidEntries = HadInvalidEntries,
    };
    internal string? Validate()
    {
        if (Profiles is null || Profiles.Count is < 1 or > 32 || Profiles.Any(p => p is null || p.Id == Guid.Empty ||
            p.Name is null || p.Name.Length > 80 || p.Mappings is null) || Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count)
            return "MappingInvalidProfiles";
        foreach (var profile in Profiles)
        {
            if (profile.Mappings.Count > 256) return "MappingLimit";
            for (var i = 0; i < profile.Mappings.Count; i++)
            {
                var entry = profile.Mappings[i];
                if (entry is null || entry.Id == Guid.Empty) return "MappingInvalidParameters";
                if (entry.Validate() is { } error) return error;
                if (profile.Mappings.Take(i).Any(m => m.Id == entry.Id || m.ConflictsWith(entry))) return "MappingDuplicate";
            }
        }
        return null;
    }
}

// Parse each entry separately; damaged mappings never reset unrelated settings.
internal sealed class KeyboardMappingSettingsConverter : JsonConverter<KeyboardMappingSettings>
{
    public override bool HandleNull => true;
    public override KeyboardMappingSettings Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var result = new KeyboardMappingSettings();
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            JsonElement Property(string name) => root.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            var version = Property("SchemaVersion");
            if (version.ValueKind != JsonValueKind.Undefined && version.GetInt32() is not (1 or KeyboardMappingSettings.CurrentVersion))
                result.HadInvalidEntries = true;
            if (Property("Enabled") is { ValueKind: not JsonValueKind.Undefined } enabled) result.Enabled = enabled.GetBoolean();
            if (Property("SuppressOriginalKey") is { ValueKind: not JsonValueKind.Undefined } suppress) result.SuppressOriginalKey = suppress.GetBoolean();
            List<KeyboardMappingEntry> ReadEntries(JsonElement array)
            {
                var entries = new List<KeyboardMappingEntry>();
                foreach (var item in array.EnumerateArray())
                {
                    try
                    {
                        var entry = item.Deserialize<KeyboardMappingEntry>(options);
                        if (entry is null || entry.Id == Guid.Empty || entry.Validate() is not null || entries.Count >= 256 ||
                            entries.Any(m => m.Id == entry.Id || m.ConflictsWith(entry))) result.HadInvalidEntries = true;
                        else entries.Add(entry);
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException)
                    { result.HadInvalidEntries = true; }
                }
                return entries;
            }
            var profiles = Property("Profiles");
            if (profiles.ValueKind != JsonValueKind.Undefined)
            {
                result.Profiles.Clear();
                foreach (var item in profiles.EnumerateArray())
                {
                    try
                    {
                        var profile = new KeyboardMappingProfile { Id = item.GetProperty("Id").GetGuid(), Name = item.GetProperty("Name").GetString() ?? "" };
                        if (item.TryGetProperty("ModifiersAsButtons", out var modifiers)) profile.ModifiersAsButtons = modifiers.GetBoolean();
                        profile.Mappings = ReadEntries(item.GetProperty("Mappings"));
                        if (profile.Id == Guid.Empty || profile.Name.Length > 80 || result.Profiles.Count >= 32 || result.Profiles.Any(p => p.Id == profile.Id))
                            result.HadInvalidEntries = true;
                        else result.Profiles.Add(profile);
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
                    { result.HadInvalidEntries = true; }
                }
                if (Property("SelectedProfileId") is { ValueKind: not JsonValueKind.Undefined } selected) result.SelectedProfileId = selected.GetGuid();
                if (result.Profiles.Count == 0) { result.Profiles.Add(new()); result.HadInvalidEntries = true; }
                if (!result.Profiles.Any(p => p.Id == result.SelectedProfileId))
                { result.SelectedProfileId = result.Profiles[0].Id; result.HadInvalidEntries = true; }
            }
            else if (Property("Mappings") is { ValueKind: not JsonValueKind.Undefined } legacy)
                result.Mappings = ReadEntries(legacy);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { result.HadInvalidEntries = true; }
        if (result.Profiles.Count == 0) result.Profiles.Add(new());
        if (result.HadInvalidEntries)
        {
            result.Enabled = false;
            DiagnosticLogger.Warning("keyboard_mapping", "invalid_mapping_configuration");
        }
        return result;
    }
    public override void Write(Utf8JsonWriter writer, KeyboardMappingSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", KeyboardMappingSettings.CurrentVersion);
        writer.WriteBoolean("Enabled", value.Enabled);
        writer.WriteBoolean("SuppressOriginalKey", value.SuppressOriginalKey);
        writer.WriteString("SelectedProfileId", value.Selected.Id);
        writer.WritePropertyName("Profiles"); JsonSerializer.Serialize(writer, value.Profiles, options);
        writer.WriteEndObject();
    }
}
