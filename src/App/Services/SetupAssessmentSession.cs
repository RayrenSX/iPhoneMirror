using System.Runtime.ExceptionServices;

namespace IPhoneMirror.App.Services;

// Evidence lives only for this wizard session. Reopening or explicitly checking again creates a new session.
internal sealed class SetupAssessmentSession
{
    private sealed record FailedEvidence(ExceptionDispatchInfo Error);
    private readonly Dictionary<string, object> _evidence = new(StringComparer.OrdinalIgnoreCase);
    private readonly SetupCheckRuntime _runtime;

    internal SetupAssessmentSession(SetupCheckRuntime source)
    {
        _runtime = new()
        {
            ReadSettingsAsync = ct => Read("settings", () => source.ReadSettingsAsync(ct), ct),
            ReadBindingsAsync = ct => Read("bindings", () => source.ReadBindingsAsync(ct), ct),
            DriverAsync = ct => Read("driver", () => source.DriverAsync(ct), ct),
            WiredIdentitiesAsync = ct => Read("identities", () => source.WiredIdentitiesAsync(ct), ct),
            ReceiverAsync = (backend, ct) => Read("receiver:" + backend, () => source.ReceiverAsync(backend, ct), ct),
            BridgeAsync = ct => Read("bridge", () => source.BridgeAsync(ct), ct),
            BluetoothAsync = ct => Read("bluetooth", () => source.BluetoothAsync(ct), ct),
            ControlDeviceAsync = (device, wireless, ct) => Read(ControlKey(device) + wireless,
                () => source.ControlDeviceAsync(device, wireless, ct), ct)
        };
    }

    private async Task<T> Read<T>(string key, Func<Task<T>> read, CancellationToken token) where T : notnull
    {
        token.ThrowIfCancellationRequested();
        if (_evidence.TryGetValue(key, out var cached))
        {
            if (cached is FailedEvidence failed) failed.Error.Throw();
            return (T)cached;
        }
        try
        {
            var result = await read(); token.ThrowIfCancellationRequested();
            _evidence[key] = result; return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { _evidence[key] = new FailedEvidence(ExceptionDispatchInfo.Capture(error)); throw; }
    }

    internal Task<IReadOnlyList<SetupCheckResult>> CheckAsync(FirstRunSetupState state,
        IProgress<SetupCheckProgress>? progress, CancellationToken token) => new SetupAssessment(_runtime).CheckAsync(state, progress, token);

    internal Task<IReadOnlyList<SetupCheckResult>> RefreshAsync(FirstRunSetupState state, SetupStep step,
        string? device, CancellationToken token)
    {
        // Re-evaluate the dependency model, but query only evidence affected by the completed step.
        switch (step)
        {
            case SetupStep.Preferences: case SetupStep.ApplicationMode: case SetupStep.Appearance:
            case SetupStep.Usage: case SetupStep.MirrorConnection: case SetupStep.ControlIntroduction:
            case SetupStep.Display: case SetupStep.WirelessDisplay: case SetupStep.WiredDisplay:
            case SetupStep.WiredFrameRate: case SetupStep.WiredDecoder: case SetupStep.Validation:
                Forget("settings"); break;
            case SetupStep.WirelessBackend:
                Forget("settings", "receiver:"); break;
            case SetupStep.Environment: case SetupStep.Connection: case SetupStep.Devices: case SetupStep.ReDetection:
                Forget("driver", "identities", "bindings", "control:"); break;
            case SetupStep.Profile: case SetupStep.Wireless:
                Forget("bindings", device is null ? "control:" : ControlKey(device)); break;
            case SetupStep.Bluetooth:
                Forget("bindings", "bluetooth"); break;
            case SetupStep.WiredControl:
                Forget("bridge", "control:"); break;
            case SetupStep.ControlReadiness: case SetupStep.WirelessControl:
                Forget(device is null ? "control:" : ControlKey(device)); break;
        }
        return CheckAsync(state, null, token);
    }

    private static string ControlKey(string device) => "control:" + IPhoneFilterDriverService.NormalizeSerial(device) + ":";
    private void Forget(params string[] prefixes)
    {
        foreach (var key in _evidence.Keys.Where(key => prefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToArray())
            _evidence.Remove(key);
    }
}
