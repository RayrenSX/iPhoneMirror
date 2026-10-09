using System.IO;
using System.Text.Json;
using IPhoneMirror.App.Updater;

namespace IPhoneMirror.App.Services;

internal enum SetupState { Completed, Incomplete, Invalid, NotRequired, Unknown }
internal sealed record SetupCheckResult(string Id, string NameKey, SetupState State, SetupStep Step,
    string? DeviceId = null, string? DetailKey = null, string? Detail = null, int Priority = 0,
    string[]? Dependencies = null)
{
    internal bool NeedsAttention => State is SetupState.Incomplete or SetupState.Invalid or SetupState.Unknown;
    internal DateTimeOffset CheckedAt { get; } = DateTimeOffset.UtcNow;
    internal bool IsRequired => State != SetupState.NotRequired;
}
internal interface ISetupCheck
{
    string Id { get; }
    Task<SetupCheckResult> CheckAsync(CancellationToken cancellationToken);
}
internal sealed class SetupCheck(string id, Func<CancellationToken, Task<SetupCheckResult>> check) : ISetupCheck
{
    public string Id => id;
    public Task<SetupCheckResult> CheckAsync(CancellationToken cancellationToken) => check(cancellationToken);
}
internal sealed record SetupCheckProgress(string Id, string NameKey, SetupCheckResult? Result);
internal sealed record SetupTask(SetupStep Step, string? DeviceId, IReadOnlyList<SetupCheckResult> Checks)
{
    internal string Key => $"{Step}:{DeviceId}";
}
internal static class SetupTaskPlanner
{
    internal static IReadOnlyList<SetupTask> Plan(IEnumerable<SetupCheckResult> results, ISet<string>? deferred = null)
    {
        var checks = results.ToArray();
        // A failed prerequisite owns the next action. Recheck its dependants after repair.
        return checks.Where(r => r.NeedsAttention && (r.Dependencies ?? []).All(id =>
                checks.FirstOrDefault(p => p.Id == id)?.State == SetupState.Completed))
            .OrderBy(r => r.Priority).GroupBy(r => (r.Step, r.DeviceId))
            .Select(g => new SetupTask(g.Key.Step, g.Key.DeviceId, g.ToArray()))
            .Where(t => deferred?.Contains(t.Key) != true).ToArray();
    }
}

