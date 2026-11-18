using System.IO;
using System.Text.Json;
using IPhoneMirror.App.Models;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Services;

// Populated during device enumeration, before capture changes the USB interface.
// Rendering never queries USB or writes this cache.
internal sealed class DeviceAppearanceCache
{
    internal sealed record Entry(string ProductType, DeviceAppearance Appearance);
    internal static DeviceAppearanceCache Shared { get; } = new(Path.Combine(UpdateSettingsStore.UserDataDirectory, "device-appearance.json"));
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;
    private bool _dirty;
    internal DeviceAppearanceCache(string path) => _path = path;

    internal DeviceAppearance ForProjection(DeviceViewModel device, DeviceBindingManager bindings)
    {
        var appearance = device.Appearance;
        if (!device.IsWireless || string.IsNullOrWhiteSpace(device.ProductType) ||
            !string.IsNullOrWhiteSpace(appearance.EnclosureColor) ||
            !string.IsNullOrWhiteSpace(appearance.HousingColor)) return appearance;
        var wiredId = bindings.FindByIdentity(DeviceIdentityType.AirPlay, device.Udid)?.WiredIdentity?.Udid;
        if (string.IsNullOrWhiteSpace(wiredId)) return appearance;

        lock (_gate)
        {
            return ReadEntries().TryGetValue(CacheKey(wiredId), out var saved) &&
                saved is { Appearance: { } savedAppearance } && saved.ProductType == device.ProductType
                ? savedAppearance : appearance;
        }
    }

    internal DeviceAppearance Remember(string? identity, string? model, DeviceAppearance current)
    {
        if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(model) ||
            DeviceViewModel.IsWirelessUdid(identity) || DeviceViewModel.IsMediaCastUdid(identity)) return current;
        lock (_gate)
        {
            var entries = ReadEntries();
            var key = CacheKey(identity);
            entries.TryGetValue(key, out var saved);
            if (saved?.ProductType != model || saved.Appearance is null) saved = null;
            var hasHousingRead = !string.IsNullOrWhiteSpace(current.EnclosureColor) || !string.IsNullOrWhiteSpace(current.HousingColor);
            var merged = new DeviceAppearance(
                string.IsNullOrWhiteSpace(current.FrontColor) ? saved?.Appearance.FrontColor ?? "" : current.FrontColor,
                hasHousingRead ? current.EnclosureColor : saved?.Appearance.EnclosureColor ?? "",
                hasHousingRead ? current.HousingColor : saved?.Appearance.HousingColor ?? "");
            if (string.IsNullOrWhiteSpace(merged.EnclosureColor) && string.IsNullOrWhiteSpace(merged.HousingColor)) return current;
            var entry = new Entry(model, merged);
            if (entry != saved)
            {
                entries[key] = entry;
                _dirty = true;
            }
            if (_dirty)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                    File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(entries));
                    File.Move(_path + ".tmp", _path, overwrite: true);
                    _dirty = false;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLogger.Exception("devices", "appearance_cache_save_failed", error);
                }
            }
            return merged;
        }
    }

    private Dictionary<string, Entry> ReadEntries()
    {
        if (_entries is null)
        {
            try { _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)); }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return _entries ??= new();
    }

    private static string CacheKey(string identity) => identity.Replace("-", "").ToLowerInvariant();
}
