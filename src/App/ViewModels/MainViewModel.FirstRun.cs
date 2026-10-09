using IPhoneMirror.App.Models;
using IPhoneMirror.App.Services;

namespace IPhoneMirror.App.ViewModels;

internal sealed partial class MainViewModel
{
    private void PersistDeviceVideoPreferences(DeviceCaptureState state)
    {
        if (System.Windows.Application.Current is not App app) return;
        app.UpdateSettings.DeviceVideoPreferences ??= new(StringComparer.OrdinalIgnoreCase);
        var hadPrevious = app.UpdateSettings.DeviceVideoPreferences.TryGetValue(state.Udid, out var previous);
        app.UpdateSettings.DeviceVideoPreferences[state.Udid] = DeviceVideoPreferences.From(state);
        if (!app.SaveUpdateSettings())
        {
            if (hadPrevious) app.UpdateSettings.DeviceVideoPreferences[state.Udid] = previous!;
            else app.UpdateSettings.DeviceVideoPreferences.Remove(state.Udid);
            SetSettingsStatus("SetupSaveFailed");
        }
    }
    internal bool SetupSaveVideoPreferences(IEnumerable<string> deviceIds)
    {
        if (System.Windows.Application.Current is not App app) return false;
        app.UpdateSettings.DeviceVideoPreferences ??= new(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, DeviceVideoPreferences>(app.UpdateSettings.DeviceVideoPreferences, StringComparer.OrdinalIgnoreCase);
        foreach (var id in deviceIds)
        {
            if (!_sessions.TryGet(id, out var state) && Devices.FirstOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, id)) is { } device)
                state = GetOrCreateDeviceState(device);
            if (state is not null) app.UpdateSettings.DeviceVideoPreferences[id] = DeviceVideoPreferences.From(state);
        }
        if (app.SaveUpdateSettings()) return true;
        app.UpdateSettings.DeviceVideoPreferences = previous;
        return false;
    }
    internal bool SetupActive { get; set; }
    internal bool SetupSaveWirelessPreferences() => PersistWirelessReceiverSettings(SelectedWirelessReceiverBackend.Backend,
        SelectedWirelessDisplayProfile, WirelessReceiverName);
    internal async Task<bool> SetupApplyWirelessPreferencesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SetupSaveWirelessPreferences()) throw new System.IO.IOException(Localization.LocalizationService.Get("SetupSaveFailed"));
        await _coreGate.WaitAsync(token);
        try
        {
            var backend = SelectedWirelessReceiverBackend.Backend;
            var profile = SelectedWirelessDisplayProfile;
            var changed = _wireless.AppliedBackend != backend || _wireless.AppliedProfile != profile ||
                _wireless.AppliedReceiverName != WirelessReceiverName;
            _wireless.Backend = backend;
            if (!_wireless.Running || !changed) return false;
            await _wireless.StopAsync();
            token.ThrowIfCancellationRequested();
            var result = await _wireless.EnsureStartedAsync(WirelessReceiverName, profile, backend);
            token.ThrowIfCancellationRequested();
            RefreshWirelessStatus();
            if (!result.Started) throw new System.IO.IOException(Localization.LocalizationService.Get("SetupAirPlayFailed"));
            return true;
        }
        finally { _coreGate.Release(); }
    }
    internal async Task<IReadOnlyList<DeviceViewModel>> SetupScanAsync(bool wireless, CancellationToken token)
    {
        await _coreGate.WaitAsync(token);
        try
        {
            var native = await Task.Run(() => wireless ? _core.GetWirelessDevices() : _core.GetDevices(true));
            token.ThrowIfCancellationRequested();
            var found = native.Select(DeviceViewModel.FromNative).Where(d => !d.IsMediaCast).ToArray();
            foreach (var old in Devices.Where(d => d.IsWireless == wireless && !d.IsMediaCast).ToArray())
                if (!found.Any(d => DeviceViewModel.UdidEquals(d.Udid, old.Udid))) Devices.Remove(old);
            foreach (var device in found)
            {
                var existing = Devices.FirstOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, device.Udid));
                if (existing is null) Devices.Add(device); else existing.UpdateFrom(device);
            }
            return Devices.Where(d => d.IsWireless == wireless && !d.IsMediaCast).ToArray();
        }
        finally { _coreGate.Release(); }
    }
    internal async Task<bool> SetupReceiverAsync(CancellationToken token)
    {
        await _coreGate.WaitAsync(token);
        try
        {
            var backend = SelectedWirelessReceiverBackend.Backend;
            var profile = SelectedWirelessDisplayProfile;
            // EnsureStarted intentionally keeps a running receiver unchanged.
            // Apply edited preferences using the same restart as normal settings.
            if (_wireless.Running && (_wireless.AppliedBackend != backend ||
                _wireless.AppliedProfile != profile || _wireless.AppliedReceiverName != WirelessReceiverName))
                await _wireless.StopAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            do
            {
                token.ThrowIfCancellationRequested();
                var result = await _wireless.EnsureStartedAsync(WirelessReceiverName, profile, backend);
                token.ThrowIfCancellationRequested();
                RefreshWirelessStatus();
                if (!result.Started) return false;
                if (_wireless.Ready) break;
                // Starting the process is not a ready signal. Poll its actual
                // receiver status while the normal refresh loop is suspended.
                await Task.Delay(250, token);
            } while (DateTime.UtcNow < deadline);
            if (!_wireless.Ready) return false;
            if (!SetupSaveWirelessPreferences()) throw new System.IO.IOException(Localization.LocalizationService.Get("SetupSaveFailed"));
            return true;
        }
        finally { _coreGate.Release(); }
    }
    internal void SetupSelect(string udid)
    {
        SelectedDevice = Devices.FirstOrDefault(d => DeviceViewModel.UdidEquals(d.Udid, udid));
        if (SelectedDevice is not null) _ = GetOrCreateDeviceState(SelectedDevice);
    }
}
