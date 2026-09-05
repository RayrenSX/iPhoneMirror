using System.Text.Json;
using System.IO;

namespace IPhoneMirror.App.Services;

internal enum ReverseControlMode { None, Bluetooth, Wireless, Usb }
internal enum DeviceIdentityType { Wired, AirPlay, Bluetooth }
internal enum DeviceBindingCompatibility { Confirmed, Compatible, Incompatible, Unknown }

internal sealed record DeviceFingerprint(string? ProductType = null, string? ProductName = null,
    string? ModelIdentifier = null, string? DeviceClass = null, string? SystemVersion = null);
internal sealed record WiredDeviceIdentity(string Udid, string? DeviceName,
    DeviceFingerprint? Fingerprint, DateTime BoundAt);
internal sealed record AirPlayDeviceIdentity(string StableId, string? DeviceName,
    DeviceFingerprint? Fingerprint, DateTime BoundAt);
internal sealed record BluetoothDeviceIdentity(string StableId, string? DeviceName, DateTime BoundAt);
internal sealed record DeviceBindingProfile(Guid Id, string DisplayName,
    DeviceFingerprint? DeviceFingerprint, WiredDeviceIdentity? WiredIdentity,
    AirPlayDeviceIdentity? AirPlayIdentity, BluetoothDeviceIdentity? BluetoothIdentity,
    DateTime CreatedAt, DateTime UpdatedAt);
internal sealed record BindIdentityResult(bool Success, DeviceBindingCompatibility Compatibility,
    string? Error = null);
internal sealed record CreateProfileResult(bool Success, DeviceBindingProfile? Profile,
    string? Error = null);

internal sealed class DeviceBindingManager
{
    internal static DeviceBindingManager Shared { get; } = new();
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<Guid, DeviceBindingProfile> _profiles = [];

