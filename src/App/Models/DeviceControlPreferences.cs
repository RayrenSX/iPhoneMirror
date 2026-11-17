using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Models;

internal sealed record DeviceControlPreferences(double MouseSensitivity, double WheelSensitivity,
    int PortraitDirection, int LandscapeDirection, bool ReverseHorizontal, bool ReverseVertical)
{
    internal bool HasSameDirection(DeviceControlPreferences other) =>
        PortraitDirection == other.PortraitDirection && LandscapeDirection == other.LandscapeDirection &&
        ReverseHorizontal == other.ReverseHorizontal && ReverseVertical == other.ReverseVertical;

    internal static DeviceControlPreferences FromLegacy(UpdateSettings settings) => new(
        settings.BluetoothMouseSensitivity, settings.BluetoothWheelSensitivity,
        settings.BluetoothPortraitMouseDirection, settings.BluetoothLandscapeMouseDirection,
        settings.BluetoothMouseReverseHorizontal, settings.BluetoothMouseReverseVertical);

    internal DeviceControlPreferences Normalize() => this with
    {
        MouseSensitivity = double.IsFinite(MouseSensitivity) ? Math.Clamp(MouseSensitivity, 10, 1000) : 500,
        WheelSensitivity = double.IsFinite(WheelSensitivity) ? Math.Clamp(WheelSensitivity, 10, 1000) : 1000,
        PortraitDirection = Math.Clamp(PortraitDirection, 0, 3),
        LandscapeDirection = Math.Clamp(LandscapeDirection, 0, 3),
    };
}
