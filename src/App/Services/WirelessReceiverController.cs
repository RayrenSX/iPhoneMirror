using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal readonly record struct WirelessReceiverStartResult(
    bool Started, string? Error, bool IsNewError);

internal sealed class WirelessReceiverController(
    NativeCore core, WirelessReceiverService? receiver = null)
{
    private readonly WirelessReceiverService _receiver = receiver ?? new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private DateTime _automaticStartNotBeforeUtc;

    internal string ReceiverName { get; set; } = WirelessReceiverConfiguration.DefaultReceiverName;
    internal WirelessDisplayProfile SelectedProfile { get; set; } =
        WirelessReceiverConfiguration.DefaultDisplayProfile;
    internal string AppliedReceiverName { get; private set; } =
        WirelessReceiverConfiguration.DefaultReceiverName;
    internal WirelessDisplayProfile AppliedProfile { get; private set; } =
        WirelessReceiverConfiguration.DefaultDisplayProfile;
    internal bool Running { get; private set; }
    internal bool Ready { get; private set; }
    internal string? StartError { get; private set; }
    internal bool IsAvailable => _receiver.IsAvailable;

    internal async Task<WirelessReceiverStartResult> EnsureStartedAsync(
        string? receiverName = null, WirelessDisplayProfile? displayProfile = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsAvailable)
            {
                Running = Ready = false;
                StartError = null;
                return new(false, null, false);
            }

            var automaticStart = receiverName is null && displayProfile is null;
            if (automaticStart && DateTime.UtcNow < _automaticStartNotBeforeUtc)
            {
                Running = Ready = false;
                return new(false, null, false);
            }

            var status = core.GetWirelessReceiverStatus();
            Running = status.Running;
            Ready = status.Ready;
            if (Running)
            {
                StartError = null;
                return new(true, null, false);
            }

            var hostPath = _receiver.ExecutablePath;
            if (hostPath is null) return new(false, null, false);
            var sanitized = WirelessReceiverConfiguration.SanitizeReceiverName(
                receiverName ?? AppliedReceiverName);
            if (receiverName is not null) ReceiverName = sanitized;
            var profile = displayProfile ?? AppliedProfile;
            var started = await Task.Run(() => core.StartWirelessReceiver(sanitized, hostPath,
                profile.Width, profile.Height, profile.FrameRate));
            Running = started.Success;
            Ready = false;
            if (started.Success)
            {
                if (!automaticStart)
                {
                    AppliedReceiverName = sanitized;
                    AppliedProfile = profile;
                }
                StartError = null;
                return new(true, null, false);
            }

            var isNewError = !string.Equals(StartError, started.Message, StringComparison.Ordinal);
            StartError = started.Message;
            return new(false, started.Message, isNewError);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task StopAsync(TimeSpan automaticStartDelay = default)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await Task.Run(core.StopWirelessReceiver);
        }
        finally
        {
            _automaticStartNotBeforeUtc = DateTime.UtcNow + automaticStartDelay;
            Running = Ready = false;
            StartError = null;
            _lifecycleGate.Release();
        }
    }

    internal string GetStatusText()
    {
        if (!IsAvailable) return LocalizationService.Get("WirelessReceiverMissing");
        if (!Running && !string.IsNullOrWhiteSpace(StartError))
            return LocalizationService.Format("StartFailedFormat", StartError);
        if (!Running || !Ready) return LocalizationService.Get("WirelessStarting");
        return LocalizationService.Format("WirelessRunningFormat", AppliedReceiverName);
    }
}