    internal DeviceBindingManager(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "iPhoneMirror", "device-binding-profiles.json");
        Load();
    }

    internal IReadOnlyList<DeviceBindingProfile> Profiles
    { get { lock (_gate) return _profiles.Values.OrderBy(profile => profile.DisplayName).ToArray(); } }

    /// <summary>Creates a usable profile from its first observed identity.
    /// Empty profiles are invalid because they cannot represent a real device.</summary>
    internal CreateProfileResult CreateProfileFromIdentity(string displayName,
        DeviceIdentityType type, string stableId, DeviceFingerprint? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(stableId))
            return new(false, null, "设备身份不能为空。");
        lock (_gate)
        {
            if (_profiles.Values.Any(profile => Matches(profile, type, stableId)))
                return new(false, null, "该设备身份已经绑定到设备档案。");
            var now = DateTime.UtcNow;
            var profile = type switch
            {
                DeviceIdentityType.Wired => new DeviceBindingProfile(Guid.NewGuid(),
                    NormalizeDisplayName(displayName), fingerprint,
                    new WiredDeviceIdentity(stableId, displayName, fingerprint, now),
                    null, null, now, now),
                DeviceIdentityType.AirPlay => new DeviceBindingProfile(Guid.NewGuid(),
                    NormalizeDisplayName(displayName), fingerprint, null,
                    new AirPlayDeviceIdentity(stableId, displayName, fingerprint, now),
                    null, now, now),
                _ => new DeviceBindingProfile(Guid.NewGuid(), NormalizeDisplayName(displayName),
                    null, null, null,
                    new BluetoothDeviceIdentity(stableId, displayName, now), now, now),
            };
            _profiles.Add(profile.Id, profile);
            Persist();
            return new(true, profile);
        }
    }

    internal DeviceBindingProfile? FindById(Guid id)
    { lock (_gate) return _profiles.GetValueOrDefault(id); }

    internal bool RenameProfile(Guid id, string displayName)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(id, out var profile) || string.IsNullOrWhiteSpace(displayName)) return false;
            _profiles[id] = profile with { DisplayName = displayName.Trim(), UpdatedAt = DateTime.UtcNow }; Persist(); return true;
        }
    }

    internal bool DeleteProfile(Guid id)
    { lock (_gate) { if (!_profiles.Remove(id)) return false; Persist(); return true; } }

    internal DeviceBindingProfile? FindByIdentity(DeviceIdentityType type, string stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId)) return null;
        lock (_gate) return _profiles.Values.FirstOrDefault(profile => Matches(profile, type, stableId));
    }

    internal DeviceBindingCompatibility ValidateCompatibility(Guid profileId, DeviceFingerprint? candidate)
    {
        lock (_gate) return _profiles.TryGetValue(profileId, out var profile)
            ? ValidateCompatibilityUnsafe(profile, candidate) : DeviceBindingCompatibility.Unknown;
    }

    internal BindIdentityResult Bind(Guid profileId, DeviceIdentityType type, string stableId,
        string? deviceName, DeviceFingerprint? fingerprint, bool userConfirmed = false)
    {
        if (string.IsNullOrWhiteSpace(stableId)) return new(false, DeviceBindingCompatibility.Unknown, "设备身份不能为空。");
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var profile)) return new(false, DeviceBindingCompatibility.Unknown, "设备档案不存在。");
            var owner = _profiles.Values.FirstOrDefault(candidate => candidate.Id != profileId && Matches(candidate, type, stableId));
            if (owner is not null) return new(false, DeviceBindingCompatibility.Unknown, "该设备身份已绑定到另一台设备档案。");
            if (Matches(profile, type, stableId))
                return new(true, DeviceBindingCompatibility.Confirmed);
            var compatibility = ValidateCompatibilityUnsafe(profile, fingerprint);
            if (compatibility == DeviceBindingCompatibility.Incompatible) return new(false, compatibility, "检测到的设备型号不一致，无法绑定。");
            if (compatibility is DeviceBindingCompatibility.Compatible or DeviceBindingCompatibility.Unknown && !userConfirmed)
                return new(false, compatibility, compatibility == DeviceBindingCompatibility.Compatible ? "设备型号一致，需要用户确认。" : "无法自动验证设备型号，需要用户确认。");
            var now = DateTime.UtcNow;
            var updated = type switch
            {
                DeviceIdentityType.Wired => profile with { WiredIdentity = new(stableId, deviceName, fingerprint, now), DeviceFingerprint = profile.DeviceFingerprint ?? fingerprint, UpdatedAt = now },
                DeviceIdentityType.AirPlay => profile with { AirPlayIdentity = new(stableId, deviceName, fingerprint, now), DeviceFingerprint = profile.DeviceFingerprint ?? fingerprint, UpdatedAt = now },
                _ => profile with { BluetoothIdentity = new(stableId, deviceName, now), UpdatedAt = now }
            };
            _profiles[profileId] = updated; Persist(); return new(true, compatibility);
        }
    }

    internal bool Unbind(Guid profileId, DeviceIdentityType type)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profileId, out var profile)) return false;
            _profiles[profileId] = type switch
            {
                DeviceIdentityType.Wired => profile with { WiredIdentity = null, UpdatedAt = DateTime.UtcNow },
                DeviceIdentityType.AirPlay => profile with { AirPlayIdentity = null, UpdatedAt = DateTime.UtcNow },
                _ => profile with { BluetoothIdentity = null, UpdatedAt = DateTime.UtcNow }
            };
            Persist(); return true;
        }
    }

    internal bool UnbindBluetoothByStableId(string stableId)
    {
        var profile = FindByIdentity(DeviceIdentityType.Bluetooth, stableId);
        return profile is not null && Unbind(profile.Id, DeviceIdentityType.Bluetooth);
    }

    internal int ClearBluetoothBindings()
    {
        Guid[] profileIds;
        lock (_gate)
            profileIds = _profiles.Values.Where(profile => profile.BluetoothIdentity is not null)
                .Select(profile => profile.Id).ToArray();
        foreach (var profileId in profileIds) Unbind(profileId, DeviceIdentityType.Bluetooth);
        return profileIds.Length;
    }

    private static bool Matches(DeviceBindingProfile profile, DeviceIdentityType type, string id) => type switch
    {
        DeviceIdentityType.Wired => string.Equals(profile.WiredIdentity?.Udid, id, StringComparison.OrdinalIgnoreCase),
        DeviceIdentityType.AirPlay => string.Equals(profile.AirPlayIdentity?.StableId, id, StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(profile.BluetoothIdentity?.StableId, id, StringComparison.OrdinalIgnoreCase)
    };

    private static DeviceBindingCompatibility ValidateCompatibilityUnsafe(DeviceBindingProfile profile, DeviceFingerprint? candidate)
    {
        if (profile.DeviceFingerprint is null || candidate is null) return DeviceBindingCompatibility.Unknown;
        var left = profile.DeviceFingerprint.ProductType ?? profile.DeviceFingerprint.ModelIdentifier;
        var right = candidate.ProductType ?? candidate.ModelIdentifier;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return DeviceBindingCompatibility.Unknown;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? DeviceBindingCompatibility.Compatible : DeviceBindingCompatibility.Incompatible;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var profiles = JsonSerializer.Deserialize<List<DeviceBindingProfile>>(
                File.ReadAllText(_path)) ?? [];
            var removedEmptyProfiles = false;
            foreach (var profile in profiles)
            {
                if (profile.WiredIdentity is null && profile.AirPlayIdentity is null &&
                    profile.BluetoothIdentity is null)
                {
                    removedEmptyProfiles = true;
                    continue;
                }
                _profiles[profile.Id] = profile;
            }
            if (removedEmptyProfiles) Persist();
        }
        catch (Exception error) { DiagnosticLogger.Exception("binding", "profile_load_failed", error); }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_profiles.Values,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (Exception error) { DiagnosticLogger.Exception("binding", "profile_save_failed", error); }
    }

    private static string NormalizeDisplayName(string displayName) =>
        string.IsNullOrWhiteSpace(displayName) ? "未命名设备" : displayName.Trim();
}

internal sealed class ReverseControlManager
{
    internal DeviceBindingManager Bindings { get; }
    internal ReverseControlMode ActiveMode { get; private set; }
    internal string? ActiveMirrorDeviceId { get; private set; }
    internal ReverseControlManager(DeviceBindingManager bindings) => Bindings = bindings;
    internal bool Activate(string mirrorDeviceId, ReverseControlMode mode)
    {
        if (Bindings.FindByIdentity(DeviceIdentityType.Wired, mirrorDeviceId) is null &&
            Bindings.FindByIdentity(DeviceIdentityType.AirPlay, mirrorDeviceId) is null) return false;
        ActiveMirrorDeviceId = mirrorDeviceId; ActiveMode = mode; return true;
    }
    internal void Deactivate() { ActiveMode = ReverseControlMode.None; ActiveMirrorDeviceId = null; }
    internal bool IsTarget(string mirrorDeviceId) => string.Equals(ActiveMirrorDeviceId, mirrorDeviceId, StringComparison.OrdinalIgnoreCase);
}
