using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.Services;

internal enum DeviceIdentityResolutionState { Resolved, BindingRequired, NotFound, Invalid }
internal enum DeviceIdentityResolutionSource { WiredSession, AirPlaySession, Unresolved }

internal sealed record DeviceIdentityResolution(DeviceBindingProfile? Profile,
    DeviceIdentityType SourceType, string SourceStableId,
    DeviceIdentityResolutionState State)
{
    internal string? AppleUdid => Profile?.WiredIdentity?.Udid;
}

internal sealed record CanonicalDeviceIdentity(string DeviceKey, string? AppleUdid,
    string? AirPlayDeviceId, string? DisplayName,
    DeviceIdentityResolutionSource ResolutionSource)
{
    internal bool IsResolved => !string.IsNullOrWhiteSpace(AppleUdid);
}

internal sealed record ControlDeviceBinding(Guid ProfileId, DeviceIdentityType SourceType,
    string SourceStableId, string TargetStableId)
{
    internal bool Matches(ControlDeviceBinding? other) => other is not null &&
        ProfileId == other.ProfileId && SourceType == other.SourceType &&
        DeviceViewModel.UdidEquals(SourceStableId, other.SourceStableId) &&
        DeviceViewModel.UdidEquals(TargetStableId, other.TargetStableId);
}

/// <summary>Maps a mirror-session identity to a real profile and never guesses.</summary>
internal sealed class DeviceIdentityResolver(DeviceBindingManager bindings)
{
    // All control modes resolve the selected mirror's profile first. USB and
    // network touch both target its Apple UDID; Bluetooth targets its HID peer.
    // Never fall back to another profile or the first connected device.
    internal ControlDeviceBinding? ResolveControlBinding(DeviceViewModel? device, ReverseControlMode mode)
    {
        var resolution = ResolveProfile(device);
        if (resolution.State != DeviceIdentityResolutionState.Resolved ||
            resolution.Profile is not { } profile) return null;
        var targetType = mode == ReverseControlMode.Bluetooth
            ? DeviceIdentityType.Bluetooth : DeviceIdentityType.Wired;
        var target = mode == ReverseControlMode.Bluetooth
            ? profile.BluetoothIdentity?.StableId : profile.WiredIdentity?.Udid;
        if (mode == ReverseControlMode.None || string.IsNullOrWhiteSpace(target) ||
            bindings.FindByIdentity(targetType, target)?.Id != profile.Id) return null;
        return new(profile.Id, resolution.SourceType, resolution.SourceStableId, target);
    }

    internal DeviceIdentityResolution ResolveProfile(DeviceViewModel? device)
    {
        // A wireless AirPlay mirror is also marked IsMediaCast while it is
        // streaming. It still needs to resolve through its AirPlay binding
        // so reverse control can target the paired wired Apple UDID.
        if (device is null || string.IsNullOrWhiteSpace(device.Udid))
            return new(null, DeviceIdentityType.Wired, string.Empty, DeviceIdentityResolutionState.Invalid);
        var type = device.IsWireless ? DeviceIdentityType.AirPlay : DeviceIdentityType.Wired;
        var profile = bindings.FindByIdentity(type, device.Udid);
        return new(profile, type, device.Udid,
            profile is null ? DeviceIdentityResolutionState.BindingRequired : DeviceIdentityResolutionState.Resolved);
    }

    internal CanonicalDeviceIdentity Resolve(DeviceViewModel? device)
    {
        if (device is null) return new(string.Empty, null, null, null, DeviceIdentityResolutionSource.Unresolved);
        var resolution = ResolveProfile(device);
        return new(device.Udid, resolution.AppleUdid,
            device.IsWireless ? device.Udid : null, device.DisplayName,
            device.IsWireless ? DeviceIdentityResolutionSource.AirPlaySession : DeviceIdentityResolutionSource.WiredSession);
    }
}
