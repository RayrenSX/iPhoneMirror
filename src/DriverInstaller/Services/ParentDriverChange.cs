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
            if (readCurrent() is not { } after ||
                !after.InstanceId.Equals(consent.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                !BindingMatches(after, consent.Driver) || !after.IsHealthy)
                throw new InvalidOperationException("The selected parent driver did not become healthy or the binding did not match.");
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
                    if (readCurrent() is { } restored &&
                        restored.InstanceId.Equals(consent.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                        restored.Service.Equals(initial!.Service, StringComparison.OrdinalIgnoreCase) &&
                        BindingMatches(restored, previous))
                        return new(false, false, "ParentChangeRolledBack");
                }
                catch (Exception rollbackError) { log(rollbackError.ToString()); }
            }
            return new(false, false, "ParentChangeRecoveryNeeded");
        }
    }

    internal static bool BindingMatches(AppleDeviceRecord device, ParentDriverChoice choice) =>
        device.IsPresent && Path.GetFileName(device.DriverInf).Equals(Path.GetFileName(choice.InfPath),
            StringComparison.OrdinalIgnoreCase) &&
        SectionMatches(device.DriverSection, choice.Section) &&
        device.DriverVersion.Equals(choice.VersionText, StringComparison.OrdinalIgnoreCase) &&
        (!choice.IsComposite || device.IsCaptureParent);

    private static bool SectionMatches(string installed, string selected) =>
        installed.Equals(selected, StringComparison.OrdinalIgnoreCase) ||
        installed.StartsWith(selected, StringComparison.OrdinalIgnoreCase) &&
        System.Text.RegularExpressions.Regex.IsMatch(installed[selected.Length..],
            @"^\.NT(?:amd64|x86|arm64)?(?:\.\d+)*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
