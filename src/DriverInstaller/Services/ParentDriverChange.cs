using IPhoneMirror.DriverInstaller.Models;

namespace IPhoneMirror.DriverInstaller.Services;

internal sealed record ParentDriverChangeResult(bool Success, bool RequiresRestart, string Message);

internal static class ParentDriverChange
{
    // Dependencies make failure-after-mutation and stale-confirmation paths testable
    // without installing a driver on the development machine.
    internal static ParentDriverChangeResult Apply(ParentDriverConsent consent,
        ParentDriverChoice? previous, Func<AppleDeviceRecord?> readCurrent,
        Func<ParentDriverChoice, bool> install, Action saveBackup,
        Action<string> log, Action? checkpoint = null)
    {
        var started = false;
        AppleDeviceRecord? initial = null;
        try
        {
            if (consent.Action != ParentDriverAction.Bind || consent.Driver is null)
                throw new InvalidOperationException("A driver selection is required.");
            saveBackup();
            if (readCurrent() is not { } current || !consent.Matches(current))
                throw new InvalidOperationException("The device or parent driver changed after confirmation. Refresh and confirm again.");
            initial = current;
            checkpoint?.Invoke();
            started = true;
            var reboot = install(consent.Driver);
            if (reboot) return new(true, true, "ParentBindingRestartRequired");
            checkpoint?.Invoke();
            var after = readCurrent();
            if (after is null ||
                !after.InstanceId.Equals(consent.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                !BindingMatches(after, consent.Driver) || !after.IsHealthy)
                throw new InvalidOperationException(
                    $"Parent binding verification failed. Present={after?.IsPresent}, started={after?.IsStarted}, " +
                    $"problem={after?.ProblemCode}, service={after?.Service}, INF={after?.DriverInf}, " +
                    $"section={after?.DriverSection}, version={after?.DriverVersion}.");
            return new(true, false, "ParentBindingComplete");
        }
        catch (Exception error)
        {
            log(error.ToString());
            if (!started) return new(false, false, "ParentChangeRejected");
            if (previous is not null)
            {
                try
                {
                    var reboot = install(previous);
                    if (reboot) return new(false, true, "ParentRollbackRestartRequired");
                    if (readCurrent() is { } restored && WasRestored(restored, initial!, previous))
                        return new(false, false, "ParentChangeRolledBack");
                    log("Rollback verification failed: the original binding, filters or device health could not be confirmed.");
                }
                catch (Exception rollbackError) { log(rollbackError.ToString()); }
            }
            return new(false, false, "ParentChangeRecoveryNeeded");
        }
    }

    internal static bool BindingMatches(AppleDeviceRecord device, ParentDriverChoice choice) =>
        device.IsPresent && Path.GetFileName(device.DriverInf).Equals(Path.GetFileName(choice.InfPath),
            StringComparison.OrdinalIgnoreCase) &&
        ParentDriverChoice.SectionMatches(device.DriverSection, choice.Section) &&
        device.DriverVersion.Equals(choice.VersionText, StringComparison.OrdinalIgnoreCase) &&
        (!choice.IsCaptureDriver || device.IsCaptureParent);

    internal static bool WasRestored(AppleDeviceRecord restored, AppleDeviceRecord original,
        ParentDriverChoice choice) =>
        restored.InstanceId.Equals(original.InstanceId, StringComparison.OrdinalIgnoreCase) &&
        restored.Service.Equals(original.Service, StringComparison.OrdinalIgnoreCase) &&
        BindingMatches(restored, choice) && (!original.IsHealthy || restored.IsHealthy) &&
        restored.UpperFilters.SequenceEqual(original.UpperFilters, StringComparer.OrdinalIgnoreCase) &&
        (restored.LowerFilters ?? []).SequenceEqual(original.LowerFilters ?? [], StringComparer.OrdinalIgnoreCase);
}