// A new assessment reads fresh evidence. Checkpoints only describe intent and prior success.
internal sealed class SetupAssessment
{
    private readonly SetupCheckRuntime _runtime;
    internal SetupAssessment(SetupCheckRuntime runtime) => _runtime = runtime;
    internal async Task<IReadOnlyList<SetupCheckResult>> CheckAsync(FirstRunSetupState state,
        IProgress<SetupCheckProgress>? progress, CancellationToken token)
    {
        var results = new List<SetupCheckResult>();
        async Task Add(string id, string name, SetupStep step, int priority,
            Func<CancellationToken, Task<(SetupState State, string? Key, string? Detail)>> probe,
            string? device = null, string[]? dependencies = null)
        {
            var check = new SetupCheck(id, async ct =>
            {
                try { var r = await probe(ct); return new(id, name, r.State, step, device, r.Key, r.Detail, priority, dependencies); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { return new(id, name, SetupState.Unknown, step, device, "SetupCheckUnavailable", error.Message, priority, dependencies); }
            });
            token.ThrowIfCancellationRequested();
            progress?.Report(new(id, name, null));
            var result = await check.CheckAsync(token);
            token.ThrowIfCancellationRequested(); results.Add(result); progress?.Report(new(id, name, result));
        }
        Task<(SetupState, string?, string?)> Value(SetupState value, string? key = null, string? detail = null) => Task.FromResult((value, key, detail));
        SetupState Missing(bool previous) => previous ? SetupState.Invalid : SetupState.Incomplete;
        bool Was(SetupStep step, SetupDevice? d = null) => (d?.Outcomes ?? state.GlobalOutcomes).GetValueOrDefault(step) == SetupOutcome.Verified;

        var settings = await _runtime.ReadSettingsAsync(token);
        foreach (var step in new[] { SetupStep.Preferences, SetupStep.ApplicationMode, SetupStep.Appearance })
        {
            var valid = settings.Settings is { } s && step switch {
                SetupStep.Preferences => new[] { "system", "zh-CN", "zh-TW", "zh-HK", "en-US" }.Contains(s.Language),
                SetupStep.ApplicationMode => Enum.IsDefined(s.ApplicationDisplayMode),
                _ => Enum.IsDefined(s.Theme) };
            await Add(step.ToString(), "SetupCheck" + step, step, 0, _ => Value(settings.Error is not null
                ? settings.Error is UnauthorizedAccessException or IOException ? SetupState.Unknown : SetupState.Invalid
                : valid ? SetupState.Completed : Missing(Was(step)), detail: settings.Error?.Message));
        }
        var usageKnown = Was(SetupStep.Usage) && (state.Usage == SetupUsage.MirrorAndControl || Was(SetupStep.MirrorConnection));
        await Add("usage", "SetupCheckUsage", SetupStep.Usage, 1, _ => Value(usageKnown ? SetupState.Completed : SetupState.Incomplete));
        if (!usageKnown) return results;

        var control = state.Usage == SetupUsage.MirrorAndControl;
        var wired = state.Usage != SetupUsage.WirelessOnly;
        var wireless = state.Usage == SetupUsage.WirelessOnly || control && state.WirelessEnabled != false;
        var bluetooth = control && state.BluetoothEnabled != false;
        var usbControl = control && state.UsbControlEnabled != false;
        if (control)
        {
            await Add("features", "SetupTitleControlIntroduction", SetupStep.ControlIntroduction, 2,
                _ => Value(state.WirelessEnabled.HasValue && state.BluetoothEnabled.HasValue && state.UsbControlEnabled.HasValue
                    ? SetupState.Completed : SetupState.Incomplete));
            if (results[^1].State != SetupState.Completed) return results;
        }
        var bindings = await _runtime.ReadBindingsAsync(token);
        var targets = state.Devices.ToList();
        // Recover identities from the real store, including users who configured outside the wizard.
        if (targets.Count == 0 && bindings.LoadError is null)
            targets.AddRange(bindings.Profiles.Select(p => (Profile: p, Id: wired ? p.WiredIdentity?.Udid : p.AirPlayIdentity?.StableId))
                .Where(p => !string.IsNullOrWhiteSpace(p.Id)).Select(p => new SetupDevice { Id = p.Id!, Name = p.Profile.DisplayName }));
        SetupDriverProgress? driver = null; Exception? driverError = null;
        await Add("apple", "SetupCheckApple", SetupStep.Environment, 10, async ct =>
        {
            if (!wired) return (SetupState.NotRequired, null, null);
            try { driver = await _runtime.DriverAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { driverError = e; return (SetupState.Unknown, "SetupCheckUnavailable", e.Message); }
            return driver.Success && driver.Environment is { } env
                ? (env.AppleReady ? SetupState.Completed : Missing(targets.Any(d => Was(SetupStep.Environment, d))), null, env.AppleDetail)
                : (SetupState.Unknown, "SetupCheckUnavailable", driver.Detail);
        }, targets.FirstOrDefault()?.Id);
        await Add("usb", "SetupCheckUsb", SetupStep.Environment, 11, _ => !wired ? Value(SetupState.NotRequired) :
            driver?.Environment is { } env ? Value(env.UsbInstalled && env.UsbFilesMatch ? SetupState.Completed :
                Missing(targets.Any(d => Was(SetupStep.Environment, d))), detail: env.UsbDetail) : Value(SetupState.Unknown, "SetupCheckUnavailable", driverError?.Message), targets.FirstOrDefault()?.Id);
        await Add("receiver", "SetupCheckReceiver", SetupStep.WirelessBackend, 12, async ct =>
        {
            if (!wireless) return (SetupState.NotRequired, null, null);
            var backend = settings.Settings?.WirelessReceiverBackend ?? state.WirelessBackendDraft ?? WirelessReceiverBackend.Original;
            if (!Enum.IsDefined(backend)) return (SetupState.Invalid, "SetupCheckUnavailable", null);
            var result = await _runtime.ReceiverAsync(backend, ct);
            return (result.Success ? SetupState.Completed : result.Status == WirelessRuntimeProbeStatus.TimedOut ? SetupState.Unknown :
                Missing(Was(SetupStep.Wireless) || targets.Any(d => Was(SetupStep.Wireless, d))), null,
                result.Success ? null : WirelessReceiverService.DescribeProbeFailure(result));
        });
        await Add("bridge", "SetupCheckBridge", SetupStep.WiredControl, 13, async ct =>
            !(usbControl || control && wireless) ? (SetupState.NotRequired, null, null) : await _runtime.BridgeAsync(ct));
        SetupBluetoothEvidence? bt = null;
        await Add("bluetooth", "SetupCheckBluetooth", SetupStep.Bluetooth, 14, async ct =>
        {
            if (!bluetooth) return (SetupState.NotRequired, null, null);
            bt = await _runtime.BluetoothAsync(ct);
            return (bt.State, bt.DetailKey, null);
        }, targets.FirstOrDefault()?.Id);

        string[]? identities = null;
        if (wired && driver?.Environment is { AppleReady: true, UsbInstalled: true, UsbFilesMatch: true } && driver.Devices?.Length > 0)
        {
            try { identities = await _runtime.WiredIdentitiesAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) { driverError = e; }
        }
        if (targets.Count > 0 && driver?.Devices?.Any(d => !targets.Any(t => SameDevice(t.Id, d.Serial))) == true)
            await Add("newdevices", "SetupCheckNewDevices", SetupStep.Connection, 21, _ => Value(SetupState.Incomplete));

        if (targets.Count == 0)
            await Add("device", "SetupCheckDevice", wired ? SetupStep.Connection : SetupStep.Wireless, 20,
                _ => Value(bindings.LoadError is null ? SetupState.Incomplete : SetupState.Invalid,
                    bindings.LoadError is null ? "SetupNoDeviceConfigured" : "SetupCheckBindingsUnreadable", bindings.LoadError?.Message),
                dependencies: wired ? ["apple", "usb"] : ["receiver"]);
        foreach (var d in targets)
        {
            var profile = bindings.FindByIdentity(wired ? DeviceIdentityType.Wired : DeviceIdentityType.AirPlay, d.Id);
            var profileId = "profile:" + d.Id;
            await Add(profileId, "SetupCheckBinding", wired ? SetupStep.Profile : SetupStep.Wireless, 30,
                _ => Value(bindings.LoadError is not null ? SetupState.Invalid : profile is null ? Missing(Was(SetupStep.Profile, d) || Was(SetupStep.Wireless)) : SetupState.Completed,
                    bindings.LoadError is not null ? "SetupCheckBindingsUnreadable" : null, bindings.LoadError?.Message), d.Id,
                wired ? ["apple", "usb", "device:" + d.Id] : ["receiver"]);
            if (wired)
            {
                var found = driver?.Devices?.FirstOrDefault(v => SameDevice(v.Serial, d.Id));
                await Add("device:" + d.Id, "SetupCheckDevice", SetupStep.ReDetection, 22, _ =>
                    found is null ? Value(SetupState.Unknown, "SetupCheckOffline") :
                    !found.Healthy || !found.FilterInstalled || driver?.Environment?.UsbRunning != true ? Value(SetupState.Invalid, "SetupCheckDeviceDriver") :
                    identities is null ? Value(SetupState.Unknown, "SetupCheckUnavailable", driverError?.Message) :
                    identities.Any(id => SameDevice(id, d.Id)) ? Value(SetupState.Completed) : Value(SetupState.Unknown, "SetupCheckUnlock"), d.Id, ["apple", "usb"]);
                // A present but unhealthy device is repaired using the existing environment page.
                if (found is not null && (!found.Healthy || !found.FilterInstalled || driver?.Environment?.UsbRunning != true))
                    results[^1] = results[^1] with { Step = SetupStep.Environment };
            }
            if (wired && wireless)
                await Add("airplay:" + d.Id, "SetupCheckAirPlayBinding", SetupStep.Wireless, 40,
                    _ => Value(profile?.AirPlayIdentity is { StableId.Length: > 0 } ? SetupState.Completed : Missing(Was(SetupStep.Wireless, d))), d.Id, [profileId, "receiver"]);
            if (bluetooth)
                await Add("btbinding:" + d.Id, "SetupCheckBluetoothBinding", SetupStep.Bluetooth, 50, _ =>
                    profile?.BluetoothIdentity is not { StableId.Length: > 0 } identity ? Value(Missing(Was(SetupStep.Bluetooth, d))) :
                    bt?.PairedIds is null ? Value(SetupState.Unknown, "SetupCheckUnavailable") :
                    Value(bt.PairedIds.Contains(identity.StableId, StringComparer.OrdinalIgnoreCase) ? SetupState.Completed : SetupState.Invalid,
                        "SetupCheckPairing"), d.Id, [profileId, "bluetooth"]);
            if (usbControl || control && wireless)
                await Add("control:" + d.Id, "SetupTitleControlReadiness", SetupStep.ControlReadiness, 60,
                    async ct => results.FirstOrDefault(r => r.Id == "bridge")?.State != SetupState.Completed ||
                        results.FirstOrDefault(r => r.Id == "device:" + d.Id)?.State != SetupState.Completed
                        ? (SetupState.Unknown, "SetupCheckOffline", null) : await _runtime.ControlDeviceAsync(d.Id, wireless, ct),
                    d.Id, [profileId, "bridge", "device:" + d.Id]);
        }
        await Add("display", "SetupCheckDisplay", SetupStep.Display, 100, _ => Value(Was(SetupStep.Display)
            && settings.Settings is { } display && (!wireless || WirelessReceiverConfiguration.DisplayProfiles.Any(p => p.Id == display.WirelessDisplayProfileId))
            && (!wired || display.DeviceVideoPreferences is not null && targets.All(d => !display.DeviceVideoPreferences.TryGetValue(d.Id, out var value) || value is { IsValid: true }))
                ? SetupState.Completed : Missing(Was(SetupStep.Display))),
            dependencies: targets.Count == 0 ? ["device"] : targets.Select(d => "profile:" + d.Id).ToArray());
        return results;
    }
    internal static bool SameDevice(string a, string b) => string.Equals(IPhoneFilterDriverService.NormalizeSerial(a),
        IPhoneFilterDriverService.NormalizeSerial(b), StringComparison.OrdinalIgnoreCase);
}
