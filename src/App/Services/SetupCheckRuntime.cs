using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Services;

internal sealed record SetupSettingsEvidence(UpdateSettings? Settings, Exception? Error = null);
internal sealed record SetupBluetoothEvidence(SetupState State, string? DetailKey, string[]? PairedIds);
internal sealed class SetupCheckRuntime
{
    internal Func<CancellationToken, Task<SetupSettingsEvidence>> ReadSettingsAsync { get; init; } = token => Task.Run(() =>
    {
        try {
            var path = Path.Combine(UpdateSettingsStore.UserDataDirectory, "settings.json");
            return !File.Exists(path) ? new SetupSettingsEvidence(null) :
                new(JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Empty settings document."));
        }
        catch (Exception e) { return new(null, e); }
    }, token);
    internal Func<CancellationToken, Task<DeviceBindingManager>> ReadBindingsAsync { get; init; } = token =>
        Task.Run(() => new DeviceBindingManager(readOnly: true), token);
    internal Func<CancellationToken, Task<SetupDriverProgress>> DriverAsync { get; init; } = token =>
        FirstRunDriverClient.RunAsync("", false, new Progress<SetupDriverProgress>(), token, diagnose: true);
    internal Func<CancellationToken, Task<string[]>> WiredIdentitiesAsync { get; init; } = _ => Task.FromResult(Array.Empty<string>());
    internal Func<string, bool, CancellationToken, Task<(SetupState, string?, string?)>> ControlDeviceAsync { get; init; } = async (udid, wireless, token) =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--check-setup", "--udid", udid, wireless ? "--wireless" : "--usb" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start device check.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(30), token);
            if (process.ExitCode != 0) return (SetupState.Unknown, "SetupCheckControlHint", await error);
            using var json = JsonDocument.Parse(await output);
            var root = json.RootElement;
            if (!Enum.TryParse<SetupState>(root.GetProperty("state").GetString(), out var state))
                return (SetupState.Unknown, "SetupCheckUnavailable", null);
            if (state == SetupState.Completed && (!root.TryGetProperty("udid", out var identity) ||
                !SetupAssessment.SameDevice(identity.GetString() ?? "", udid)))
                return (SetupState.Invalid, "SetupCheckControlHint", "device_identity_mismatch");
            return (state, state == SetupState.Completed ? null : "SetupCheckControlHint",
                root.TryGetProperty("code", out var code) ? code.GetString() : null);
        }
        finally {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output, error);
        }
    };
    internal Func<WirelessReceiverBackend, CancellationToken, Task<WirelessRuntimeProbeResult>> ReceiverAsync { get; init; } =
        (backend, token) => Task.Run(() => new WirelessReceiverService().ProbeRuntime(backend), token);
    internal Func<CancellationToken, Task<SetupBluetoothEvidence>> BluetoothAsync { get; init; } = async token =>
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token);
        if (adapter is null || !adapter.IsLowEnergySupported) return new(SetupState.Invalid, "BluetoothHidLowEnergyUnsupported", null);
        var radio = await adapter.GetRadioAsync().AsTask(token);
        if (radio is null || radio.State != RadioState.On) return new(SetupState.Unknown, "SetupCheckBluetoothOff", null);
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(token);
        // Peripheral capability flags are unreliable on some working Windows adapters.
        return new(SetupState.Completed, null, paired.Select(d => d.Id).ToArray());
    };
    internal Func<CancellationToken, Task<(SetupState, string?, string?)>> BridgeAsync { get; init; } = async token =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", "iUsbBridge.exe");
        var integrity = await Task.Run(() => (Valid: RuntimeBinaryIntegrity.VerifyUsbTouchBridgeRuntime(path, out var reason), Reason: reason), token);
        if (!integrity.Valid) return (SetupState.Invalid, "SetupCheckBridgeRepair", integrity.Reason);
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("--check-runtime");
        using var process = Process.Start(start) ?? throw new IOException("Could not start runtime probe.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(20), token);
            return (process.ExitCode == 0 ? SetupState.Completed : SetupState.Invalid,
                process.ExitCode == 0 ? null : "SetupCheckBridgeRepair", process.ExitCode == 0 ? null : await error);
        }
        finally {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output, error);
        }
    };
}
