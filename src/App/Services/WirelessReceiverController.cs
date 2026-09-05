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
    private static readonly TimeSpan UxPlayStartupGracePeriod =
        TimeSpan.FromSeconds(15);
    private DateTime _automaticStartNotBeforeUtc;
    private DateTime _lastReceiverStartUtc;
    private WirelessReceiverBackend _lastStartedBackend =
        WirelessReceiverBackend.Original;
    private bool _automaticRestartBlocked;
    private WirelessReceiverBackend _backend = WirelessReceiverBackend.Original;

    internal string ReceiverName { get; set; } = WirelessReceiverConfiguration.DefaultReceiverName;
    internal WirelessDisplayProfile SelectedProfile { get; set; } =
        WirelessReceiverConfiguration.DefaultDisplayProfile;
    internal WirelessReceiverBackend Backend
    {
        get => _backend;
        set => _backend = WirelessReceiverConfiguration.NormalizeBackend(value);
    }
    internal string AppliedReceiverName { get; private set; } =
        WirelessReceiverConfiguration.DefaultReceiverName;
    internal WirelessDisplayProfile AppliedProfile { get; private set; } =
        WirelessReceiverConfiguration.DefaultDisplayProfile;
    internal WirelessReceiverBackend AppliedBackend { get; private set; } =
        WirelessReceiverBackend.Original;
    internal bool Running { get; private set; }
    internal bool Ready { get; private set; }
    internal string? StartError { get; private set; }
    internal bool IsAvailable => _receiver.IsBackendAvailable(Backend);
    internal bool IsBackendAvailable(WirelessReceiverBackend backend) =>
        _receiver.IsBackendAvailable(backend);

    internal async Task<WirelessReceiverStartResult> EnsureStartedAsync(
        string? receiverName = null, WirelessDisplayProfile? displayProfile = null,
        WirelessReceiverBackend? backend = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var selectedBackend = WirelessReceiverConfiguration.NormalizeBackend(
                backend ?? Backend);
            Backend = selectedBackend;
            if (!_receiver.IsBackendAvailable(selectedBackend))
            {
                Running = Ready = false;
                StartError = null;
                return new(false, null, false);
            }

            var automaticStart = receiverName is null && displayProfile is null &&
                backend is null;
            if (!automaticStart) _automaticRestartBlocked = false;
            var status = core.GetWirelessReceiverStatus();
            Running = status.Running;
            Ready = status.Ready;
            if (!Running && automaticStart && !_automaticRestartBlocked &&
                selectedBackend == WirelessReceiverBackend.UxPlay &&
                _lastStartedBackend == WirelessReceiverBackend.UxPlay &&
                _lastReceiverStartUtc != default &&
                DateTime.UtcNow - _lastReceiverStartUtc < UxPlayStartupGracePeriod)
            {
                // A missing GStreamer plugin or invalid UxPlay pipeline exits
                // just after the IPC host reports Ready. Do not restart that
                // broken child every refresh tick and make AirPlay discovery
                // flap; a manual Apply is the explicit retry path.
                _automaticRestartBlocked = true;
                StartError = LocalizationService.Get("WirelessUxPlayStartupFailed");
                return new(false, StartError, true);
            }
            if (automaticStart && _automaticRestartBlocked)
            {
                Running = Ready = false;
                return new(false, StartError, false);
            }
            if (automaticStart && DateTime.UtcNow < _automaticStartNotBeforeUtc)
            {
                Running = Ready = false;
                return new(false, null, false);
            }

            if (Running)
            {
                StartError = null;
                return new(true, null, false);
            }

            var hostPath = _receiver.GetExecutablePath(selectedBackend);
            if (hostPath is null) return new(false, null, false);
            var runtime = await Task.Run(() => _receiver.ProbeRuntime(selectedBackend));
            if (!runtime.Success)
            {
                Running = Ready = false;
                var error = WirelessReceiverService.DescribeProbeFailure(runtime);
                var isNewRuntimeError = !string.Equals(StartError, error,
                    StringComparison.Ordinal);
                StartError = error;
                _automaticStartNotBeforeUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                return new(false, error, isNewRuntimeError);
            }
            var sanitized = WirelessReceiverConfiguration.SanitizeReceiverName(
                receiverName ?? (automaticStart ? ReceiverName : AppliedReceiverName));
            if (receiverName is not null) ReceiverName = sanitized;
            var profile = displayProfile ?? (automaticStart ? SelectedProfile : AppliedProfile);
            var started = await Task.Run(() => core.StartWirelessReceiver(sanitized, hostPath,
                profile.Width, profile.Height, profile.FrameRate));
            Running = started.Success;
            Ready = false;
            if (started.Success)
            {
                Backend = selectedBackend;
                AppliedBackend = selectedBackend;
                _lastStartedBackend = selectedBackend;
                _lastReceiverStartUtc = DateTime.UtcNow;
                AppliedReceiverName = sanitized;
                AppliedProfile = profile;
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
            _automaticRestartBlocked = false;
            _lastReceiverStartUtc = default;
            Running = Ready = false;
            StartError = null;
            _lifecycleGate.Release();
        }
    }

    internal string GetStatusText()
    {
        if (!Running && !IsBackendAvailable(Backend))
            return LocalizationService.Format("WirelessBackendUnavailableFormat",
                WirelessReceiverConfiguration.GetBackendOption(Backend).Label);
        if (!Running && !string.IsNullOrWhiteSpace(StartError))
            return LocalizationService.Format("StartFailedFormat", StartError);
        if (!Running || !Ready) return LocalizationService.Get("WirelessStarting");
        return LocalizationService.Format("WirelessRunningWithBackendFormat",
            WirelessReceiverConfiguration.GetBackendOption(AppliedBackend).Label,
            AppliedReceiverName);
    }
}
