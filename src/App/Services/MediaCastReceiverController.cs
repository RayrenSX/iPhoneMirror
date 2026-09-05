using IPhoneMirror.App.Interop;
using IPhoneMirror.App.Localization;

namespace IPhoneMirror.App.Services;

internal sealed class MediaCastReceiverController(
    NativeCore core, WirelessReceiverService? receiver = null,
    Func<WirelessReceiverBackend>? selectedWirelessBackend = null)
{
    private readonly WirelessReceiverService _receiver = receiver ?? new();
    private readonly Func<WirelessReceiverBackend> _selectedWirelessBackend =
        selectedWirelessBackend ?? (() => WirelessReceiverBackend.Original);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _startError;
    private DateTime _automaticStartNotBeforeUtc;

    internal bool Running { get; private set; }
    internal bool Ready { get; private set; }
    internal bool SupportsCurrentWirelessBackend =>
        WirelessReceiverConfiguration.SupportsMediaCast(_selectedWirelessBackend());
    internal bool IsAvailable => SupportsCurrentWirelessBackend &&
        _receiver.IsBackendAvailable(WirelessReceiverBackend.Original);

    internal async Task<bool> EnsureStartedAsync()
    {
        if (!SupportsCurrentWirelessBackend)
        {
            if (Running || Ready) await StopAsync();
            return false;
        }
        await _gate.WaitAsync();
        try
        {
            if (!IsAvailable) return Running = Ready = false;
            var status = core.GetMediaCastReceiverStatus();
            Running = status.Running;
            Ready = status.Ready;
            if (Running) return true;
            if (DateTime.UtcNow < _automaticStartNotBeforeUtc) return false;
            if (_receiver.GetExecutablePath(WirelessReceiverBackend.Original) is not
                { } hostPath) return false;
            var runtime = await Task.Run(() =>
                _receiver.ProbeRuntime(WirelessReceiverBackend.Original));
            if (!runtime.Success)
            {
                Running = Ready = false;
                _startError = WirelessReceiverService.DescribeProbeFailure(runtime);
                _automaticStartNotBeforeUtc = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                return false;
            }
            var result = await Task.Run(() => core.StartMediaCastReceiver(
                WirelessReceiverConfiguration.DefaultReceiverName, hostPath));
            Running = result.Success;
            Ready = false;
            _startError = result.Success ? null : result.Message;
            return result.Success;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await Task.Run(core.StopMediaCastReceiver);
            Running = Ready = false;
            _startError = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal string GetStatusText()
    {
        if (!SupportsCurrentWirelessBackend)
            return LocalizationService.Get("MediaCastRequiresOriginalBackend");
        if (!IsAvailable) return LocalizationService.Get("MediaCastReceiverMissing");
        if (!Running && !string.IsNullOrWhiteSpace(_startError))
            return LocalizationService.Format("StartFailedFormat", _startError);
        return LocalizationService.Get(Ready ? "MediaCastReady" : "MediaCastStarting");
    }
}
