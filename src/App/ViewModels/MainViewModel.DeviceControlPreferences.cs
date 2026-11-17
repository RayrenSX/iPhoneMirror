using System.Collections.Concurrent;
using IPhoneMirror.App.Services;
using IPhoneMirror.App.Models;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private readonly Dictionary<string, DeviceControlPreferences> _controlDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceControlPreferences _defaultControlPreferences;
    // Input guards also run on transport workers. Publish immutable values
    // only after persistence succeeds; never expose the staged settings file.
    private readonly ConcurrentDictionary<string, DeviceControlPreferences> _appliedDeviceControlPreferences =
        new(StringComparer.OrdinalIgnoreCase);

    internal DeviceControlPreferences ControlPreferencesFor(string? udid) =>
        udid is not null && _appliedDeviceControlPreferences.TryGetValue(udid, out var applied)
            ? applied : _defaultControlPreferences;

    private DeviceControlPreferences CurrentControlDraft() => new(_bluetoothMouseSensitivity,
        _bluetoothWheelSensitivity, (int)_bluetoothPortraitMouseDirection, (int)_bluetoothLandscapeMouseDirection,
        _bluetoothMouseReverseHorizontal, _bluetoothMouseReverseVertical);

    private void RestoreDeviceControlPreferences(string? previous, string? next)
    {
        if (previous is not null) _controlDrafts[previous] = CurrentControlDraft();
        var saved = ControlPreferencesFor(next);
        var draft = next is not null && _controlDrafts.TryGetValue(next, out var pending) ? pending : saved;
        _bluetoothMouseSensitivity = draft.MouseSensitivity;
        _bluetoothWheelSensitivity = draft.WheelSensitivity;
        _bluetoothPortraitMouseDirection = (BluetoothMouseDirection)draft.PortraitDirection;
        _bluetoothLandscapeMouseDirection = (BluetoothMouseDirection)draft.LandscapeDirection;
        _bluetoothMouseReverseHorizontal = draft.ReverseHorizontal;
        _bluetoothMouseReverseVertical = draft.ReverseVertical;
        _appliedBluetoothMouseSensitivity = saved.MouseSensitivity;
        _appliedBluetoothWheelSensitivity = saved.WheelSensitivity;
        _appliedBluetoothPortraitMouseDirection = (BluetoothMouseDirection)saved.PortraitDirection;
        _appliedBluetoothLandscapeMouseDirection = (BluetoothMouseDirection)saved.LandscapeDirection;
        _appliedBluetoothMouseReverseHorizontal = saved.ReverseHorizontal;
        _appliedBluetoothMouseReverseVertical = saved.ReverseVertical;
        foreach (var property in new[] { nameof(BluetoothMouseSensitivity), nameof(BluetoothWheelSensitivity),
            nameof(SelectedBluetoothPortraitMouseDirection), nameof(SelectedBluetoothLandscapeMouseDirection),
            nameof(BluetoothMouseReverseHorizontal), nameof(BluetoothMouseReverseVertical) }) OnPropertyChanged(property);
        ClearMouseSettingsStatus();
        MouseDirectionSettingsStatus = BluetoothSensitivitySettingsStatus = string.Empty;
        NotifyMouseSettingsPending();
    }
}
